using System.Text.Json;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Runtime;
using Beachtro.Application.Security;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Configuration;

/// <summary>
/// Versioned profiles (finance design §Money and policy contract; D-01/03/06; IB-06).
/// Revisions are immutable rows. A new effective revision closes the open one at its start
/// instant, so non-Draft revisions of a kind never overlap. Changes are prospective only.
/// </summary>
public sealed class ProfileService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IClock clock,
    WriteGate writeGate) : IProfileService
{
    public const int MaxTaxKeyLength = 64;
    public const int MaxSettingJsonLength = 2000;

    /// <summary>A start time up to this far in the past is treated as "now" (clock read by the caller).</summary>
    public const long EffectiveNowToleranceMs = 60_000;

    public async Task<Result<Guid>> CreateCalculationProfileAsync(UserSession session, CalculationProfileDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsFinancial, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<Guid>();
        }

        var invalid = ValidateCommon(draft.ApprovalState, draft.EffectiveFromUtc, draft.EvidenceReference) ?? ValidateTaxes(draft);
        if (invalid is not null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, invalid);
        }

        var payload = ProfilePayloads.Calculation(OrderCalculator.StrategyKey, draft.Currency, draft.Taxes);
        return await CreatePolicyAsync(session, PolicyKind.Calculation, draft.ApprovalState, draft.EffectiveFromUtc, draft.EvidenceReference, payload,
            policyId => draft.Taxes.Select(t => new TaxConfiguration
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = policyId,
                TaxKey = t.TaxKey,
                RatePpm = t.RatePpm,
                PriceMode = t.PriceMode,
                TreatmentCode = t.Treatment,
                StrategyKey = OrderCalculator.StrategyKey,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Guid>> CreateCalendarProfileAsync(UserSession session, CalendarProfileDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsFinancial, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<Guid>();
        }

        var invalid = ValidateCommon(draft.ApprovalState, draft.EffectiveFromUtc, draft.EvidenceReference);
        if (invalid is null && !CalendarPolicy.IsKnownTimeZone(draft.TimeZoneId ?? string.Empty))
        {
            invalid = $"Unknown time zone '{draft.TimeZoneId}'.";
        }

        if (invalid is null && draft.CutoffMinute is < 0 or >= CalendarPolicy.MinutesPerDay)
        {
            invalid = "The business-day cutoff must be between 00:00 and 23:59.";
        }

        if (invalid is not null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, invalid);
        }

        var payload = ProfilePayloads.Calendar(draft.TimeZoneId!, draft.CutoffMinute);
        return await CreatePolicyAsync(session, PolicyKind.Calendar, draft.ApprovalState, draft.EffectiveFromUtc, draft.EvidenceReference, payload,
            _ => [], cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Guid>> SaveBusinessProfileAsync(UserSession session, BusinessProfileDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsFinancial, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<Guid>();
        }

        var invalid = TextRules.ValidateRequired(draft.RegisteredName, "Registered name", TextRules.NameMaximum)
            ?? TextRules.ValidateRequired(draft.Address, "Address", TextRules.ReasonMaximum)
            ?? (draft.TemplateVersion < 1 ? "Template version must be 1 or higher." : null)
            ?? ValidateCommon(draft.ApprovalState, clock.UtcNowMilliseconds, draft.EvidenceReference);
        if (invalid is null && draft.ApprovalState == ProfileApprovalState.Test
            && !draft.RegisteredName.Contains("TEST", StringComparison.OrdinalIgnoreCase))
        {
            // Test receipts must never look like a real business identity (D-02).
            invalid = "A Test business profile name must contain the word TEST.";
        }

        if (invalid is not null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNowMilliseconds;
        var revision = (await db.BusinessProfiles.MaxAsync(p => (int?)p.Revision, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var profile = new BusinessProfile
        {
            Id = Guid.NewGuid(),
            Revision = revision,
            RegisteredName = draft.RegisteredName.Trim(),
            Address = draft.Address.Trim(),
            Contact = string.IsNullOrWhiteSpace(draft.Contact) ? null : draft.Contact.Trim(),
            TaxIdentifier = string.IsNullOrWhiteSpace(draft.TaxIdentifier) ? null : draft.TaxIdentifier.Trim(),
            RegistrationJson = ProfilePayloads.Registration(),
            TemplateVersion = draft.TemplateVersion,
            ApprovalState = draft.ApprovalState,
            EvidenceReference = draft.EvidenceReference,
            CreatedUtc = now,
        };
        db.BusinessProfiles.Add(profile);

        if (draft.ApprovalState != ProfileApprovalState.Draft)
        {
            var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
            state.CurrentBusinessProfileId = profile.Id;
            state.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Settings.BusinessProfileSaved", "BusinessProfile", profile.Id.ToString(), null,
            new { revision, state = draft.ApprovalState.ToString(), evidence = draft.EvidenceReference });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<Guid>.Ok(profile.Id);
    }

    public async Task<Result<EffectiveCalculationProfile>> GetEffectiveCalculationProfileAsync(long atUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var policy = await FindEffectiveAsync(db, PolicyKind.Calculation, atUtc, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return Result<EffectiveCalculationProfile>.Fail(ResultStatus.NotFound, "No calculation profile is in effect for this time.");
        }

        var (strategy, currency) = ProfilePayloads.ReadCalculation(policy.CanonicalPayloadJson);
        var taxes = await db.TaxConfigurations.AsNoTracking().Where(t => t.PolicyVersionId == policy.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rules = taxes.ToDictionary(
            t => t.TaxKey,
            t => t.TreatmentCode == TreatmentCode.Taxable
                ? LineTaxRule.Taxable(t.PriceMode, t.TaxKey, t.RatePpm)
                : new LineTaxRule(t.PriceMode, t.TreatmentCode, []),
            StringComparer.Ordinal);

        return Result<EffectiveCalculationProfile>.Ok(new EffectiveCalculationProfile(
            new CalculationProfile(policy.Id, policy.Revision, policy.ApprovalState, currency, strategy),
            policy.EffectiveFromUtc,
            policy.EffectiveToUtc,
            policy.PayloadSha256,
            rules));
    }

    public async Task<Result<EffectiveCalendarProfile>> GetEffectiveCalendarProfileAsync(long atUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var policy = await FindEffectiveAsync(db, PolicyKind.Calendar, atUtc, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return Result<EffectiveCalendarProfile>.Fail(ResultStatus.NotFound, "No calendar profile is in effect for this time.");
        }

        var (timeZoneId, cutoff) = ProfilePayloads.ReadCalendar(policy.CanonicalPayloadJson);
        return Result<EffectiveCalendarProfile>.Ok(new EffectiveCalendarProfile(policy.Id, policy.Revision, policy.ApprovalState, new CalendarPolicy(timeZoneId, cutoff)));
    }

    public async Task<ProductionReadiness> GetProductionReadinessAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNowMilliseconds;
        var blockers = new List<string>();

        if (!RuntimeProfilePolicy.ProductionEnabledInThisBuild)
        {
            blockers.Add("Production activation is disabled in this build (POS-DEC-002/003: Test profile only).");
        }

        var calculation = await GetEffectiveCalculationProfileAsync(now, cancellationToken).ConfigureAwait(false);
        if (!calculation.IsSuccess || calculation.Value!.Profile.ApprovalState != ProfileApprovalState.Approved)
        {
            blockers.Add("No Approved calculation (tax) profile is in effect; tax treatment must be professionally confirmed (D-01).");
        }
        else if (calculation.Value.HasUnknownTreatment)
        {
            blockers.Add("The effective calculation profile still has an Unknown tax treatment.");
        }

        var calendar = await GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess || calendar.Value!.ApprovalState != ProfileApprovalState.Approved)
        {
            blockers.Add("No Approved calendar profile (time zone and business-day cutoff) is in effect (D-06).");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var business = state.CurrentBusinessProfileId is { } id
            ? await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false)
            : null;
        if (business?.ApprovalState != ProfileApprovalState.Approved)
        {
            blockers.Add("No Approved business profile (registered name, address, required identifiers) is current (D-02).");
        }

        if (state.BootstrapCompletedUtc is null)
        {
            blockers.Add("The owner account has not been created.");
        }

        return new ProductionReadiness(blockers);
    }

    public async Task<Result<Unit>> ActivateProductionAsync(UserSession session, string evidenceReference, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ProductionActivate, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(evidenceReference, "Readiness evidence reference", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        var readiness = await GetProductionReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (!readiness.IsReady)
        {
            return Result.Fail(ResultStatus.Validation, "Production cannot be activated: " + string.Join(" ", readiness.Blockers));
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        state.ProductionEnabled = true;
        state.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Settings.ProductionActivated", "InstallationState", state.InstallationId.ToString(),
            null, new { evidence = evidenceReference });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<Unit>> SetOperationalSettingAsync(UserSession session, string key, string valueJson, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsOperational, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        if (key is null || !OperationalSettingKeys.All.Contains(key))
        {
            return Result.Fail(ResultStatus.Validation, "That is not an operational setting. Financial settings use versioned profiles.");
        }

        if (string.IsNullOrWhiteSpace(valueJson) || valueJson.Length > MaxSettingJsonLength || !IsValidJson(valueJson))
        {
            return Result.Fail(ResultStatus.Validation, "The setting value must be valid JSON of at most 2000 characters.");
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var setting = await db.BusinessSettings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        var before = setting?.ValueJson;
        if (setting is null)
        {
            db.BusinessSettings.Add(new BusinessSetting { Key = key, ValueJson = valueJson, UpdatedByUserId = session.UserId, UpdatedUtc = now });
        }
        else
        {
            setting.ValueJson = valueJson;
            setting.UpdatedByUserId = session.UserId;
            setting.UpdatedUtc = now;
            setting.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Settings.OperationalChanged", "BusinessSetting", key, null,
            new { before, after = valueJson });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    // ---------------------------------------------------------------- internals

    private async Task<Result<Guid>> CreatePolicyAsync(
        UserSession session,
        PolicyKind kind,
        ProfileApprovalState approvalState,
        long effectiveFromUtc,
        string? evidenceReference,
        string payload,
        Func<Guid, IEnumerable<TaxConfiguration>> children,
        CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (approvalState != ProfileApprovalState.Draft)
        {
            var conflict = await ScheduleAsync(db, kind, effectiveFromUtc, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return Result<Guid>.Fail(ResultStatus.Conflict, conflict);
            }
        }

        var now = clock.UtcNowMilliseconds;
        var revision = (await db.PolicyVersions.Where(p => p.Kind == kind).MaxAsync(p => (int?)p.Revision, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var policy = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Revision = revision,
            EffectiveFromUtc = effectiveFromUtc,
            ApprovalState = approvalState,
            ApprovedByUserId = approvalState == ProfileApprovalState.Approved ? session.UserId : null,
            EvidenceReference = evidenceReference,
            PayloadSchemaVersion = 1,
            CanonicalPayloadJson = payload,
            PayloadSha256 = CanonicalJson.Sha256(payload),
            CreatedUtc = now,
        };
        db.PolicyVersions.Add(policy);
        db.TaxConfigurations.AddRange(children(policy.Id));

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, $"Settings.{kind}ProfileCreated", "PolicyVersion", policy.Id.ToString(), null,
            new { kind = kind.ToString(), revision, state = approvalState.ToString(), effectiveFromUtc, payloadSha256 = policy.PayloadSha256, evidence = evidenceReference });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<Guid>.Ok(policy.Id);
    }

    /// <summary>
    /// Keeps non-Draft revisions of a kind non-overlapping: rejects a start at or before an existing
    /// revision's start, and closes the single open revision at the new start (allowed once by trigger).
    /// </summary>
    private static async Task<string?> ScheduleAsync(PosDbContext db, PolicyKind kind, long effectiveFromUtc, CancellationToken cancellationToken)
    {
        var existing = await db.PolicyVersions
            .Where(p => p.Kind == kind && p.ApprovalState != ProfileApprovalState.Draft)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (existing.Any(p => p.EffectiveFromUtc >= effectiveFromUtc))
        {
            return "Another revision already starts at or after this time. Schedule the new revision later.";
        }

        foreach (var p in existing.Where(p => p.EffectiveToUtc is null || p.EffectiveToUtc > effectiveFromUtc))
        {
            if (p.EffectiveToUtc is not null)
            {
                return "An existing revision's closed interval overlaps this start time.";
            }

            p.EffectiveToUtc = effectiveFromUtc;
        }

        return null;
    }

    private static async Task<PolicyVersion?> FindEffectiveAsync(PosDbContext db, PolicyKind kind, long atUtc, CancellationToken cancellationToken)
    {
        var productionEnabled = await db.InstallationState.AsNoTracking().Select(s => s.ProductionEnabled).SingleAsync(cancellationToken).ConfigureAwait(false);
        var candidates = db.PolicyVersions.AsNoTracking()
            .Where(p => p.Kind == kind && p.ApprovalState != ProfileApprovalState.Draft
                && p.EffectiveFromUtc <= atUtc && (p.EffectiveToUtc == null || p.EffectiveToUtc > atUtc));

        // A production installation never resolves synthetic Test revisions.
        if (productionEnabled)
        {
            candidates = candidates.Where(p => p.ApprovalState == ProfileApprovalState.Approved);
        }

        return await candidates.OrderByDescending(p => p.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ValidateCommon(ProfileApprovalState state, long effectiveFromUtc, string? evidenceReference)
    {
        if (!Enum.IsDefined(state))
        {
            return "Unknown approval state.";
        }

        // "Effective now" computed a moment earlier by the caller is fine; anything older is backdating.
        if (effectiveFromUtc < clock.UtcNowMilliseconds - EffectiveNowToleranceMs)
        {
            return "Profile changes are prospective only; the start time cannot be in the past.";
        }

        if (state == ProfileApprovalState.Approved && string.IsNullOrWhiteSpace(evidenceReference))
        {
            return "An Approved profile needs a reference to its professional or owner approval evidence.";
        }

        return evidenceReference is { Length: > TextRules.ReasonMaximum } ? "Evidence reference is too long." : null;
    }

    private static string? ValidateTaxes(CalculationProfileDraft draft)
    {
        if (draft.Taxes is null)
        {
            return "Tax configuration list is required (it may be empty).";
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in draft.Taxes)
        {
            if (string.IsNullOrWhiteSpace(t.TaxKey) || t.TaxKey.Length > MaxTaxKeyLength || t.TaxKey != t.TaxKey.Trim())
            {
                return $"Tax keys must be 1–{MaxTaxKeyLength} characters without surrounding spaces.";
            }

            if (!keys.Add(t.TaxKey))
            {
                return $"Tax key '{t.TaxKey}' appears twice.";
            }

            if (!Enum.IsDefined(t.PriceMode) || !Enum.IsDefined(t.Treatment))
            {
                return $"Tax key '{t.TaxKey}' has an invalid price mode or treatment.";
            }

            if (t.RatePpm is < 0 or > FinancialLimits.RateScalePpm)
            {
                return $"Tax key '{t.TaxKey}' rate must be 0–100%.";
            }

            if (t.Treatment != TreatmentCode.Taxable && t.RatePpm != 0)
            {
                return $"Tax key '{t.TaxKey}' is {t.Treatment}; only Taxable entries carry a rate.";
            }

            if (t.Treatment == TreatmentCode.TestNoTax && draft.ApprovalState != ProfileApprovalState.Test)
            {
                return "TestNoTax is allowed only in Test profiles.";
            }

            if (t.Treatment == TreatmentCode.Unknown && draft.ApprovalState == ProfileApprovalState.Approved)
            {
                return $"Tax key '{t.TaxKey}' is Unknown; an Approved profile needs confirmed treatments (D-01).";
            }
        }

        return null;
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
