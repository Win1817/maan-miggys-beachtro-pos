using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Receipts;
using Beachtro.Application.Recovery;
using Beachtro.Application.Reports;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Application.Shifts;
using Beachtro.Application.Sync;
using Beachtro.Desktop.Mvvm;
using Beachtro.Infrastructure.Runtime;

namespace Beachtro.Desktop.ViewModels;

/// <summary>Application services the screens use. View models never touch the database directly (T-19).</summary>
public sealed record PosServices(
    IIdentityService Identity,
    IProfileService Profiles,
    ICatalogService Catalog,
    ICheckoutService Checkout,
    ICorrectionService Corrections,
    IOrderQueryService Orders,
    IPrintService Print,
    IReceiptPrinter Printer,
    IBackupService Backups,
    IRecoveryService Recovery,
    INumberSeriesService Numbers,
    IDemoDataService Demo,
    IShiftService Shifts,
    IReportService Reports,
    ISyncService Sync,
    BackgroundWork Background,
    SessionManager Sessions,
    IClock Clock,
    LocalDataPaths Paths);

/// <summary>Base for signed-in pages: busy/error/info state and uniform handling of session problems.</summary>
public abstract class PageViewModel(ShellViewModel shell) : ObservableObject
{
    private string? _error;
    private string? _info;
    private bool _busy;

    public ShellViewModel Shell { get; } = shell;
    public PosServices Services => Shell.Services;
    public UserSession Session => Shell.Services.Sessions.Current ?? throw new InvalidOperationException("No session.");

    public abstract string Title { get; }

    public string? Error { get => _error; set => Set(ref _error, value); }
    public string? Info { get => _info; set => Set(ref _info, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Today's business date under the calendar profile in effect (falls back to the PC date).</summary>
    protected async Task<string> TodayAsync()
    {
        var now = Services.Clock.UtcNowMilliseconds;
        var calendar = await Services.Profiles.GetEffectiveCalendarProfileAsync(now);
        return calendar.IsSuccess
            ? calendar.Value!.Policy.BusinessDateFor(now)
            : DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True on success; otherwise shows a safe message and reacts to locked/ended sessions.</summary>
    protected bool Check<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            Error = null;
            return true;
        }

        Error = Formatting.Describe(result);
        if (result.Status == ResultStatus.Unauthenticated)
        {
            Shell.OnSessionProblem();
        }

        return false;
    }

    protected void ShowException(Exception ex)
    {
        Error = "Something went wrong. Details were written to the log.";
        Serilog.Log.Error(ex, "Unhandled error on {Page}", Title);
    }

    protected AsyncCommand Command(Func<object?, Task> run, Func<object?, bool>? canRun = null) =>
        new(async p =>
        {
            Busy = true;
            try
            {
                await run(p);
            }
            finally
            {
                Busy = false;
            }
        }, canRun, ShowException);
}
