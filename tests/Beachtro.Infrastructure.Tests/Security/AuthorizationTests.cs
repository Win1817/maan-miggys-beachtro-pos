using Beachtro.Application.Common;
using Beachtro.Application.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Security;

/// <summary>T-02: services enforce capabilities directly, delegation limits, last-owner retention and audit.</summary>
public sealed class AuthorizationTests : IDisposable
{
    private readonly SecurityHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static TheoryData<string> Templates => [RoleTemplates.Cashier, RoleTemplates.Manager, RoleTemplates.Administrator, RoleTemplates.Owner];

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task Each_template_gets_exactly_its_matrix_capabilities(string template)
    {
        var owner = await _h.BootstrapOwnerAsync();
        var session = template == RoleTemplates.Owner ? owner : (await _h.CreateUserAsync(owner, "user." + template, template)).Session;

        foreach (var capability in Capabilities.All)
        {
            var result = await _h.Identity.AuthorizeAsync(session, capability);
            var expected = RoleTemplates.Grants[template].Contains(capability);
            Assert.True(expected == result.IsSuccess, $"{template} / {capability}: expected {(expected ? "allow" : "deny")}, got {result.Status}");
            if (!expected)
            {
                Assert.Equal(ResultStatus.Forbidden, result.Status);
            }
        }
    }

    [Fact]
    public void Administrator_has_no_selling_or_financial_correction_authority()
    {
        var admin = RoleTemplates.Grants[RoleTemplates.Administrator];
        Assert.DoesNotContain(Capabilities.PosSell, admin);
        Assert.DoesNotContain(Capabilities.CorrectionsRefund, admin);
        Assert.DoesNotContain(Capabilities.CorrectionsVoid, admin);
        Assert.DoesNotContain(Capabilities.SettingsFinancial, admin);
    }

    [Fact]
    public async Task Without_a_session_every_protected_call_is_refused()
    {
        await _h.BootstrapOwnerAsync();
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(null, Capabilities.PosSell)).Status);
    }

    [Fact]
    public async Task Cashier_cannot_administer_accounts_through_the_service()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (_, cashier) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        var ownerId = owner.UserId;

        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.CreateUserAsync(cashier, new CreateUserRequest("x.user", "X", [RoleTemplates.Cashier], "r"))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.ResetPasswordAsync(cashier, ownerId, "takeover")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.SetUserRolesAsync(cashier, cashier.UserId, [RoleTemplates.Owner], "escalate")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.ListUsersAsync(cashier)).Status);
    }

    [Fact]
    public async Task Administrator_cannot_touch_owners_or_grant_permissions_it_lacks()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (managerId, _) = await _h.CreateUserAsync(owner, "manager.one", RoleTemplates.Manager);
        var (_, admin) = await _h.CreateUserAsync(owner, "admin.one", RoleTemplates.Administrator);

        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.ResetPasswordAsync(admin, owner.UserId, "takeover")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.SetUserActiveAsync(admin, owner.UserId, false, "takeover")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.SetUserRolesAsync(admin, admin.UserId, [RoleTemplates.Administrator, RoleTemplates.Owner], "escalate")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.SetUserRolesAsync(admin, managerId, [RoleTemplates.Administrator], "change")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.CreateUserAsync(admin, new CreateUserRequest("m.two", "M", [RoleTemplates.Manager], "hire"))).Status);

        // Within its own delegable set an Administrator can act.
        Assert.True((await _h.Identity.CreateUserAsync(admin, new CreateUserRequest("admin.two", "A2", [RoleTemplates.Administrator], "backup admin"))).IsSuccess);
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_removed_or_deactivated()
    {
        var owner = await _h.BootstrapOwnerAsync();

        Assert.Equal(ResultStatus.Conflict, (await _h.Identity.SetUserActiveAsync(owner, owner.UserId, false, "leaving")).Status);
        Assert.Equal(ResultStatus.Conflict, (await _h.Identity.SetUserRolesAsync(owner, owner.UserId, [RoleTemplates.Manager], "demote")).Status);

        var (secondOwnerId, _) = await _h.CreateUserAsync(owner, "owner.two", RoleTemplates.Owner);
        Assert.True((await _h.Identity.SetUserActiveAsync(owner, secondOwnerId, false, "temporary")).IsSuccess);
        Assert.Equal(ResultStatus.Conflict, (await _h.Identity.SetUserActiveAsync(owner, owner.UserId, false, "leaving")).Status);

        Assert.True((await _h.Identity.SetUserActiveAsync(owner, secondOwnerId, true, "back")).IsSuccess);
        Assert.True((await _h.Identity.SetUserRolesAsync(owner, owner.UserId, [RoleTemplates.Manager], "hand over")).IsSuccess);
        Assert.Equal(1L, _h.Database.Scalar<long>(
            "SELECT count(*) FROM UserRoles ur JOIN Roles r ON r.Id = ur.RoleId JOIN Users u ON u.Id = ur.UserId WHERE r.Key = 'owner' AND u.IsActive = 1"));
    }

    [Fact]
    public async Task Role_and_activation_changes_end_the_target_sessions()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (managerId, manager) = await _h.CreateUserAsync(owner, "manager.one", RoleTemplates.Manager);

        Ok(await _h.Identity.SetUserRolesAsync(owner, managerId, [RoleTemplates.Cashier], "role change"));
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(manager, Capabilities.PosSell)).Status);

        var asCashier = await _h.SignInAsync("manager.one", UserPassword);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Identity.AuthorizeAsync(asCashier, Capabilities.ReceiptsReprint)).Status);

        Ok(await _h.Identity.SetUserActiveAsync(owner, managerId, false, "left"));
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(asCashier, Capabilities.PosSell)).Status);
    }

    [Fact]
    public async Task Account_changes_are_audited_with_actor_target_and_reason()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var issued = Ok(await _h.Identity.CreateUserAsync(owner, new CreateUserRequest("cashier.one", "C", [RoleTemplates.Cashier], "new hire")));
        Ok(await _h.Identity.SetUserRolesAsync(owner, issued.UserId, [RoleTemplates.Manager], "promotion"));

        var rows = _h.Database.Column(
            $"SELECT Action || '|' || ActorUserId || '|' || EntityId || '|' || Reason FROM AuditLogs WHERE Action IN ('Security.UserCreated', 'Security.RolesChanged') ORDER BY OccurredUtc, Action");
        Assert.Contains($"Security.UserCreated|{owner.UserId}|{issued.UserId}|new hire", rows);
        Assert.Contains($"Security.RolesChanged|{owner.UserId}|{issued.UserId}|promotion", rows);
    }
}
