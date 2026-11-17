using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Tests.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Configuration;

/// <summary>
/// T-01/02/17 for the settings and users screens (IB-25, POS-DEC-008): the shared delegation rule the
/// screen uses agrees with the service, and the revision history.
/// </summary>
public sealed class SettingsScreenTests : IDisposable
{
    private const int SyntheticRate = 120_000; // arithmetic test input only, not an approved tax setting
    private readonly SecurityHarness _h = new();

    public void Dispose() => _h.Dispose();

    private long Now => _h.Clock.UtcNowMilliseconds;

    public static TheoryData<string, string> ActorAndRole
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var actor in new[] { RoleTemplates.Administrator, RoleTemplates.Owner })
            {
                foreach (var role in RoleTemplates.Grants.Keys)
                {
                    data.Add(actor, role);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ActorAndRole))]
    public async Task The_screen_offers_exactly_the_roles_the_service_lets_the_actor_grant(string actorTemplate, string role)
    {
        var owner = await _h.BootstrapOwnerAsync();
        var actor = actorTemplate == RoleTemplates.Owner ? owner : (await _h.CreateUserAsync(owner, "actor.one", actorTemplate)).Session;

        var offered = RoleDelegation.WhyNot(actor, role) is null;
        var created = await _h.Identity.CreateUserAsync(actor, new CreateUserRequest("new." + role, "New", [role], "screen check"));

        Assert.Equal(offered, created.IsSuccess);
        if (!offered)
        {
            Assert.Equal(ResultStatus.Forbidden, created.Status);
        }
    }

    [Fact]
    public async Task An_administrator_cannot_create_or_reset_selling_accounts()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (cashierId, _) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        var (_, admin) = await _h.CreateUserAsync(owner, "admin.one", RoleTemplates.Administrator);

        Assert.Equal(RoleDelegation.MissingPermissionsMessage, RoleDelegation.WhyNot(admin, RoleTemplates.Cashier));
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.CreateUserAsync(admin, new CreateUserRequest("cashier.two", "C2", [RoleTemplates.Cashier], "hire"))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.ResetPasswordAsync(admin, cashierId, "forgot")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.SetUserActiveAsync(admin, cashierId, false, "left")).Status);
        Assert.Equal(RoleDelegation.OwnerOnlyMessage, RoleDelegation.WhyNot(admin, RoleTemplates.Owner));
        Assert.Null(RoleDelegation.WhyNot(owner, RoleTemplates.Cashier));
    }

    [Fact]
    public async Task History_lists_every_revision_with_its_range_and_the_current_business_profile()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var first = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, Calc(ProfileApprovalState.Test, Now,
            new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Inclusive, TreatmentCode.Taxable),
            new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.TestNoTax))));
        var switchAt = Now + 3_600_000;
        var second = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, Calc(ProfileApprovalState.Test, switchAt,
            new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Exclusive, TreatmentCode.Taxable))));
        var draft = Ok(await _h.Profiles.CreateCalculationProfileAsync(owner, Calc(ProfileApprovalState.Draft, Now + 7_200_000,
            new TaxConfigurationDraft("SYN", 0, PriceMode.Inclusive, TreatmentCode.Unknown))));
        Ok(await _h.Profiles.CreateCalendarProfileAsync(owner, new CalendarProfileDraft(ProfileApprovalState.Test, Now, "Asia/Manila", 240, "test note")));
        var oldBusiness = Ok(await _h.Profiles.SaveBusinessProfileAsync(owner, new BusinessProfileDraft(ProfileApprovalState.Test, "TEST Shop", "Test address", null, null, 1, null)));
        var newBusiness = Ok(await _h.Profiles.SaveBusinessProfileAsync(owner, new BusinessProfileDraft(ProfileApprovalState.Test, "TEST Shop 2", "Test address", "0900", null, 1, "ref-1")));

        var history = Ok(await _h.Profiles.GetProfileHistoryAsync(owner));

        Assert.Equal([draft, second, first], history.Calculation.Select(r => r.Id));
        var firstView = history.Calculation[2];
        Assert.Equal(switchAt, firstView.EffectiveToUtc);
        Assert.Equal(["NONE", "SYN"], firstView.Taxes.Select(t => t.TaxKey));
        Assert.Equal("PHP", firstView.CurrencyCode);
        Assert.Null(history.Calculation[1].EffectiveToUtc);
        Assert.Equal(ProfileApprovalState.Draft, history.Calculation[0].ApprovalState);
        Assert.All(history.Calculation, r => Assert.Equal(PolicyKind.Calculation, r.Kind));

        var calendar = Assert.Single(history.Calendar);
        Assert.Equal(("Asia/Manila", 240, "test note"), (calendar.TimeZoneId, calendar.CutoffMinute ?? -1, calendar.EvidenceReference));
        Assert.Empty(calendar.Taxes);

        Assert.Equal([newBusiness, oldBusiness], history.Business.Select(b => b.Id));
        Assert.Equal(newBusiness, history.CurrentBusiness!.Id);
        Assert.Equal(("0900", "ref-1"), (history.CurrentBusiness.Contact, history.CurrentBusiness.EvidenceReference));
    }

    [Fact]
    public async Task History_needs_settings_permission()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (_, cashier) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        var (_, manager) = await _h.CreateUserAsync(owner, "manager.one", RoleTemplates.Manager);

        Assert.Equal(ResultStatus.Forbidden, (await _h.Profiles.GetProfileHistoryAsync(cashier)).Status);
        Assert.True((await _h.Profiles.GetProfileHistoryAsync(manager)).IsSuccess);

        // A manager can look but not change financial settings.
        Assert.Equal(ResultStatus.Forbidden, (await _h.Profiles.CreateCalculationProfileAsync(manager, Calc(ProfileApprovalState.Test, Now,
            new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.TestNoTax)))).Status);
    }

    private static CalculationProfileDraft Calc(ProfileApprovalState state, long from, params TaxConfigurationDraft[] taxes) =>
        new(state, from, Currency.Php, taxes, null);
}
