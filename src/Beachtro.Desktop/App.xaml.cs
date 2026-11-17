using System.Windows;
using Beachtro.Application.Runtime;
using System.IO;
using Beachtro.Infrastructure;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Beachtro.Desktop;

/// <summary>
/// Composition root. Startup order follows system design §Runtime: resolve profile → data
/// directory → single-instance lock → logging → host → database (Test profile: migrate and verify
/// pragmas/integrity) → UI.
/// </summary>
public partial class App
{
    private IHost? _host;
    private SingleInstanceLock? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RuntimeProfile profile;
        try
        {
            profile = RuntimeProfilePolicy.Parse(e.Args);
            RuntimeProfilePolicy.EnsureAllowed(profile);
        }
        catch (ProfileNotAllowedException ex)
        {
            Fail(ex.Message);
            return;
        }

        // "--data-root <folder>" points a Test installation at another folder (testing and demos). Never honoured in Production.
        var rootIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--data-root", StringComparison.OrdinalIgnoreCase));
        var paths = profile == RuntimeProfile.Test && rootIndex >= 0 && rootIndex + 1 < e.Args.Length
            ? new LocalDataPaths(e.Args[rootIndex + 1], profile)
            : LocalDataPaths.ForProfile(profile);
        paths.EnsureCreated();

        _instanceLock = SingleInstanceLock.TryAcquire(paths);
        if (_instanceLock is null)
        {
            Fail("Beachtro POS is already running for this data folder.");
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // EF Core logs SQL at Information; keep commands and parameters out of logs (security design §Audit/privacy).
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.Logs, "pos-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = profile.ToString(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSerilog();
        builder.Services.AddBeachtroInfrastructure(paths);
        builder.Services.AddSingleton<Beachtro.Application.Receipts.IReceiptPrinter, Printing.WindowsReceiptPrinter>();
        builder.Services.AddSingleton<BackgroundWork>();
        builder.Services.AddSingleton<ViewModels.PosServices>();
        builder.Services.AddSingleton<ViewModels.ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();

        Log.Information("Started in {Profile} profile; data root {Root}", profile, paths.Root);

        try
        {
            var pragmas = _host.Services.GetRequiredService<DatabaseInitializer>().Initialize(profile);
            Log.Information("Database ready: {Pragmas}", pragmas);

            // Reconcile number series with the external journal before any sale can start.
            var numbers = _host.Services.GetRequiredService<Beachtro.Application.Recovery.INumberSeriesService>()
                .ReconcileAsync().GetAwaiter().GetResult();
            Log.Information("Number series reconciled: {Blocked} on hold, journal repaired: {Repaired}", numbers.Blocked.Count, numbers.JournalRepaired);

            // Receipts left mid-submission by a crash become OutcomeUnknown; they are never resent silently.
            var interrupted = _host.Services.GetRequiredService<Beachtro.Application.Receipts.IPrintService>()
                .RecoverInterruptedAsync().GetAwaiter().GetResult();
            Log.Information("Print jobs needing operator check after restart: {Count}", interrupted);
        }
        catch (Exception ex) when (ex is DatabaseNotReadyException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Never fall back to a fresh empty ledger; stop and leave the data for diagnosis.
            Log.Fatal(ex, "Database initialization failed");
            Fail("The local database could not be opened safely. Details are in the log file.");
            return;
        }

        // Artifact files, uploads and scheduled jobs run in the background; checkout never waits for them.
        _host.Services.GetRequiredService<BackgroundWork>().Start();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.Services.GetRequiredService<BackgroundWork>().StopAsync().GetAwaiter().GetResult();
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        finally
        {
            Log.CloseAndFlush();
            _instanceLock?.Dispose();
            base.OnExit(e);
        }
    }

    private void Fail(string message)
    {
        MessageBox.Show(message, "Beachtro POS", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown(1);
    }
}
