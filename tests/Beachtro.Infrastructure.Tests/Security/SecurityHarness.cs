using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Configuration;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Security;
using Beachtro.Infrastructure.Tests.Persistence;

namespace Beachtro.Infrastructure.Tests.Security;

/// <summary>Controllable wall and monotonic clock for cooldown, inactivity and reauthentication tests.</summary>
public sealed class ManualClock : IClock
{
    public long UtcNowMilliseconds { get; private set; } = Seed.Now;
    public long MonotonicMilliseconds { get; private set; } = 1_000_000;

    public void Advance(TimeSpan by)
    {
        UtcNowMilliseconds += (long)by.TotalMilliseconds;
        MonotonicMilliseconds += (long)by.TotalMilliseconds;
    }
}

/// <summary>
/// Real identity and profile services over a migrated temp SQLite database. Uses a low hash work
/// factor for speed; the production work factor is asserted separately.
/// </summary>
public sealed class SecurityHarness : IDisposable
{
    public const string OwnerUsername = "owner.test";
    public const string OwnerPassword = "correct horse battery staple";
    public const string UserPassword = "a different long passphrase";
    public const int FastIterations = 1_000;

    public SecurityHarness(int iterations = FastIterations)
    {
        Hasher = new PosPasswordHasher(iterations);
        Identity = NewIdentityService(Hasher);
        Profiles = new ProfileService(Database.Factory, Identity, Clock, Gate);
    }

    public TestDatabase Database { get; } = new();
    public ManualClock Clock { get; } = new();
    public WriteGate Gate { get; } = new();
    public PosPasswordHasher Hasher { get; }
    public IdentityService Identity { get; }
    public ProfileService Profiles { get; }

    /// <summary>A fresh service instance over the same database, as after an application restart.</summary>
    public IdentityService NewIdentityService(PosPasswordHasher? hasher = null) => new(Database.Factory, Clock, hasher ?? Hasher, Gate);

    public async Task<UserSession> BootstrapOwnerAsync()
    {
        Ok(await Identity.BootstrapOwnerAsync(new BootstrapOwnerRequest(OwnerUsername, "Test Owner", OwnerPassword)));
        return await SignInAsync(OwnerUsername, OwnerPassword);
    }

    /// <summary>Signs in and reauthenticates, so sensitive actions are allowed for the next 5 minutes.</summary>
    public async Task<UserSession> SignInAsync(string username, string password)
    {
        var session = Ok(await Identity.SignInAsync(username, password));
        Ok(await Identity.ReauthenticateAsync(session, password));
        return session;
    }

    /// <summary>Owner creates a user with the given roles; the user signs in, replaces the temporary password and reauthenticates.</summary>
    public async Task<(Guid UserId, UserSession Session)> CreateUserAsync(UserSession owner, string username, params string[] roles)
    {
        var issued = Ok(await Identity.CreateUserAsync(owner, new CreateUserRequest(username, "Test " + username, roles, "test fixture")));
        var session = Ok(await Identity.SignInAsync(username, issued.TemporaryPassword));
        Ok(await Identity.ChangePasswordAsync(session, issued.TemporaryPassword, UserPassword));
        Ok(await Identity.ReauthenticateAsync(session, UserPassword));
        return (issued.UserId, session);
    }

    public static T Ok<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, $"Expected success but got {result.Status}: {result.Message}");
        return result.Value!;
    }

    public void Dispose()
    {
        Gate.Dispose();
        Database.Dispose();
    }
}
