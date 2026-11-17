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

/// <summary>One editable tax entry of a calculation profile draft.</summary>
public sealed class TaxRowViewModel : ObservableObject
{
    private string _key = string.Empty;
    private TreatmentCode _treatment = TreatmentCode.Unknown;
    private string _rateText = "0";
    private PriceMode _priceMode = PriceMode.Inclusive;

    public string Key { get => _key; set => Set(ref _key, value); }
    public TreatmentCode Treatment { get => _treatment; set => Set(ref _treatment, value); }
    public string RateText { get => _rateText; set => Set(ref _rateText, value); }
    public PriceMode PriceMode { get => _priceMode; set => Set(ref _priceMode, value); }

    public static IReadOnlyList<TreatmentCode> Treatments { get; } = Enum.GetValues<TreatmentCode>();
    public static IReadOnlyList<PriceMode> PriceModes { get; } = Enum.GetValues<PriceMode>();
}

/// <summary>
/// Business, tax and calendar profiles (IB-25, FR-011/029; POS-DEC-008): new revisions with start time,
/// approval state and evidence; a live preview of a draft; the profile in effect at any chosen time; and
/// the full revision history. Changes are prospective; the service authorizes and validates everything.
/// While this build is Test-only, the screens offer Test and Draft, not Approved.
/// </summary>
public sealed class ProfilesViewModel : PageViewModel
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private CalendarPolicy _zone = CalendarPolicy.ManilaMidnight;
    private ProfileHistory? _history;

    // Calculation draft
    private ProfileApprovalState _calcState = ProfileApprovalState.Test;
    private bool _calcStartNow = true;
    private string _calcStartText = string.Empty;
    private string _calcEvidence = string.Empty;
    private string _samplePriceText = "100.00";

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
        AddTaxRowCommand = new RelayCommand(_ => TaxRows.Add(new TaxRowViewModel { Key = "NEW" }));
        RemoveTaxRowCommand = new RelayCommand(p => TaxRows.Remove((TaxRowViewModel)p!), p => p is TaxRowViewModel);
        PreviewCommand = new RelayCommand(_ => RefreshPreview());
        SaveCalculationCommand = Command(SaveCalculationAsync, _ => CanEditFinancial);
        SaveCalendarCommand = Command(SaveCalendarAsync, _ => CanEditFinancial);
        SaveBusinessCommand = Command(SaveBusinessAsync, _ => CanEditFinancial);
        ShowEffectiveCommand = Command(_ => ShowEffectiveAsync());
    }

    public override string Title => "Profiles";

    /// <summary>Approved needs professionally confirmed rules (IB-29); not offered while the build is Test-only.</summary>
    public static IReadOnlyList<ProfileApprovalState> States { get; } = [ProfileApprovalState.Test, ProfileApprovalState.Draft];

    public bool CanEditFinancial => Services.Sessions.Current?.Has(Capabilities.SettingsFinancial) ?? false;
    public string ZoneNote => $"Times are in the business time zone ({_zone.TimeZoneId}), written as {TimeFormat}.";

    public ObservableCollection<TaxRowViewModel> TaxRows { get; } = [];
    public ObservableCollection<string> PreviewLines { get; } = [];
    public ObservableCollection<string> CalculationHistory { get; } = [];
    public ObservableCollection<string> CalendarHistory { get; } = [];
    public ObservableCollection<string> BusinessHistory { get; } = [];
    public ObservableCollection<string> Blockers { get; } = [];

    public ProfileApprovalState CalcState { get => _calcState; set => Set(ref _calcState, value); }
    public bool CalcStartNow { get => _calcStartNow; set => Set(ref _calcStartNow, value); }
    public string CalcStartText { get => _calcStartText; set => Set(ref _calcStartText, value); }
    public string CalcEvidence { get => _calcEvidence; set => Set(ref _calcEvidence, value); }
    public string SamplePriceText { get => _samplePriceText; set => Set(ref _samplePriceText, value); }

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

    public ICommand AddTaxRowCommand { get; }
    public ICommand RemoveTaxRowCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand SaveCalculationCommand { get; }
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
        CalcStartText = CalStartText = later;

        if (!await RefreshHistoryAsync())
        {
            return;
        }

        // Start each draft from what is in effect now, so a change is an edit, not a retype.
        TaxRows.Clear();
        var calc = await Services.Profiles.GetEffectiveCalculationProfileAsync(now);
        var source = calc.IsSuccess
            ? _history!.Calculation.FirstOrDefault(r => r.Id == calc.Value!.Profile.Id)?.Taxes
            : _history!.Calculation.FirstOrDefault()?.Taxes;
        foreach (var t in source ?? [])
        {
            TaxRows.Add(new TaxRowViewModel { Key = t.TaxKey, Treatment = t.Treatment, PriceMode = t.PriceMode, RateText = Percent(t.RatePpm) });
        }

        if (calendar.IsSuccess)
        {
            TimeZoneText = calendar.Value!.Policy.TimeZoneId;
            CutoffText = TimeSpan.FromMinutes(calendar.Value.Policy.CutoffMinute).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        if (_history.CurrentBusiness is { } b)
        {
            BizName = b.RegisteredName;
            BizAddress = b.Address;
            BizContact = b.Contact ?? string.Empty;
            BizTaxId = b.TaxIdentifier ?? string.Empty;
        }

        RefreshPreview();
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
            $"{t.TaxKey}: {t.Treatment}{(t.Treatment == TreatmentCode.Taxable ? $" {Percent(t.RatePpm)}%" : string.Empty)}, {t.PriceMode.ToString().ToLowerInvariant()}"));

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

    /// <summary>Builds the draft from the rows; null with an error message when a row is not valid.</summary>
    private CalculationProfileDraft? BuildCalculationDraft(long effectiveFromUtc)
    {
        var taxes = new List<TaxConfigurationDraft>();
        foreach (var row in TaxRows)
        {
            if (!decimal.TryParse(row.RateText?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
                || percent * 10_000m != decimal.Truncate(percent * 10_000m))
            {
                Error = $"Tax entry '{row.Key}': write the rate as a percentage with at most four decimals, for example 12 or 0.5.";
                return null;
            }

            if (percent > 100)
            {
                Error = $"Tax entry '{row.Key}': the rate must be between 0 and 100%.";
                return null;
            }

            taxes.Add(new TaxConfigurationDraft(row.Key?.Trim() ?? string.Empty, (int)(percent * 10_000m), row.PriceMode, row.Treatment));
        }

        return new CalculationProfileDraft(CalcState, effectiveFromUtc, Currency.Php, taxes, Blank(CalcEvidence));
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void RefreshPreview()
    {
        PreviewLines.Clear();
        if (!Formatting.TryParseMoney(SamplePriceText, out var price) || price <= 0)
        {
            PreviewLines.Add("Enter a sample price, for example 100.00.");
            return;
        }

        var draft = BuildCalculationDraft(Services.Clock.UtcNowMilliseconds);
        if (draft is null)
        {
            PreviewLines.Add(Error ?? "The draft is not valid yet.");
            Error = null;
            return;
        }

        foreach (var l in ProfilePreview.Sample(draft, price))
        {
            PreviewLines.Add(l.Problem is not null
                ? $"{l.TaxKey}: cannot calculate — {l.Problem}"
                : $"{l.TaxKey} ({l.Treatment}{(l.Treatment == TreatmentCode.Taxable ? $" {Percent(l.RatePpm)}%" : string.Empty)}, price {l.PriceMode.ToString().ToLowerInvariant()} of tax): "
                  + $"item priced {Formatting.Money(l.PriceMinor)} → net {Formatting.Money(l.NetMinor)} + tax {Formatting.Money(l.TaxMinor)} = customer pays {Formatting.Money(l.TotalMinor)}");
        }

        if (PreviewLines.Count == 0)
        {
            PreviewLines.Add("No tax entries. Products need a tax entry before they can be sold.");
        }
    }

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
                ? $"Tax: revision {calc.Value!.Profile.Revision} ({calc.Value.Profile.ApprovalState}), {calc.Value.Profile.Currency.Code} — "
                  + TaxText(_history?.Calculation.FirstOrDefault(r => r.Id == calc.Value.Profile.Id)?.Taxes ?? [])
                  + (calc.Value.HasUnknownTreatment ? Environment.NewLine + "   Products with an Unknown tax entry cannot be sold under this revision." : string.Empty)
                : "Tax: no calculation profile in effect — nothing can be sold at this time.",
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

    private async Task SaveCalculationAsync(object? passwordBox)
    {
        if (!TryParseTime(CalcStartNow, CalcStartText, "Starts", out var from) || BuildCalculationDraft(from) is not { } draft)
        {
            ShellViewModel.TakePassword(passwordBox);
            return;
        }

        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Profiles.CreateCalculationProfileAsync(Session, draft)))
        {
            return;
        }

        Info = draft.ApprovalState == ProfileApprovalState.Draft
            ? "Draft tax revision saved. Drafts are never used for sales."
            : $"Tax revision saved; it applies to sales from {_zone.LocalTimeText(from)}. Earlier sales keep the revision they were made under.";
        CalcEvidence = string.Empty;
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
        RefreshPreview();
        await ShowEffectiveAsync();
        await Shell.RefreshBusinessDateAsync();
    }
}
