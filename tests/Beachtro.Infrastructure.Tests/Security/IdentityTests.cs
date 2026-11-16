using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Security;

/// <summary>T-01 identity: bootstrap, sign-in, hashing, cooldown, reset/change, sessions and lock.</summary>
public sealed class IdentityTests : IDisposable
{
    private readonly SecurityHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Owner_bootstrap_runs_once_and_seeds_the_role_templates()
    {
        Assert.True(await _h.Identity.IsBootstrapRequiredAsync());
        await _h.BootstrapOwnerAsync();
        Assert.False(await _h.Identity.IsBootstrapRequiredAsync());

        var again = await _h.Identity.BootstrapOwnerAsync(new BootstrapOwnerRequest("second.owner", "Second", OwnerPassword + "!"));
        Assert.Equal(ResultStatus.Conflict, again.Status);

        Assert.Equal(4L, _h.Database.Scalar<long>("SELECT count(*) FROM Roles"));
        Assert.Equal((long)Capabilities.All.Count, _h.Database.Scalar<long>("SELECT count(*) FROM Permissions"));
        Assert.Equal((long)RoleTemplates.Grants.Values.Sum(g => g.Count), _h.Database.Scalar<long>("SELECT count(*) FROM RolePermissions"));
        Assert.Equal(1L, _h.Database.Scalar<long>("SELECT count(*) FROM Users"));
        Assert.NotEqual(0L, _h.Database.Scalar<long>("SELECT BootstrapCompletedUtc FROM InstallationState"));
    }

    [Theory]
    [InlineData("too short pw", "at least 15")]
    [InlineData("owner.test is my password", "cannot contain the username")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa", "repeated character")]
    public async Task Bootstrap_rejects_weak_passwords(string password, string message)
    {
        var result = await _h.Identity.BootstrapOwnerAsync(new BootstrapOwnerRequest(OwnerUsername, "Test Owner", password));
        Assert.Equal(ResultStatus.Validation, result.Status);
        Assert.Contains(message, result.Message, StringComparison.Ordinal);
        Assert.True(await _h.Identity.IsBootstrapRequiredAsync());
    }

    [Fact]
    public async Task Passwords_allow_spaces_and_unicode_and_are_not_trimmed()
    {
        const string password = "  café ☕ long passphrase  ";
        Ok(await _h.Identity.BootstrapOwnerAsync(new BootstrapOwnerRequest(OwnerUsername, "Test Owner", password)));

        Assert.True((await _h.Identity.SignInAsync(OwnerUsername, password)).IsSuccess);
        Assert.False((await _h.Identity.SignInAsync(OwnerUsername, password.Trim())).IsSuccess);
        Assert.NotNull(PasswordPolicy.Validate(new string('x', 64) + new string('y', 65)));
    }

    [Fact]
    public async Task Sign_in_failures_are_indistinguishable()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (cashierId, _) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        Ok(await _h.Identity.SetUserActiveAsync(owner, cashierId, false, "left"));

        var wrongPassword = await _h.Identity.SignInAsync(OwnerUsername, "not the right passphrase");
        var unknownUser = await _h.Identity.SignInAsync("nobody.here", OwnerPassword);
        var inactive = await _h.Identity.SignInAsync("cashier.one", UserPassword);

        foreach (var r in new[] { wrongPassword, unknownUser, inactive })
        {
            Assert.Equal(ResultStatus.Unauthenticated, r.Status);
            Assert.Equal(IdentityService.GenericSignInFailure, r.Message);
        }
    }

    [Fact]
    public async Task Usernames_are_trimmed_and_case_insensitive_and_unique()
    {
        var owner = await _h.BootstrapOwnerAsync();
        Assert.True((await _h.Identity.SignInAsync("  OWNER.TEST ", OwnerPassword)).IsSuccess);

        var duplicate = await _h.Identity.CreateUserAsync(owner, new CreateUserRequest("Owner.Test", "Dup", [RoleTemplates.Cashier], "dup"));
        Assert.Equal(ResultStatus.Conflict, duplicate.Status);
    }

    [Fact]
    public void Production_hasher_uses_identity_v3_with_at_least_210000_iterations_and_unique_salts()
    {
        var hasher = new PosPasswordHasher();
        var a = hasher.Hash(OwnerPassword);
        var b = hasher.Hash(OwnerPassword);

        Assert.True(PosPasswordHasher.ReadIterationCount(a) >= 210_000);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain(OwnerPassword, a, StringComparison.Ordinal);
        Assert.Equal(PasswordCheck.Success, hasher.Verify(a, OwnerPassword));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(a, OwnerPassword + "x"));
    }

    [Fact]
    public async Task An_older_work_factor_is_upgraded_on_successful_sign_in()
    {
        await _h.BootstrapOwnerAsync();
        Assert.Equal(FastIterations, PosPasswordHasher.ReadIterationCount(_h.Database.Scalar<string>("SELECT PasswordHash FROM Users")));

        var upgraded = _h.NewIdentityService(new PosPasswordHasher(FastIterations * 2));
        Assert.True((await upgraded.SignInAsync(OwnerUsername, OwnerPassword)).IsSuccess);

        Assert.Equal(FastIterations * 2, PosPasswordHasher.ReadIterationCount(_h.Database.Scalar<string>("SELECT PasswordHash FROM Users")));
    }

    [Fact]
    public async Task Five_failures_start_a_cooldown_that_survives_restart_and_then_expires()
    {
        await _h.BootstrapOwnerAsync();
        for (var i = 0; i < IdentityService.MaxFailedAttempts; i++)
        {
            Assert.False((await _h.Identity.SignInAsync(OwnerUsername, "wrong passphrase number " + i)).IsSuccess);
        }

        Assert.False((await _h.Identity.SignInAsync(OwnerUsername, OwnerPassword)).IsSuccess);

        var afterRestart = _h.NewIdentityService();
        Assert.False((await afterRestart.SignInAsync(OwnerUsername, OwnerPassword)).IsSuccess);

        _h.Clock.Advance(IdentityService.Cooldown);
        Assert.True((await afterRestart.SignInAsync(OwnerUsername, OwnerPassword)).IsSuccess);
        Assert.Equal(0L, _h.Database.Scalar<long>("SELECT FailedCount FROM Users"));
        Assert.Equal(0L, _h.Database.Scalar<long>("SELECT count(*) FROM Users WHERE LockedUntilUtc IS NOT NULL"));
    }

    [Fact]
    public async Task Reset_forces_a_password_change_and_ends_existing_sessions()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (cashierId, cashier) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        Assert.True((await _h.Identity.AuthorizeAsync(cashier, Capabilities.PosSell)).IsSuccess);

        var issued = Ok(await _h.Identity.ResetPasswordAsync(owner, cashierId, "forgot password"));

        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(cashier, Capabilities.PosSell)).Status);
        Assert.False((await _h.Identity.SignInAsync("cashier.one", UserPassword)).IsSuccess);

        var temporary = Ok(await _h.Identity.SignInAsync("cashier.one", issued.TemporaryPassword));
        Assert.True(temporary.MustChangePassword);
        Assert.Equal(ResultStatus.PasswordChangeRequired, (await _h.Identity.AuthorizeAsync(temporary, Capabilities.PosSell)).Status);

        Ok(await _h.Identity.ChangePasswordAsync(temporary, issued.TemporaryPassword, "brand new long passphrase"));
        Assert.True((await _h.Identity.AuthorizeAsync(temporary, Capabilities.PosSell)).IsSuccess);
    }

    [Fact]
    public async Task Changing_your_own_password_keeps_this_session_and_ends_others()
    {
        await _h.BootstrapOwnerAsync();
        var first = await _h.SignInAsync(OwnerUsername, OwnerPassword);
        var second = await _h.SignInAsync(OwnerUsername, OwnerPassword);

        Ok(await _h.Identity.ChangePasswordAsync(first, OwnerPassword, "an even longer new passphrase"));

        Assert.True((await _h.Identity.AuthorizeAsync(first, Capabilities.PosSell)).IsSuccess);
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(second, Capabilities.PosSell)).Status);
    }

    [Fact]
    public async Task Inactivity_locks_the_session_and_the_password_unlocks_it()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var sessions = new SessionManager(_h.Clock);
        sessions.Start(owner);

        _h.Clock.Advance(SessionManager.InactivityLimit - TimeSpan.FromSeconds(1));
        Assert.False(sessions.EnforceInactivity());
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(sessions.EnforceInactivity());

        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Identity.AuthorizeAsync(owner, Capabilities.PosSell)).Status);
        Assert.False((await _h.Identity.ReauthenticateAsync(owner, "wrong passphrase here")).IsSuccess);
        Ok(await _h.Identity.ReauthenticateAsync(owner, OwnerPassword));
        Assert.True((await _h.Identity.AuthorizeAsync(owner, Capabilities.PosSell)).IsSuccess);
    }

    [Fact]
    public async Task Sensitive_actions_need_reauthentication_within_five_minutes()
    {
        var owner = await _h.BootstrapOwnerAsync();
        _h.Clock.Advance(IdentityService.ReauthenticationWindow + TimeSpan.FromSeconds(1));

        var result = await _h.Identity.CreateUserAsync(owner, new CreateUserRequest("cashier.one", "C", [RoleTemplates.Cashier], "hire"));
        Assert.Equal(ResultStatus.ReauthenticationRequired, result.Status);

        Ok(await _h.Identity.ReauthenticateAsync(owner, OwnerPassword));
        Assert.True((await _h.Identity.CreateUserAsync(owner, new CreateUserRequest("cashier.one", "C", [RoleTemplates.Cashier], "hire"))).IsSuccess);
    }

    [Fact]
    public async Task Audit_rows_never_contain_passwords_or_hashes()
    {
        var owner = await _h.BootstrapOwnerAsync();
        var (cashierId, _) = await _h.CreateUserAsync(owner, "cashier.one", RoleTemplates.Cashier);
        var issued = Ok(await _h.Identity.ResetPasswordAsync(owner, cashierId, "reset"));
        await _h.Identity.SignInAsync(OwnerUsername, "a wrong passphrase attempt");

        var hashes = _h.Database.Column("SELECT PasswordHash FROM Users");
        var audit = _h.Database.Column("SELECT coalesce(Reason, '') || ' ' || SafeDetailsJson || ' ' || ActorNameSnapshot FROM AuditLogs");
        Assert.NotEmpty(audit);

        foreach (var secret in hashes.Concat([OwnerPassword, UserPassword, issued.TemporaryPassword, "a wrong passphrase attempt"]))
        {
            Assert.DoesNotContain(audit, row => row.Contains(secret, StringComparison.Ordinal));
        }
    }
}
