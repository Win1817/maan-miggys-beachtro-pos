using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Infrastructure.Serialization;
using Beachtro.Infrastructure.Tests.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Configuration;

/// <summary>T-17 configuration: versioned, prospective, non-overlapping profiles and the production gate.</summary>
public sealed class ProfileTests : IDisposable
{
    private const int SyntheticRate = 120_000; // arithmetic test input only, not an approved tax setting
    private readonly SecurityHarness _h = new();

    public void Dispose() => _h.Dispose();

    private long Now => _h.Clock.UtcNowMilliseconds;

    private static CalculationProfileDraft TestCalc(long from, params TaxConfigurationDraft[] taxes) =>
        new(ProfileApprovalState.Test, from, Currency.Php, taxes.Length == 0 ? [new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.TestNoTax)] : taxes, null);

    [Fact]
    public async Task A_test_calculation_profile_resolves_only_from_its_start()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var start = Now + 60_000;
        var id = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(start, new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Inclusive, TreatmentCode.Taxable))));

        Assert.Equal(ResultStatus.NotFound, (await _h.Profiles.GetEffectiveCalculationProfileAsync(start - 1)).Status);

        var effective = Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(start));
        Assert.Equal(id, effective.Profile.Id);
        Assert.Equal(1, effective.Profile.Revision);
        Assert.Equal(ProfileApprovalState.Test, effective.Profile.ApprovalState);
        Assert.Equal(Currency.Php, effective.Profile.Currency);
        Assert.Equal(OrderCalculator.StrategyKey, effective.Profile.StrategyKey);

        // The resolved rule plugs straight into the money kernel (F-03: 112.00 inclusive → 100.00 + 12.00).
        var line = new CartLine(1, Guid.NewGuid(), 112_00, 1000, 1000, Currency.Php, true, effective.TaxRules["SYN"]);
        var order = OrderCalculator.Calculate(effective.Profile, [line], OrderDiscount.None);
        Assert.Equal((100_00L, 12_00L), (order.NetMinor, order.TaxMinor));
    }

    [Fact]
    public async Task A_new_revision_closes_the_previous_one_and_history_keeps_resolving()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var first = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now)));
        var switchAt = Now + 3_600_000;
        var second = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(switchAt, new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Exclusive, TreatmentCode.Taxable))));

        Assert.Equal(first, Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(switchAt - 1)).Profile.Id);
        Assert.Equal(second, Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(switchAt)).Profile.Id);
        Assert.Equal(switchAt, Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(Now)).EffectiveToUtc);
        Assert.Equal(2, Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(switchAt)).Profile.Revision);
    }

    [Fact]
    public async Task Backdated_and_overlapping_revisions_are_rejected()
    {
        var owner = await _h.BootstrapOwnerAsync();
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now - 60_001))).Status);

        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now + 10_000)));
        Assert.Equal(ResultStatus.Conflict, (await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now + 10_000))).Status);
        Assert.Equal(ResultStatus.Conflict, (await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now + 5_000))).Status);
    }

    [Fact]
    public async Task A_start_read_from_the_clock_a_moment_earlier_counts_as_now()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var start = Now;
        _h.Clock.Advance(TimeSpan.FromMilliseconds(250)); // real clocks move between the caller's read and validation
        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(start)));
        Assert.True((await _h.Profiles.GetEffectiveCalculationProfileAsync(Now)).IsSuccess);
    }

    [Fact]
    public async Task Unknown_tax_is_explicit_and_blocks_approval_but_not_test_engineering()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var unknown = new TaxConfigurationDraft("VAT?", 0, PriceMode.Inclusive, TreatmentCode.Unknown);

        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now, unknown)));
        var effective = Ok(await _h.Profiles.GetEffectiveCalculationProfileAsync(Now));
        Assert.True(effective.HasUnknownTreatment);

        var line = new CartLine(1, Guid.NewGuid(), 100_00, 1000, 1000, Currency.Php, true, effective.TaxRules["VAT?"]);
        var ex = Assert.Throws<FinancialRuleException>(() => OrderCalculator.Calculate(effective.Profile, [line], OrderDiscount.None));
        Assert.Equal(FinancialErrorCode.UnknownTaxTreatment, ex.Code);

        var approvedUnknown = new CalculationProfileDraft(ProfileApprovalState.Approved, Now + 1000, Currency.Php, [unknown], "ACCT-REF-TEST");
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalculationProfileAsync(owner, approvedUnknown)).Status);

        var approvedNoEvidence = new CalculationProfileDraft(ProfileApprovalState.Approved, Now + 1000, Currency.Php, [], null);
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalculationProfileAsync(owner, approvedNoEvidence)).Status);

        var approvedTestNoTax = new CalculationProfileDraft(ProfileApprovalState.Approved, Now + 1000, Currency.Php,
            [new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.TestNoTax)], "ACCT-REF-TEST");
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalculationProfileAsync(owner, approvedTestNoTax)).Status);
    }

    [Fact]
    public async Task Tax_entries_are_validated()
    {
        var owner = await _h.BootstrapOwnerAsync();
        async Task<ResultStatus> Status(params TaxConfigurationDraft[] taxes) =>
            (await _h.Profiles.CreateCalculationProfileAsync(owner, TestCalc(Now + 1, taxes))).Status;

        Assert.Equal(ResultStatus.Validation, await Status(new TaxConfigurationDraft("A", 1_000_001, PriceMode.Inclusive, TreatmentCode.Taxable)));
        Assert.Equal(ResultStatus.Validation, await Status(new TaxConfigurationDraft("A", 5, PriceMode.Inclusive, TreatmentCode.Exempt)));
        Assert.Equal(ResultStatus.Validation, await Status(
            new TaxConfigurationDraft("A", 0, PriceMode.Inclusive, TreatmentCode.Exempt), new TaxConfigurationDraft("A", 0, PriceMode.Inclusive, TreatmentCode.ZeroRated)));
        Assert.Equal(ResultStatus.Validation, await Status(new TaxConfigurationDraft(" A", 0, PriceMode.Inclusive, TreatmentCode.Exempt)));
    }

    [Fact]
    public async Task Canonical_payload_is_order_independent_and_matches_the_stored_hash()
    {
        var a = new TaxConfigurationDraft("A", 1, PriceMode.Inclusive, TreatmentCode.Taxable);
        var b = new TaxConfigurationDraft("B", 0, PriceMode.Exclusive, TreatmentCode.Exempt);
        var forward = ProfilePayloads.Calculation(OrderCalculator.StrategyKey, Currency.Php, [a, b]);
        var reversed = ProfilePayloads.Calculation(OrderCalculator.StrategyKey, Currency.Php, [b, a]);

        Assert.Equal(forward, reversed);
        Assert.Equal(
            """{"schema":"calculation.v1","strategy":"generic-line-v1","currency":{"code":"PHP","exponent":2},"taxes":[{"key":"A","ratePpm":1,"priceMode":"Inclusive","treatment":"Taxable"},{"key":"B","ratePpm":0,"priceMode":"Exclusive","treatment":"Exempt"}]}""",
            forward);

        var owner = await _h.BootstrapOwnerAsync();
        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, new CalculationProfileDraft(ProfileApprovalState.Test, Now, Currency.Php, [b, a], null)));
        Assert.Equal(forward, _h.Database.Scalar<string>("SELECT CanonicalPayloadJson FROM PolicyVersions"));
        Assert.Equal(CanonicalJson.Sha256(forward), _h.Database.Scalar<string>("SELECT PayloadSha256 FROM PolicyVersions"));
    }

    [Fact]
    public async Task Calendar_profiles_resolve_and_reject_unknown_zones_and_cutoffs()
    {
        var owner = await _h.BootstrapOwnerAsync();
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalendarProfileAsync(owner, new CalendarProfileDraft(ProfileApprovalState.Test, Now, "Mars/Olympus", 0, null))).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.CreateCalendarProfileAsync(owner, new CalendarProfileDraft(ProfileApprovalState.Test, Now, "Asia/Manila", 1440, null))).Status);

        Ok(await _h.Profiles.CreateCalendarProfileAsync(owner, new CalendarProfileDraft(ProfileApprovalState.Test, Now, "Asia/Manila", 0, null)));
        var calendar = Ok(await _h.Profiles.GetEffectiveCalendarProfileAsync(Now));
        Assert.Equal("Asia/Manila", calendar.Policy.TimeZoneId);
        Assert.Equal(0, calendar.Policy.CutoffMinute);
    }

    [Fact]
    public async Task Test_business_profiles_must_say_TEST_and_become_current()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var realLooking = new BusinessProfileDraft(ProfileApprovalState.Test, "Beach Cafe", "Somewhere", null, null, 1, null);
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.SaveBusinessProfileAsync(owner, realLooking)).Status);

        var id = Ok(await _h.Profiles.SaveBusinessProfileAsync(owner, realLooking with { RegisteredName = "TEST CAFE - NOT REAL" }));
        Assert.Equal(id.ToString(), _h.Database.Scalar<string>("SELECT CurrentBusinessProfileId FROM InstallationState"));

        var approvedNoEvidence = realLooking with { ApprovalState = ProfileApprovalState.Approved };
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.SaveBusinessProfileAsync(owner, approvedNoEvidence)).Status);
    }

    [Fact]
    public async Task Production_stays_blocked_in_this_build_even_with_approved_profiles()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var initial = await _h.Profiles.GetProductionReadinessAsync();
        Assert.False(initial.IsReady);
        Assert.Contains(initial.Blockers, b => b.Contains("disabled in this build", StringComparison.Ordinal));
        Assert.Contains(initial.Blockers, b => b.Contains("calculation", StringComparison.Ordinal));

        // Synthetic "approved" inputs exercise the gate; they are not real approvals.
        // An approved profile that still computes tax is refused: the business charges no tax (POS-DEC-009).
        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, new CalculationProfileDraft(ProfileApprovalState.Approved, Now, Currency.Php,
            [new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Inclusive, TreatmentCode.Taxable)], "SYNTHETIC-EVIDENCE")));
        Assert.Contains((await _h.Profiles.GetProductionReadinessAsync()).Blockers, b => b.Contains("still computes tax", StringComparison.Ordinal));
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, new CalculationProfileDraft(ProfileApprovalState.Approved, Now, Currency.Php,
            [new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.NotTaxed)], "SYNTHETIC-EVIDENCE")));
        Ok(await _h.Profiles.CreateCalendarProfileAsync(owner, new CalendarProfileDraft(ProfileApprovalState.Approved, Now, "Asia/Manila", 0, "SYNTHETIC-EVIDENCE")));
        Ok(await _h.Profiles.SaveBusinessProfileAsync(owner, new BusinessProfileDraft(ProfileApprovalState.Approved, "Synthetic Name", "Synthetic Address", null, null, 1, "SYNTHETIC-EVIDENCE")));

        var readiness = await _h.Profiles.GetProductionReadinessAsync();
        Assert.Equal(["Production activation is disabled in this build (POS-DEC-002/003: Test profile only)."], readiness.Blockers);

        var activation = await _h.Profiles.ActivateProductionAsync(owner, "SYNTHETIC-EVIDENCE");
        Assert.Equal(ResultStatus.Validation, activation.Status);
        Assert.Equal(0L, _h.Database.Scalar<long>("SELECT ProductionEnabled FROM InstallationState"));
    }

    [Fact]
    public async Task Financial_settings_need_settings_financial_and_operational_settings_are_whitelisted()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (_, manager) = await _h.CreateUserAsync(owner, "manager.one", RoleTemplates.Manager);
        var (_, cashier) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);

        Assert.Equal(ResultStatus.Forbidden, (await _h.Profiles.CreateCalculationProfileAsync(manager, TestCalc(Now + 1))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Profiles.ActivateProductionAsync(manager, "x")).Status);

        Ok(await _h.Profiles.SetOperationalSettingAsync(manager, OperationalSettingKeys.PrinterName, "\"Microsoft Print to PDF\""));
        Ok(await _h.Profiles.SetOperationalSettingAsync(manager, OperationalSettingKeys.PrinterName, "\"Virtual Printer\""));
        Assert.Equal("\"Virtual Printer\"", _h.Database.Scalar<string>("SELECT ValueJson FROM BusinessSettings WHERE Key = 'printer.name'"));
        Assert.Equal(2L, _h.Database.Scalar<long>("SELECT Version FROM BusinessSettings WHERE Key = 'printer.name'"));

        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.SetOperationalSettingAsync(manager, "tax.rate", "12")).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Profiles.SetOperationalSettingAsync(manager, OperationalSettingKeys.UiTouchMode, "{not json")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Profiles.SetOperationalSettingAsync(cashier, OperationalSettingKeys.UiTouchMode, "true")).Status);
    }
}
