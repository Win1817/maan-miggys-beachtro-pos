using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;

namespace Beachtro.Application.Configuration;

public sealed record TaxConfigurationDraft(string TaxKey, int RatePpm, PriceMode PriceMode, TreatmentCode Treatment);

/// <summary>A new calculation profile revision. Revisions are immutable once written; changes are new revisions.</summary>
public sealed record CalculationProfileDraft(
    ProfileApprovalState ApprovalState,
    long EffectiveFromUtc,
    Currency Currency,
    IReadOnlyList<TaxConfigurationDraft> Taxes,
    string? EvidenceReference);

public sealed record CalendarProfileDraft(
    ProfileApprovalState ApprovalState,
    long EffectiveFromUtc,
    string TimeZoneId,
    int CutoffMinute,
    string? EvidenceReference);

/// <summary>Business identity for receipts. Test revisions must be visibly marked TEST; never real identifiers.</summary>
public sealed record BusinessProfileDraft(
    ProfileApprovalState ApprovalState,
    string RegisteredName,
    string Address,
    string? Contact,
    string? TaxIdentifier,
    int TemplateVersion,
    string? EvidenceReference);

/// <summary>The calculation profile in force at an instant, with each tax key resolved to a line rule.</summary>
public sealed record EffectiveCalculationProfile(
    CalculationProfile Profile,
    long EffectiveFromUtc,
    long? EffectiveToUtc,
    string PayloadSha256,
    IReadOnlyDictionary<string, LineTaxRule> TaxRules)
{
    /// <summary>True when any configured tax treatment is still Unknown; such profiles cannot complete sales.</summary>
    public bool HasUnknownTreatment => TaxRules.Values.Any(r => r.Treatment == TreatmentCode.Unknown);
}

public sealed record EffectiveCalendarProfile(Guid PolicyId, int Revision, ProfileApprovalState ApprovalState, CalendarPolicy Policy);

public sealed record ProductionReadiness(IReadOnlyList<string> Blockers)
{
    public bool IsReady => Blockers.Count == 0;
}

/// <summary>
/// Versioned financial, calendar and business profiles plus operational settings (FR-011, FR-029;
/// IB-06). Financial changes are prospective only; history keeps the revision it was issued under.
/// </summary>
public interface IProfileService
{
    Task<Result<Guid>> CreateCalculationProfileAsync(UserSession session, CalculationProfileDraft draft, CancellationToken cancellationToken = default);

    Task<Result<Guid>> CreateCalendarProfileAsync(UserSession session, CalendarProfileDraft draft, CancellationToken cancellationToken = default);

    Task<Result<Guid>> SaveBusinessProfileAsync(UserSession session, BusinessProfileDraft draft, CancellationToken cancellationToken = default);

    Task<Result<EffectiveCalculationProfile>> GetEffectiveCalculationProfileAsync(long atUtc, CancellationToken cancellationToken = default);

    Task<Result<EffectiveCalendarProfile>> GetEffectiveCalendarProfileAsync(long atUtc, CancellationToken cancellationToken = default);

    Task<ProductionReadiness> GetProductionReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Deliberate readiness action (IB-32). Refused while any blocker remains.</summary>
    Task<Result<Unit>> ActivateProductionAsync(UserSession session, string evidenceReference, CancellationToken cancellationToken = default);

    /// <summary>Non-financial settings only (printer, UI). Financial policy is never a mutable key/value.</summary>
    Task<Result<Unit>> SetOperationalSettingAsync(UserSession session, string key, string valueJson, CancellationToken cancellationToken = default);
}

public static class OperationalSettingKeys
{
    public const string PrinterName = "printer.name";
    public const string PrinterPaperWidthMm = "printer.paperWidthMm";
    public const string UiTouchMode = "ui.touchMode";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { PrinterName, PrinterPaperWidthMm, UiTouchMode };
}
