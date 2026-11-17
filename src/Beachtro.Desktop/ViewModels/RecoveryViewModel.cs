using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Beachtro.Application.Recovery;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;

namespace Beachtro.Desktop.ViewModels;

/// <summary>A number series on hold: the owner enters the highest number known to be used, with evidence.</summary>
public sealed class SeriesRow(SeriesHold hold) : ObservableObject
{
    private string _provenText = hold.LastReservedSequence.ToString(CultureInfo.InvariantCulture);
    private string _evidence = string.Empty;

    public SeriesHold Hold { get; } = hold;
    public string Title => $"{Hold.SeriesKey} — highest number recorded here: {Hold.LastReservedSequence}";
    public string Why => Hold.Reason;
    public string ProvenText { get => _provenText; set => Set(ref _provenText, value); }
    public string Evidence { get => _evidence; set => Set(ref _evidence, value); }
}

/// <summary>A prepared checkout or refund from the restored data, closed with what really happened.</summary>
public sealed class PreparedRow(PreparedRequestView view) : ObservableObject
{
    private string _reason = string.Empty;

    public PreparedRequestView View { get; } = view;

    public string Title => $"{(View.IsRefund ? "Refund" : "Sale")} prepared {Formatting.LocalTime(View.PreparedUtc)} by {View.ActorName}"
        + (View.TotalMinor is { } t ? $" — total {Formatting.Money(t)}" : string.Empty)
        + (View.HasPaymentEvidence ? " — payment details were entered" : string.Empty);

    public string Reason { get => _reason; set => Set(ref _reason, value); }
}

/// <summary>
/// The restore runbook on one page (IB-24, POS-DEC-010): restore a backup, prove number series, close
/// unfinished payments, release held uploads, reopen, then take a fresh backup. Sensitive actions use
/// one password confirmation that stays valid for five minutes (the services check it themselves).
/// </summary>
public sealed class RecoveryViewModel : PageViewModel
{
    private RecoveryStatus? _status;
    private string _statusText = string.Empty;
    private string _packagePath = string.Empty;
    private string _candidateReport = string.Empty;
    private string _restoreReason = string.Empty;
    private string _releaseEvidence = string.Empty;
    private string _reopenNotes = string.Empty;
    private string _confirmedNote = string.Empty;
    private int _heldUploads;

    public RecoveryViewModel(ShellViewModel shell)
        : base(shell)
    {
        ConfirmCommand = Command(ConfirmAsync);
        BrowseCommand = new RelayCommand(_ => Browse());
        CheckBackupCommand = Command(_ => CheckBackupAsync(), _ => PackagePath.Length > 0);
        RestoreCommand = Command(_ => RestoreAsync(), _ => PackagePath.Length > 0);
        ResolveSeriesCommand = Command(p => ResolveSeriesAsync((SeriesRow)p!), p => p is SeriesRow);
        ClosePreparedCommand = Command(p => ClosePreparedAsync((PreparedRow)p!), p => p is PreparedRow);
        ReleaseUploadsCommand = Command(_ => ReleaseUploadsAsync(), _ => HeldUploads > 0);
        ReopenCommand = Command(_ => ReopenAsync(), _ => InRecovery);
    }

    public override string Title => "Recovery";

    public ObservableCollection<SeriesRow> Series { get; } = [];
    public ObservableCollection<PreparedRow> Prepared { get; } = [];
    public ObservableCollection<string> Blockers { get; } = [];

    public bool InRecovery => _status?.InRecovery ?? false;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string PackagePath { get => _packagePath; set => Set(ref _packagePath, value?.Trim() ?? string.Empty); }
    public string CandidateReport { get => _candidateReport; private set => Set(ref _candidateReport, value); }
    public string RestoreReason { get => _restoreReason; set => Set(ref _restoreReason, value); }
    public string ReleaseEvidence { get => _releaseEvidence; set => Set(ref _releaseEvidence, value); }
    public string ReopenNotes { get => _reopenNotes; set => Set(ref _reopenNotes, value); }
    public string ConfirmedNote { get => _confirmedNote; private set => Set(ref _confirmedNote, value); }
    public int HeldUploads { get => _heldUploads; private set { if (Set(ref _heldUploads, value)) { OnPropertyChanged(nameof(HeldText)); } } }

    public string HeldText => HeldUploads == 0
        ? "No uploads are on hold."
        : $"{HeldUploads} upload(s) on hold: the PC cannot tell whether they were already uploaded (for example by the lost PC). "
          + "Checking looks for each file on the destination: an identical copy is adopted, a different one stays on hold, and if none is found the file is uploaded normally.";

    public bool CanConfigureSync => Services.Sessions.Current?.Has(Capabilities.SyncConfigure) ?? false;

    public ICommand ConfirmCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand CheckBackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ResolveSeriesCommand { get; }
    public ICommand ClosePreparedCommand { get; }
    public ICommand ReleaseUploadsCommand { get; }
    public ICommand ReopenCommand { get; }

    public override Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        var result = await Services.Recovery.GetStatusAsync(Session);
        if (!Check(result))
        {
            return;
        }

        _status = result.Value!;
        OnPropertyChanged(nameof(InRecovery));
        var run = _status.LatestRun;
        StatusText = (_status.InRecovery
                ? "RECOVERY IN PROGRESS — selling is paused until every item below is resolved and the installation is reopened."
                : "Normal operation. Restore a backup only if the database is lost or damaged.")
            + (run is null ? string.Empty
                : Environment.NewLine + $"Latest restore: {Formatting.LocalTime(run.StartedUtc)} ({run.Disposition}); last sale in that backup: {run.LastSaleNumber ?? "none"}; "
                  + $"the previous database was kept in {run.PreservedPath}.");

        Blockers.Clear();
        foreach (var b in _status.ReopenBlockers)
        {
            Blockers.Add(b);
        }

        Series.Clear();
        foreach (var s in _status.BlockedSeries)
        {
            Series.Add(new SeriesRow(s));
        }

        Prepared.Clear();
        foreach (var p in _status.PreparedRequests)
        {
            Prepared.Add(new PreparedRow(p));
        }

        HeldUploads = _status.HeldUploads;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ConfirmAsync(object? passwordBox)
    {
        var password = ShellViewModel.TakePassword(passwordBox);
        if (Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password))))
        {
            ConfirmedNote = $"Confirmed. Recovery actions are allowed until {DateTime.Now.AddMinutes(5):HH:mm}.";
        }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a backup package",
            Filter = "Backup package (*.zip)|*.zip",
            InitialDirectory = Services.Paths.Backups,
        };
        if (dialog.ShowDialog() == true)
        {
            PackagePath = dialog.FileName;
            CandidateReport = string.Empty;
        }
    }

    private async Task CheckBackupAsync()
    {
        var result = await Task.Run(() => Services.Backups.ValidateCandidateAsync(Session, PackagePath));
        if (!Check(result))
        {
            return;
        }

        var r = result.Value!;
        CandidateReport = r.IsRestorable
            ? $"This backup can be restored. Made {Formatting.LocalTime(r.CreatedUtc)}; last sale in it: {r.LastSaleNumber ?? "none"}. Anything sold after that is not in it."
            : "This backup cannot be restored: " + string.Join(" ", r.Problems);
    }

    private async Task RestoreAsync()
    {
        var result = await Task.Run(() => Services.Backups.RestoreAsync(Session, PackagePath, RestoreReason));
        if (!Check(result))
        {
            return;
        }

        var r = result.Value!;
        Info = $"Backup restored. The previous database was kept in {r.PreservedFolder}. "
            + $"{r.BlockedSeries.Count} number series and {r.UnresolvedPreparedRequests} unfinished payment(s) need your decision below.";
        RestoreReason = string.Empty;
        Services.Background.Nudge();
        await RefreshAsync();
        await Shell.RefreshBusinessDateAsync();
    }

    private async Task ResolveSeriesAsync(SeriesRow row)
    {
        if (!int.TryParse(row.ProvenText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var proven))
        {
            Error = $"{row.Hold.SeriesKey}: enter the highest number known to be used as a whole number (0 if nothing was sold).";
            return;
        }

        if (Check(await Services.Numbers.ResolveHoldAsync(Session, row.Hold.SeriesKey, proven, row.Evidence)))
        {
            Info = $"{row.Hold.SeriesKey} reopened above {proven}. The next sale on that date gets {proven + 1}.";
            await RefreshAsync();
        }
    }

    private async Task ClosePreparedAsync(PreparedRow row)
    {
        if (Check(await Services.Recovery.ClosePreparedRequestAsync(Session, row.View.RequestId, row.Reason)))
        {
            Info = "Unfinished payment closed. Your reason is in the audit log.";
            await RefreshAsync();
        }
    }

    private async Task ReleaseUploadsAsync()
    {
        var result = await Task.Run(() => Services.Sync.ReleaseHeldUploadsAsync(Session, ReleaseEvidence));
        if (!Check(result))
        {
            return;
        }

        var r = result.Value!;
        Info = $"Checked the destination: {r.Adopted} already there (kept, not uploaded again), {r.Released} released for upload, {r.StillHeld} still on hold because a different file is there.";
        ReleaseEvidence = string.Empty;
        Services.Background.Nudge();
        await RefreshAsync();
    }

    private async Task ReopenAsync()
    {
        if (!Check(await Services.Backups.ReopenAsync(Session, ReopenNotes)))
        {
            return;
        }

        ReopenNotes = string.Empty;
        var backup = await Task.Run(() => Services.Backups.CreateBackupAsync(Session));
        Info = backup.IsSuccess
            ? $"Reopened: selling is allowed again. A fresh verified backup of the recovered data was saved to {backup.Value!.PackagePath}."
            : "Reopened: selling is allowed again. The fresh backup did not complete (" + Formatting.Describe(backup) + "); run one from Settings.";
        Services.Background.Nudge();
        await RefreshAsync();
    }
}
