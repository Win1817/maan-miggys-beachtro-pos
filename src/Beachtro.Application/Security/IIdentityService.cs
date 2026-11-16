using Beachtro.Application.Common;

namespace Beachtro.Application.Security;

public sealed record BootstrapOwnerRequest(string Username, string DisplayName, string Password);

public sealed record CreateUserRequest(string Username, string DisplayName, IReadOnlyCollection<string> RoleKeys, string Reason);

/// <summary>The temporary password is shown once and never stored or logged in plain text.</summary>
public sealed record IssuedCredential(Guid UserId, string TemporaryPassword);

public sealed record UserSummary(Guid UserId, string Username, string DisplayName, bool IsActive, bool MustChangePassword, IReadOnlyList<string> RoleKeys);

/// <summary>
/// Offline identity, authorization and account administration (FR-001–003, FR-024; IB-05).
/// Every method authorizes inside the service from the session, never from caller-supplied IDs.
/// </summary>
public interface IIdentityService
{
    Task<bool> IsBootstrapRequiredAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the first Owner exactly once on a new installation; no default credentials exist.</summary>
    Task<Result<Unit>> BootstrapOwnerAsync(BootstrapOwnerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Generic failure for unknown, inactive, wrong-password and cooling-down accounts.</summary>
    Task<Result<UserSession>> SignInAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<Result<Unit>> ChangePasswordAsync(UserSession session, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Re-enter password: unlocks a locked session and opens the 5-minute sensitive-action window.</summary>
    Task<Result<Unit>> ReauthenticateAsync(UserSession session, string password, CancellationToken cancellationToken = default);

    /// <summary>Checks session validity against the database, then the capability and optional fresh reauthentication.</summary>
    Task<Result<Unit>> AuthorizeAsync(UserSession? session, string capability, bool requireRecentReauthentication = false, CancellationToken cancellationToken = default);

    Task<Result<IssuedCredential>> CreateUserAsync(UserSession session, CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<Result<IssuedCredential>> ResetPasswordAsync(UserSession session, Guid targetUserId, string reason, CancellationToken cancellationToken = default);

    Task<Result<Unit>> SetUserRolesAsync(UserSession session, Guid targetUserId, IReadOnlyCollection<string> roleKeys, string reason, CancellationToken cancellationToken = default);

    Task<Result<Unit>> SetUserActiveAsync(UserSession session, Guid targetUserId, bool active, string reason, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<UserSummary>>> ListUsersAsync(UserSession session, CancellationToken cancellationToken = default);
}
