using System.Security.Cryptography;
using Beachtro.Application.Ports;

namespace Beachtro.Application.Security;

/// <summary>
/// In-memory session (security design §Sessions). Holds no password or token. Capabilities are a
/// snapshot; every protected command rechecks the account and its SecurityVersion in the database.
/// </summary>
public sealed class UserSession
{
    public UserSession(
        Guid userId,
        string username,
        string displayName,
        int securityVersion,
        IReadOnlySet<string> capabilities,
        bool mustChangePassword,
        long issuedMonotonicMs)
    {
        SessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        SecurityVersion = securityVersion;
        Capabilities = capabilities;
        MustChangePassword = mustChangePassword;
        IssuedMonotonicMs = issuedMonotonicMs;
        LastActivityMonotonicMs = issuedMonotonicMs;
    }

    public string SessionId { get; }
    public Guid UserId { get; }
    public string Username { get; }
    public string DisplayName { get; }
    public int SecurityVersion { get; private set; }
    public IReadOnlySet<string> Capabilities { get; }
    public bool MustChangePassword { get; private set; }
    public long IssuedMonotonicMs { get; }
    public long LastActivityMonotonicMs { get; private set; }
    public long? ReauthenticatedMonotonicMs { get; private set; }
    public bool IsLocked { get; private set; }
    public bool IsEnded { get; private set; }

    public bool Has(string capability) => Capabilities.Contains(capability);

    public void Touch(long monotonicMs) => LastActivityMonotonicMs = Math.Max(LastActivityMonotonicMs, monotonicMs);

    public void MarkReauthenticated(long monotonicMs)
    {
        ReauthenticatedMonotonicMs = monotonicMs;
        IsLocked = false;
        Touch(monotonicMs);
    }

    /// <summary>A password change by this session's owner keeps the session valid at the new version.</summary>
    public void PasswordChanged(int newSecurityVersion)
    {
        SecurityVersion = newSecurityVersion;
        MustChangePassword = false;
    }

    public void Lock()
    {
        IsLocked = true;
        ReauthenticatedMonotonicMs = null;
    }

    public void End()
    {
        IsEnded = true;
        Lock();
    }
}

/// <summary>
/// Holds the single interactive session for the terminal and applies the inactivity lock
/// (B-03 provisional: 10 minutes). Locking never aborts an in-flight commit; resuming requires
/// the password again.
/// </summary>
public sealed class SessionManager(IClock clock)
{
    public static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(10);

    public UserSession? Current { get; private set; }

    public void Start(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Current?.End();
        Current = session;
    }

    public void RecordActivity() => Current?.Touch(clock.MonotonicMilliseconds);

    /// <summary>Locks the session if idle past the limit. Returns true when locked.</summary>
    public bool EnforceInactivity()
    {
        if (Current is null || Current.IsLocked)
        {
            return Current?.IsLocked ?? false;
        }

        if (clock.MonotonicMilliseconds - Current.LastActivityMonotonicMs >= (long)InactivityLimit.TotalMilliseconds)
        {
            Current.Lock();
            return true;
        }

        return false;
    }

    public void Lock() => Current?.Lock();

    public void SignOut()
    {
        Current?.End();
        Current = null;
    }
}
