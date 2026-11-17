using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Beachtro.Application.Configuration;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;

namespace Beachtro.Desktop.ViewModels;

/// <summary>
/// Business and calendar profiles plus the no-tax status (IB-25; POS-DEC-008/009): new revisions with
/// start time, approval state and evidence; the profiles in effect at any chosen time; and the revision
/// history. The business charges no tax, so there is no tax editor, only a one-step switch to no tax for
/// data created before that decision. While this build is Test-only, the screens offer Test and Draft.
/// </summary>
public sealed class ProfilesViewModel : PageViewModel
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private CalendarPolicy _zone = CalendarPolicy.ManilaMidnight;
    private ProfileHistory? _history;

    private string _taxStatus = string.Empty;
    private bool _needsNoTaxSwitch;

    // Calendar draft
    private ProfileApprovalState _calState = ProfileApprovalState.Test;
    private bool _calStartNow = true;
    private string _calStartText = string.Empty;
    private string _calEvidence = string.Empty;
    private string _timeZoneText = CalendarPolicy.ManilaMidnight.TimeZoneId;
    private string _cutoffText = "00:00";

    // Business draft
    private ProfileApprovalState _bizState = ProfileApprovalState.Test;
    private string _bizName = string.Empty;
    private string _bizAddress = string.Empty;
    private string _bizContact = string.Empty;
    private string _bizTaxId = string.Empty;
    private string _bizEvidence = string.Empty;

    private string _atText = string.Empty;
    private string _effectiveAt = string.Empty;
    private string _currentBusiness = string.Empty;

    public ProfilesViewModel(ShellViewModel shell)
        : base(shell)
    {
        SwitchToNoTaxCommand = Command(SwitchToNoTaxAsync, _ => CanEditFinancial && NeedsNoTaxSwitch);
        SaveCalendarCommand = Command(SaveCalendarAsync, _ => CanEditFinancial);
        SaveBusinessCommand = Command(SaveBusinessAsync, _ => CanEditFinancial);
        ShowEffectiveCommand = Command(_ => ShowEffectiveAsync());
    }

    public override string Title => "Profiles";

    /// <summary>Approved is set at production setup (milestone 13); not offered while the build is Test-only.</summary>
    public static IReadOnlyList<ProfileApprovalState> States { get; } = [ProfileApprovalState.Test, ProfileApprovalState.Draft];

    public bool CanEditFinancial => Services.Sessions.Current?.Has(Capabilities.SettingsFinancial) ?? false;
    public string ZoneNote => $"Times are in the business time zone ({_zone.TimeZoneId}), written as {TimeFormat}.";

    public ObservableCollection<string> CalculationHistory { get; } = [];
    public ObservableCollection<string> CalendarHistory { get; } = [];
    public ObservableCollection<string> BusinessHistory { get; } = [];
    public ObservableCollection<string> Blockers { get; } = [];

    public string TaxStatus { get => _taxStatus; private set => Set(ref _taxStatus, value); }
    public bool NeedsNoTaxSwitch { get => _needsNoTaxSwitch; private set => Set(ref _needsNoTaxSwitch, value); }

    public ProfileApprovalState CalState { get => _calState; set => Set(ref _calState, value); }
    public bool CalStartNow { get => _calStartNow; set => Set(ref _calStartNow, value); }
    public string CalStartText { get => _calStartText; set => Set(ref _calStartText, value); }
    public string CalEvidence { get => _calEvidence; set => Set(ref _calEvidence, value); }
    public string TimeZoneText { get => _timeZoneText; set => Set(ref _timeZoneText, value); }
    public string CutoffText { get => _cutoffText; set => Set(ref _cutoffText, value); }

    public ProfileApprovalState BizState { get => _bizState; set => Set(ref _bizState, value); }
    public string BizName { get => _bizName; set => Set(ref _bizName, value); }
    public string BizAddress { get => _bizAddress; set => Set(ref _bizAddress, value); }
    public string BizContact { get => _bizContact; set => Set(ref _bizContact, value); }
    public string BizTaxId { get => _bizTaxId; set => Set(ref _bizTaxId, value); }
    public string BizEvidence { get => _bizEvidence; set => Set(ref _bizEvidence, value); }

    public string AtText { get => _atText; set => Set(ref _atText, value); }
    public string EffectiveAt { get => _effectiveAt; private set => Set(ref _effectiveAt, value); }
    public string CurrentBusiness { get => _currentBusiness; private set => Set(ref _currentBusiness, value); }

    public ICommand SwitchToNoTaxCommand { get; }
    public ICommand SaveCalendarCommand { get; }
    public ICommand SaveBusinessCommand { get; }
    public ICommand ShowEffectiveCommand { get; }

    public override async Task LoadAsync()
    {
        var now = Services.Clock.UtcNowMilliseconds;
        var calendar = await Services.Profiles.GetEffectiveCalendarProfileAsync(now);
        _zone = calendar.IsSuccess ? calendar.Value!.Policy : CalendarPolicy.ManilaMidnight;
        OnPropertyChanged(nameof(ZoneNote));
        AtText = _zone.LocalTimeText(now);
        var later = _zone.LocalTimeText(now + (24 * 3600 * 1000L));
        CalStartText = later;

        if (!await RefreshHistoryAsync())
        {
            return;
        }

        if (calendar.IsSuccess)
        {
            TimeZoneText = calendar.Value!.Policy.TimeZoneId;
            CutoffText = TimeSpan.FromMinutes(calendar.Value.Policy.CutoffMinute).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        if (_history!.CurrentBusiness is { } b)
        {
            BizName = b.RegisteredName;
            BizAddress = b.Address;
            BizContact = b.Contact ?? string.Empty;
            BizTaxId = b.TaxIdentifier ?? string.Empty;
        }

        await ShowEffectiveAsync();
    }

    private static string Percent(int ppm) => (ppm / 10_000m).ToString("0.####", CultureInfo.InvariantCulture);

    private async Task<bool> RefreshHistoryAsync()
    {
        var result = await Services.Profiles.GetProfileHistoryAsync(Session);
        if (!Check(result))
        {
            return false;
        }

        _history = result.Value!;
        var now = Services.Clock.UtcNowMilliseconds;
        Fill(CalculationHistory, _history.Calculation.Select(r => Describe(r, now) + Environment.NewLine + "   " + TaxText(r.Taxes)));
        Fill(CalendarHistory, _history.Calendar.Select(r => Describe(r, now) + Environment.NewLine
            + $"   {r.TimeZoneId}, business day starts {TimeSpan.FromMinutes(r.CutoffMinute ?? 0):hh\\:mm}"));
        Fill(BusinessHistory, _history.Business.Select(b =>
            $"Revision {b.Revision} · {b.ApprovalState}{(b.IsCurrent ? " · CURRENT" : string.Empty)} · saved {_zone.LocalTimeText(b.CreatedUtc)}"
            + Environment.NewLine + $"   {b.RegisteredName}, {b.Address}"
            + (b.EvidenceReference is null ? string.Empty : $" · evidence: {b.EvidenceReference}")));
        CurrentBusiness = _history.CurrentBusiness is { } c
            ? $"Current: revision {c.Revision} ({c.ApprovalState}) — {c.RegisteredName}"
            : "No business profile yet. Receipts need one before the first sale.";

        var calc = await Services.Profiles.GetEffectiveCalculationProfileAsync(now);
        NeedsNoTaxSwitch = !calc.IsSuccess || !calc.Value!.IsNoTax;
        TaxStatus = !calc.IsSuccess
            ? "No calculation profile is in effect yet, so nothing can be sold. Switch to no tax (or load sample data) to start."
            : calc.Value!.IsNoTax
                ? $"No tax is charged. Prices are final (revision {calc.Value.Profile.Revision}, {calc.Value.Profile.ApprovalState})."
                : $"The profile in effect (revision {calc.Value.Profile.Revision}) still computes tax: {TaxText(_history.Calculation.FirstOrDefault(r => r.Id == calc.Value.Profile.Id)?.Taxes ?? [])}. "
                  + "This POS charges no tax; switch to no tax. Existing products keep working and past sales are not changed.";

        Blockers.Clear();
        foreach (var b in (await Services.Profiles.GetProductionReadinessAsync()).Blockers)
        {
            Blockers.Add(b);
        }

        return true;
    }

    private string Describe(ProfileRevisionView r, long now)
    {
        var status = r.ApprovalState == ProfileApprovalState.Draft ? "draft, never used"
            : r.EffectiveFromUtc > now ? "SCHEDULED"
            : r.EffectiveToUtc is { } to && to <= now ? "ended"
            : "IN EFFECT NOW";
        var range = $"{_zone.LocalTimeText(r.EffectiveFromUtc)} → {(r.EffectiveToUtc is { } end ? _zone.LocalTimeText(end) : "open")}";
        return $"Revision {r.Revision} · {r.ApprovalState} · {status} · {range}"
            + (r.EvidenceReference is null ? string.Empty : $" · evidence: {r.EvidenceReference}")
            + $" · checksum {r.PayloadSha256[..12]}";
    }

    private static string TaxText(IReadOnlyList<TaxConfigurationDraft> taxes) =>
        taxes.Count == 0 ? "no tax entries" : string.Join("; ", taxes.Select(t =>
            t.Treatment == TreatmentCode.NotTaxed ? $"{t.TaxKey}: no tax"
            : $"{t.TaxKey}: {t.Treatment}{(t.Treatment == TreatmentCode.Taxable ? $" {Percent(t.RatePpm)}%" : string.Empty)}, {t.PriceMode.ToString().ToLowerInvariant()}"));

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> lines)
    {
        target.Clear();
        foreach (var l in lines)
        {
            target.Add(l);
        }
    }

    private bool TryParseTime(bool now, string text, string what, out long utc)
    {
        utc = Services.Clock.UtcNowMilliseconds;
        if (now)
        {
            return true;
        }

        if (!DateTime.TryParseExact(text?.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            Error = $"{what}: write the time as {TimeFormat}, for example {_zone.LocalTimeText(utc)}.";
            return false;
        }

        try
        {
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone.TimeZone)).ToUnixTimeMilliseconds();
            return true;
        }
        catch (ArgumentException)
        {
            Error = $"{what}: that local time does not exist in {_zone.TimeZoneId}.";
            return false;
        }
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task ShowEffectiveAsync()
    {
        if (!TryParseTime(false, AtText, "Show profiles in effect at", out var at))
        {
            return;
        }

        // The box shows whole minutes; the current minute means "now", including a revision started seconds ago.
        var now = Services.Clock.UtcNowMilliseconds;
        if (at <= now && now < at + 60_000)
        {
            at = now;
        }

        var calc = await Services.Profiles.GetEffectiveCalculationProfileAsync(at);
        var cal = await Services.Profiles.GetEffectiveCalendarProfileAsync(at);
        var lines = new List<string>
        {
            calc.IsSuccess
                ? $"Pricing: revision {calc.Value!.Profile.Revision} ({calc.Value.Profile.ApprovalState}), {calc.Value.Profile.Currency.Code} — "
                  + (calc.Value.IsNoTax ? "no tax, prices are final" : TaxText(_history?.Calculation.FirstOrDefault(r => r.Id == calc.Value.Profile.Id)?.Taxes ?? []))
                : "Pricing: no calculation profile in effect — nothing can be sold at this time.",
            cal.IsSuccess
                ? $"Calendar: revision {cal.Value!.Revision} ({cal.Value.ApprovalState}) — {cal.Value.Policy.TimeZoneId}, business day starts "
                  + $"{TimeSpan.FromMinutes(cal.Value.Policy.CutoffMinute):hh\\:mm}; that moment falls on business date {cal.Value.Policy.BusinessDateFor(at)}"
                : "Calendar: no calendar profile in effect.",
        };
        EffectiveAt = string.Join(Environment.NewLine, lines);
        Error = null;
    }

    private async Task<bool> ConfirmAsync(object? passwordBox)
    {
        var password = ShellViewModel.TakePassword(passwordBox);
        if (string.IsNullOrEmpty(password))
        {
            Error = "Confirm with your password.";
            return false;
        }

        return Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password)));
    }

    private async Task SwitchToNoTaxAsync(object? passwordBox)
    {
        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Profiles.ApplyNoTaxPolicyAsync(Session, null)))
        {
            return;
        }

        Info = "Switched to no tax from now on. Existing products keep selling at the same prices with no tax; earlier sales keep the revision they were made under.";
        await AfterSaveAsync();
    }

    private async Task SaveCalendarAsync(object? passwordBox)
    {
        if (!TryParseTime(CalStartNow, CalStartText, "Starts", out var from))
        {
            ShellViewModel.TakePassword(passwordBox);
            return;
        }

        if (!TimeSpan.TryParseExact(CutoffText?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var cutoff))
        {
            Error = "Write the business-day start as HH:mm, for example 00:00 or 04:00.";
            ShellViewModel.TakePassword(passwordBox);
            return;
        }

        var draft = new CalendarProfileDraft(CalState, from, TimeZoneText?.Trim() ?? string.Empty, (int)cutoff.TotalMinutes, Blank(CalEvidence));
        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Profiles.CreateCalendarProfileAsync(Session, draft)))
        {
            return;
        }

        Info = draft.ApprovalState == ProfileApprovalState.Draft
            ? "Draft calendar revision saved. Drafts are never used."
            : $"Calendar revision saved; it applies from {_zone.LocalTimeText(from)}. Closed business days are not changed.";
        CalEvidence = string.Empty;
        await AfterSaveAsync();
    }

    private async Task SaveBusinessAsync(object? passwordBox)
    {
        var draft = new BusinessProfileDraft(BizState, BizName?.Trim() ?? string.Empty, BizAddress?.Trim() ?? string.Empty, Blank(BizContact), Blank(BizTaxId), 1, Blank(BizEvidence));
        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Profiles.SaveBusinessProfileAsync(Session, draft)))
        {
            return;
        }

        Info = draft.ApprovalState == ProfileApprovalState.Draft
            ? "Draft business profile saved. Receipts keep using the current one."
            : "Business profile saved. New receipts use it; printed and stored receipts keep the details they were issued with.";
        BizEvidence = string.Empty;
        await AfterSaveAsync();
    }

    private async Task AfterSaveAsync()
    {
        await RefreshHistoryAsync();
        await ShowEffectiveAsync();
        await Shell.RefreshBusinessDateAsync();
    }
}
