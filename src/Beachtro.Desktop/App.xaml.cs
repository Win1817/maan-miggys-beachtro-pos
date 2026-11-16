using System.Windows;
using Beachtro.Application.Runtime;
using Beachtro.Infrastructure;
using Beachtro.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Beachtro.Desktop;

/// <summary>
/// Composition root. Startup order follows system design §Runtime: resolve profile → data
/// directory → single-instance lock → logging → host → UI. Schema verification/migration is
/// added with the database milestone.
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

        var paths = LocalDataPaths.ForProfile(profile);
        paths.EnsureCreated();

        _instanceLock = SingleInstanceLock.TryAcquire(paths);
        if (_instanceLock is null)
        {
            Fail("Beachtro POS is already running for this data folder.");
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.Logs, "pos-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSerilog();
        builder.Services.AddBeachtroInfrastructure(paths);
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();

        Log.Information("Started in {Profile} profile; data root {Root}", profile, paths.Root);

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
