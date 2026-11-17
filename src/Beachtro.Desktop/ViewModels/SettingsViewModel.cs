using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Beachtro.Application.Configuration;
using Beachtro.Application.Receipts;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

/// <summary>
/// Settings tabs (IB-25): general (printer, shifts, sample data), business and tax/calendar profiles
/// (<see cref="ProfilesViewModel"/>), and backup and sync status.
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private string? _selectedPrinter;
    private PrintJobStatus? _testJob;
    private string _testPreview = string.Empty;
    private bool _requireShift;
    private string _syncSummary = string.Empty;
    private bool _destinationEnabled;

    public SettingsViewModel(ShellViewModel shell)
        : base(shell)
    {
        SavePrinterCommand = Command(_ => SavePrinterAsync(), _ => SelectedPrinter is not null);
        TestPrintCommand = Command(_ => TestPrintAsync());
        ConfirmTestCommand = Command(p => ConfirmAsync(p is true or "True"), _ => _testJob is { State: PrintJobState.SpoolAccepted });
        LoadSampleCommand = Command(LoadSampleAsync);
        BackupCommand = Command(_ => BackupAsync());
        SaveShiftPolicyCommand = Command(_ => SaveShiftPolicyAsync());
        RefreshSyncCommand = Command(_ => RefreshSyncAsync());
        RunSyncNowCommand = Command(_ => RunSyncNowAsync());
        RetryFailedCommand = Command(_ => RetryFailedAsync(), _ => CanRetrySync);
        ToggleDestinationCommand = Command(_ => ToggleDestinationAsync(), _ => CanConfigureSync);
        Profiles = new ProfilesViewModel(shell);
    }

    public override string Title => "Settings";

    public ObservableCollection<string> Printers { get; } = [];
    public ProfilesViewModel Profiles { get; }
    public bool CanViewProfiles => Services.Sessions.Current?.Has(Capabilities.SettingsOperational) ?? false;
    public bool CanSeeBackupTab => CanBackup || CanViewSync;
    public string? SelectedPrinter { get => _selectedPrinter; set => Set(ref _selectedPrinter, value); }
    public string TestPreview { get => _testPreview; private set => Set(ref _testPreview, value); }
    public bool IsTestProfile => Shell.IsTestProfile;
    public bool CanConfigurePrinter => Services.Sessions.Current?.Has(Capabilities.SettingsOperational) ?? false;
    public bool CanLoadSample => IsTestProfile && (Services.Sessions.Current?.Has(Capabilities.SettingsFinancial) ?? false);
    public bool CanBackup => Services.Sessions.Current?.Has(Capabilities.BackupCreateVerify) ?? false;
    public bool CanViewSync => Services.Sessions.Current?.Has(Capabilities.SyncView) ?? false;
    public bool CanRetrySync => Services.Sessions.Current?.Has(Capabilities.SyncRetry) ?? false;
    public bool CanConfigureSync => IsTestProfile && (Services.Sessions.Current?.Has(Capabilities.SyncConfigure) ?? false);

    public ObservableCollection<string> SyncProblems { get; } = [];
    public string SyncSummary { get => _syncSummary; private set => Set(ref _syncSummary, value); }

    public bool DestinationEnabled
    {
        get => _destinationEnabled;
        private set
        {
            if (Set(ref _destinationEnabled, value))
            {
                OnPropertyChanged(nameof(DestinationButtonText));
            }
        }
    }

    public string DestinationButtonText => DestinationEnabled ? "Turn off simulated destination" : "Turn on simulated destination";

    public ICommand SavePrinterCommand { get; }
    public ICommand TestPrintCommand { get; }
    public ICommand ConfirmTestCommand { get; }
    public ICommand LoadSampleCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand SaveShiftPolicyCommand { get; }
    public ICommand RefreshSyncCommand { get; }
    public ICommand RunSyncNowCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand ToggleDestinationCommand { get; }

    /// <summary>B-03: off by default until the business confirms its shift procedure (D-08).</summary>
    public bool RequireShift { get => _requireShift; set => Set(ref _requireShift, value); }

    public override async Task LoadAsync()
    {
        Printers.Clear();
        foreach (var p in Services.Printer.InstalledPrinters())
        {
            Printers.Add(p);
        }

        SelectedPrinter ??= Printers.FirstOrDefault(p => p.Contains("PDF", StringComparison.OrdinalIgnoreCase)) ?? Printers.FirstOrDefault();
        RequireShift = await Services.Shifts.IsShiftRequiredAsync();
        if (CanViewProfiles)
        {
            await Profiles.LoadAsync();
        }

        if (CanViewSync)
        {
            await RefreshSyncAsync();
        }
    }

    private async Task RefreshSyncAsync()
    {
        var result = await Services.Sync.GetStatusAsync(Session);
        if (!Check(result))
        {
            return;
        }

        var s = result.Value!;
        DestinationEnabled = s.DestinationEnabled;
        var b = s.Backup;
        var lines = new List<string>
        {
            "Destination: " + (s.Provider is null ? "none connected (files are kept on this PC)" : $"{s.DestinationLabel} — {(s.DestinationEnabled ? "on" : "off")}"),
            $"Files waiting to be written: {s.AwaitingFile}" + (s.HeldArtifacts > 0 ? $" · held for review: {s.HeldArtifacts}" : string.Empty),
            $"Uploads: {s.Pending} waiting, {s.InProgress} in progress, {s.Failed} failed, {s.RecoveryHold} on hold, {s.Succeeded} done"
                + (s.OtherDestination > 0 ? $" ({s.OtherDestination} belong to another destination)" : string.Empty),
            "Oldest waiting upload: " + (s.OldestPendingUtc is { } o ? Formatting.LocalTime(o) : "none")
                + " · Last successful upload: " + (s.LastSuccessUtc is { } l ? Formatting.LocalTime(l) : "never"),
            "Last verified backup: " + (b.LastVerifiedUtc is { } v ? $"{Formatting.LocalTime(v)} ({b.LastBackupFile})" : "none yet")
                + $" · {b.LocalGenerations} kept on this PC",
            Infrastructure.Runtime.DataFolderSecurity.Check(Services.Paths.Root) is { IsPrivate: true }
                ? $"Data folder: private to this Windows account ({Services.Paths.Root})"
                : "Data folder: NOT private — " + string.Join(" ", Infrastructure.Runtime.DataFolderSecurity.Check(Services.Paths.Root).Problems),
            b.FreeBytes < 0 ? "Free disk space: unknown" : $"Free disk space: {b.FreeBytes / (1024.0 * 1024 * 1024):0.0} GB" + (b.LowSpace ? " — LOW, free some space" : string.Empty),
        };
        foreach (var j in s.Jobs)
        {
            var name = j.Kind switch { "DailyBackup" => "Daily backup", "DailyReport" => "Daily report (business-day close)", "MonthlyReport" => "Monthly report", _ => j.Kind };
            lines.Add($"{name}: last done {j.LastCompletedKey ?? "never"}"
                + (j.WaitingKey is not null ? $"; {j.WaitingKey} is due and runs when an owner or authorized user is signed in" : string.Empty)
                + (j.LastError is not null ? $"; last error: {j.LastError}" : string.Empty));
        }

        SyncSummary = string.Join(Environment.NewLine, lines);
        SyncProblems.Clear();
        foreach (var p in s.Problems)
        {
            SyncProblems.Add($"{p.LogicalKey}: {p.State} — {p.Code}" + (p.Detail is null ? string.Empty : $" ({p.Detail})"));
        }
    }

    private async Task RunSyncNowAsync()
    {
        var cycle = await Services.Sync.RunCycleAsync();
        Info = cycle == Application.Sync.SyncCycleResult.Empty ? "Sync run: nothing new to write or upload." : $"Sync run: {cycle.Materialized} file(s) written, {cycle.Enqueued} queued, {cycle.Uploaded} uploaded"
            + (cycle.Retrying + cycle.Failed + cycle.RecoveryHeld + cycle.Held > 0 ? $", {cycle.Retrying} will retry, {cycle.Failed} failed, {cycle.RecoveryHeld + cycle.Held} held" : string.Empty) + ".";
        await RefreshSyncAsync();
    }

    private async Task RetryFailedAsync()
    {
        var result = await Services.Sync.RetryFailedAsync(Session);
        if (Check(result))
        {
            Info = result.Value == 0 ? "Nothing to retry." : $"{result.Value} failed upload(s) will be retried with the same file identity.";
            Services.Background.Nudge();
            await RefreshSyncAsync();
        }
    }

    private async Task ToggleDestinationAsync()
    {
        var enable = !DestinationEnabled;
        if (Check(await Services.Sync.SetSimulatedDestinationAsync(Session, enable)))
        {
            Info = enable
                ? "Simulated destination on: files are copied to the simulated-drive folder in the data folder. No Google account is used."
                : "Simulated destination off. Files keep being written on this PC and wait in the queue.";
            Services.Background.Nudge();
            await RefreshSyncAsync();
        }
    }

    private async Task SavePrinterAsync()
    {
        if (Check(await Services.Profiles.SetOperationalSettingAsync(Session, OperationalSettingKeys.PrinterName, JsonSerializer.Serialize(SelectedPrinter))))
        {
            Info = $"Receipt printer set to {SelectedPrinter}.";
        }
    }

    private async Task TestPrintAsync()
    {
        var result = await Services.Print.TestPrintAsync(Session);
        if (!Check(result))
        {
            return;
        }

        _testJob = result.Value;
        TestPreview = result.Value!.Document?.Text ?? string.Empty;
        Info = result.Value.State == PrintJobState.SpoolAccepted
            ? $"Test page sent to {result.Value.PrinterName}. Confirm whether it printed."
            : "Test page not printed: " + result.Value.LastError;
    }

    private async Task ConfirmAsync(bool printed)
    {
        if (_testJob is not null && Check(await Services.Print.ConfirmAsync(Session, _testJob.JobId, printed)))
        {
            Info = printed ? "Printer confirmed working." : "Recorded as not printed. Check the printer and try again.";
            _testJob = null;
        }
    }

    private async Task LoadSampleAsync(object? passwordBox)
    {
        var password = ShellViewModel.TakePassword(passwordBox);
        if (!Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password))))
        {
            return;
        }

        var result = await Services.Demo.LoadSampleDataAsync(Session);
        if (Check(result))
        {
            Info = $"Sample data loaded: {result.Value!.ProfilesCreated} profile(s), {result.Value.ProductsCreated} product(s). All marked TEST.";
            await Shell.EnterMainAsync();
        }
    }

    private async Task SaveShiftPolicyAsync()
    {
        if (Check(await Services.Profiles.SetOperationalSettingAsync(Session, OperationalSettingKeys.ShiftsRequired, RequireShift ? "true" : "false")))
        {
            Info = RequireShift ? "Cashiers must now open a shift before selling." : "Shifts are optional again; selling does not need an open shift.";
        }
    }

    private async Task BackupAsync()
    {
        var result = await Services.Backups.CreateBackupAsync(Session);
        if (Check(result))
        {
            Info = $"Backup verified and saved to {result.Value!.PackagePath}"
                + (result.Value.Pruned > 0 ? $" ({result.Value.Pruned} older backup(s) removed; at least seven are kept)." : ".");
            if (CanViewSync)
            {
                await RefreshSyncAsync();
            }
        }
    }
}
