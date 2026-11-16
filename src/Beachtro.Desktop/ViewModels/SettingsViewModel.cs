using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Beachtro.Application.Configuration;
using Beachtro.Application.Receipts;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

/// <summary>
/// Printer, sample data, backup and a read-only view of the effective profiles and production gate.
/// Full settings/profile editing is IB-25.
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private string? _selectedPrinter;
    private PrintJobStatus? _testJob;
    private string _profileSummary = string.Empty;
    private string _testPreview = string.Empty;

    public SettingsViewModel(ShellViewModel shell)
        : base(shell)
    {
        SavePrinterCommand = Command(_ => SavePrinterAsync(), _ => SelectedPrinter is not null);
        TestPrintCommand = Command(_ => TestPrintAsync());
        ConfirmTestCommand = Command(p => ConfirmAsync(p is true or "True"), _ => _testJob is { State: PrintJobState.SpoolAccepted });
        LoadSampleCommand = Command(LoadSampleAsync);
        BackupCommand = Command(_ => BackupAsync());
    }

    public override string Title => "Settings";

    public ObservableCollection<string> Printers { get; } = [];
    public ObservableCollection<string> Blockers { get; } = [];
    public string? SelectedPrinter { get => _selectedPrinter; set => Set(ref _selectedPrinter, value); }
    public string ProfileSummary { get => _profileSummary; private set => Set(ref _profileSummary, value); }
    public string TestPreview { get => _testPreview; private set => Set(ref _testPreview, value); }
    public bool IsTestProfile => Shell.IsTestProfile;
    public bool CanConfigurePrinter => Services.Sessions.Current?.Has(Capabilities.SettingsOperational) ?? false;
    public bool CanLoadSample => IsTestProfile && (Services.Sessions.Current?.Has(Capabilities.SettingsFinancial) ?? false);
    public bool CanBackup => Services.Sessions.Current?.Has(Capabilities.BackupCreateVerify) ?? false;

    public ICommand SavePrinterCommand { get; }
    public ICommand TestPrintCommand { get; }
    public ICommand ConfirmTestCommand { get; }
    public ICommand LoadSampleCommand { get; }
    public ICommand BackupCommand { get; }

    public override async Task LoadAsync()
    {
        Printers.Clear();
        foreach (var p in Services.Printer.InstalledPrinters())
        {
            Printers.Add(p);
        }

        SelectedPrinter ??= Printers.FirstOrDefault(p => p.Contains("PDF", StringComparison.OrdinalIgnoreCase)) ?? Printers.FirstOrDefault();
        await RefreshProfilesAsync();
    }

    private async Task RefreshProfilesAsync()
    {
        var now = Services.Clock.UtcNowMilliseconds;
        var calc = await Services.Profiles.GetEffectiveCalculationProfileAsync(now);
        var calendar = await Services.Profiles.GetEffectiveCalendarProfileAsync(now);
        var lines = new List<string>
        {
            calc.IsSuccess
                ? $"Calculation profile: revision {calc.Value!.Profile.Revision} ({calc.Value.Profile.ApprovalState}), {calc.Value.Profile.Currency.Code}, tax entries: "
                  + string.Join(", ", calc.Value.TaxRules.Select(r => $"{r.Key} = {r.Value.Treatment}{(r.Value.Components.Count > 0 ? $" {r.Value.Components[0].RatePpm / 10_000m:0.##}%" : string.Empty)}"))
                : "Calculation profile: none in effect",
            calendar.IsSuccess
                ? $"Calendar: {calendar.Value!.Policy.TimeZoneId}, business day starts {TimeSpan.FromMinutes(calendar.Value.Policy.CutoffMinute):hh\\:mm} ({calendar.Value.ApprovalState})"
                : "Calendar: none in effect",
        };
        ProfileSummary = string.Join(Environment.NewLine, lines);

        Blockers.Clear();
        foreach (var b in (await Services.Profiles.GetProductionReadinessAsync()).Blockers)
        {
            Blockers.Add(b);
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
            await RefreshProfilesAsync();
            await Shell.EnterMainAsync();
        }
    }

    private async Task BackupAsync()
    {
        var result = await Services.Backups.CreateBackupAsync(Session);
        if (Check(result))
        {
            Info = $"Backup verified and saved to {result.Value!.Folder}";
        }
    }
}
