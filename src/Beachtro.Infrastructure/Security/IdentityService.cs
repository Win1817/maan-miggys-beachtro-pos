using System.Collections.Concurrent;
using System.Security.Cryptography;
using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Security;

/// <summary>
/// Offline identity and authorization over the local database (security design §§Authentication,
/// Capability matrix, Bootstrap; IB-05). Security state changes and their audit rows commit together.
/// </summary>
public sealed class IdentityService(
    IDbContextFactory<PosDbContext> contextFactory,
    IClock clock,
    PosPasswordHasher hasher,
    WriteGate writeGate) : IIdentityService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ReauthenticationWindow = TimeSpan.FromMinutes(5);

    public const string GenericSignInFailure =
        "Sign-in failed. Check the username and password. After 5 failed attempts, wait 5 minutes before trying again.";

    private const string TemporaryPasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private const int TemporaryPasswordLength = 20;

    // Process-local throttle for usernames that do not exist (security design §Authentication).
    private readonly ConcurrentDictionary<string, (int Count, long WindowStartUtc)> _unknownNameAttempts = new(StringComparer.Ordinal);

    public async Task<bool> IsBootstrapRequiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await IsBootstrapRequiredAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Unit>> BootstrapOwnerAsync(BootstrapOwnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = UsernameRules.Validate(request.Username)
            ?? TextRules.ValidateRequired(request.DisplayName, "Display name", TextRules.NameMaximum)
            ?? PasswordPolicy.Validate(request.Password, request.Username);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        var hash = hasher.Hash(request.Password);

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsBootstrapRequiredAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return Result.Fail(ResultStatus.Conflict, "The owner account has already been created for this installation.");
        }

        var now = clock.UtcNowMilliseconds;
        var roles = await EnsureRolesAndPermissionsAsync(db, cancellationToken).ConfigureAwait(false);

        var owner = NewUser(request.Username, request.DisplayName, hash, mustChangePassword: false, now);
        db.Users.Add(owner);
        db.UserRoles.Add(new UserRole { UserId = owner.Id, RoleId = roles[RoleTemplates.Owner] });

        var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
        state.BootstrapCompletedUtc = now;
        state.Version++;

        AuditRecorder.Record(db, now, owner.Id, owner.DisplayName, "Security.OwnerBootstrapped", "User", owner.Id.ToString(), null,
            new { username = owner.Username });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<UserSession>> SignInAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var normalized = UsernameRules.Normalize(username ?? string.Empty);
        password ??= string.Empty;
        var now = clock.UtcNowMilliseconds;

        // Oversized input is rejected before any expensive hashing.
        if (normalized.Length is 0 or > UsernameRules.MaximumLength || password.Length > PasswordPolicy.MaximumLength * 2)
        {
            return Result<UserSession>.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        if (IsUnknownNameThrottled(normalized, now))
        {
            return Result<UserSession>.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            hasher.Verify(hasher.DummyHash, password);
            RecordUnknownName(normalized, now);
            await AuditFailureAsync(db, now, null, normalized, "UnknownUser", cancellationToken).ConfigureAwait(false);
            return Result<UserSession>.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        if (!user.IsActive || IsCoolingDown(user, now))
        {
            hasher.Verify(hasher.DummyHash, password);
            await AuditFailureAsync(db, now, user, normalized, user.IsActive ? "CoolingDown" : "Inactive", cancellationToken).ConfigureAwait(false);
            return Result<UserSession>.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        var check = hasher.Verify(user.PasswordHash, password);
        if (check == PasswordCheck.Failed)
        {
            var outcome = RegisterFailedAttempt(user, now);
            await AuditFailureAsync(db, now, user, normalized, outcome, cancellationToken).ConfigureAwait(false);
            return Result<UserSession>.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        user.FailedCount = 0;
        user.LockedUntilUtc = null;
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.Hash(password);
        }

        user.UpdatedUtc = now;
        user.Version++;
        AuditRecorder.Record(db, now, user.Id, user.DisplayName, "Security.SignInSucceeded", "User", user.Id.ToString(), null,
            new { rehashed = check == PasswordCheck.SuccessRehashNeeded });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var capabilities = await LoadCapabilitiesAsync(db, user.Id, cancellationToken).ConfigureAwait(false);
        return Result<UserSession>.Ok(new UserSession(
            user.Id, user.Username, user.DisplayName, user.SecurityVersion, capabilities, user.MustChangePassword, clock.MonotonicMilliseconds));
    }

    public async Task<Result<Unit>> ChangePasswordAsync(UserSession session, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var (status, user) = await ValidateSessionAsync(db, session, cancellationToken).ConfigureAwait(false);
        if (status is not null)
        {
            return status;
        }

        var invalid = PasswordPolicy.Validate(newPassword, user!.Username);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        var now = clock.UtcNowMilliseconds;
        if (hasher.Verify(user.PasswordHash, currentPassword ?? string.Empty) == PasswordCheck.Failed)
        {
            RegisterFailedAttempt(user, now);
            AuditRecorder.Record(db, now, user.Id, user.DisplayName, "Security.PasswordChangeFailed", "User", user.Id.ToString(), null, new { outcome = "WrongCurrentPassword" });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail(ResultStatus.Forbidden, "The current password is not correct.");
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return Result.Fail(ResultStatus.Validation, "Choose a password different from the current one.");
        }

        user.PasswordHash = hasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.FailedCount = 0;
        user.LockedUntilUtc = null;
        user.SecurityVersion++;
        user.UpdatedUtc = now;
        user.Version++;
        AuditRecorder.Record(db, now, user.Id, user.DisplayName, "Security.PasswordChanged", "User", user.Id.ToString(), null, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // This session continues at the new version; every other session for the account ends.
        session.PasswordChanged(user.SecurityVersion);
        return Result.Ok();
    }

    public async Task<Result<Unit>> ReauthenticateAsync(UserSession session, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var (status, user) = await ValidateSessionAsync(db, session, cancellationToken, allowLocked: true).ConfigureAwait(false);
        if (status is not null)
        {
            return status;
        }

        var now = clock.UtcNowMilliseconds;
        if (IsCoolingDown(user!, now))
        {
            return Result.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        if (hasher.Verify(user!.PasswordHash, password ?? string.Empty) == PasswordCheck.Failed)
        {
            var outcome = RegisterFailedAttempt(user, now);
            AuditRecorder.Record(db, now, user.Id, user.DisplayName, "Security.ReauthenticationFailed", "User", user.Id.ToString(), null, new { outcome });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail(ResultStatus.Unauthenticated, GenericSignInFailure);
        }

        var wasLocked = session.IsLocked;
        user.FailedCount = 0;
        user.Version++;
        AuditRecorder.Record(db, now, user.Id, user.DisplayName, wasLocked ? "Security.SessionUnlocked" : "Security.Reauthenticated", "User", user.Id.ToString(), null, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        session.MarkReauthenticated(clock.MonotonicMilliseconds);
        return Result.Ok();
    }

    public async Task<Result<Unit>> AuthorizeAsync(UserSession? session, string capability, bool requireRecentReauthentication = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await AuthorizeAsync(db, session, capability, requireRecentReauthentication, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IssuedCredential>> CreateUserAsync(UserSession session, CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var auth = await AuthorizeAsync(db, session, Capabilities.UsersManage, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<IssuedCredential>();
        }

        var invalid = UsernameRules.Validate(request.Username)
            ?? TextRules.ValidateRequired(request.DisplayName, "Display name", TextRules.NameMaximum)
            ?? TextRules.ValidateRequired(request.Reason, "Reason", TextRules.ReasonMaximum)
            ?? ValidateRoleKeys(request.RoleKeys);
        if (invalid is not null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Validation, invalid);
        }

        var delegation = CheckDelegation(session, request.RoleKeys, request.RoleKeys);
        if (delegation is not null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Forbidden, delegation);
        }

        var normalized = UsernameRules.Normalize(request.Username);
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, cancellationToken).ConfigureAwait(false))
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Conflict, "That username is already in use.");
        }

        var now = clock.UtcNowMilliseconds;
        var temporary = GenerateTemporaryPassword();
        var user = NewUser(request.Username, request.DisplayName, hasher.Hash(temporary), mustChangePassword: true, now);
        var roleIds = await RoleIdsAsync(db, cancellationToken).ConfigureAwait(false);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Users.Add(user);
        foreach (var key in request.RoleKeys.Distinct(StringComparer.Ordinal))
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleIds[key] });
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Security.UserCreated", "User", user.Id.ToString(), request.Reason,
            new { username = user.Username, roles = request.RoleKeys.Order(StringComparer.Ordinal).ToArray() });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result<IssuedCredential>.Ok(new IssuedCredential(user.Id, temporary));
    }

    public async Task<Result<IssuedCredential>> ResetPasswordAsync(UserSession session, Guid targetUserId, string reason, CancellationToken cancellationToken = default)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var auth = await AuthorizeAsync(db, session, Capabilities.UsersManage, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<IssuedCredential>();
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Validation, invalid);
        }

        if (targetUserId == session.UserId)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Validation, "Use Change password for your own account.");
        }

        var target = await db.Users.SingleOrDefaultAsync(u => u.Id == targetUserId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.NotFound, "User not found.");
        }

        var targetRoles = await RoleKeysOfAsync(db, target.Id, cancellationToken).ConfigureAwait(false);
        var delegation = CheckDelegation(session, targetRoles, targetRoles);
        if (delegation is not null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.Forbidden, delegation);
        }

        var now = clock.UtcNowMilliseconds;
        var temporary = GenerateTemporaryPassword();
        target.PasswordHash = hasher.Hash(temporary);
        target.MustChangePassword = true;
        target.FailedCount = 0;
        target.LockedUntilUtc = null;
        target.SecurityVersion++;
        target.UpdatedUtc = now;
        target.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Security.PasswordReset", "User", target.Id.ToString(), reason,
            new { username = target.Username });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<IssuedCredential>.Ok(new IssuedCredential(target.Id, temporary));
    }

    public async Task<Result<Unit>> SetUserRolesAsync(UserSession session, Guid targetUserId, IReadOnlyCollection<string> roleKeys, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleKeys);
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var auth = await AuthorizeAsync(db, session, Capabilities.UsersManage, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum) ?? ValidateRoleKeys(roleKeys);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        var target = await db.Users.SingleOrDefaultAsync(u => u.Id == targetUserId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Result.Fail(ResultStatus.NotFound, "User not found.");
        }

        var current = await RoleKeysOfAsync(db, target.Id, cancellationToken).ConfigureAwait(false);
        var desired = roleKeys.Distinct(StringComparer.Ordinal).ToArray();
        var changedRoles = current.Except(desired, StringComparer.Ordinal).Union(desired.Except(current, StringComparer.Ordinal), StringComparer.Ordinal);
        var delegation = CheckDelegation(session, current.Union(desired, StringComparer.Ordinal), changedRoles);
        if (delegation is not null)
        {
            return Result.Fail(ResultStatus.Forbidden, delegation);
        }

        if (current.Contains(RoleTemplates.Owner) && !desired.Contains(RoleTemplates.Owner) && target.IsActive
            && await CountOtherActiveOwnersAsync(db, target.Id, cancellationToken).ConfigureAwait(false) == 0)
        {
            return Result.Fail(ResultStatus.Conflict, "The last active Owner cannot lose the Owner role.");
        }

        if (current.SetEquals(desired))
        {
            return Result.Ok();
        }

        var now = clock.UtcNowMilliseconds;
        var roleIds = await RoleIdsAsync(db, cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == target.Id));
        foreach (var key in desired)
        {
            db.UserRoles.Add(new UserRole { UserId = target.Id, RoleId = roleIds[key] });
        }

        target.SecurityVersion++;
        target.UpdatedUtc = now;
        target.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Security.RolesChanged", "User", target.Id.ToString(), reason,
            new { before = current.Order(StringComparer.Ordinal).ToArray(), after = desired.Order(StringComparer.Ordinal).ToArray() });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<Unit>> SetUserActiveAsync(UserSession session, Guid targetUserId, bool active, string reason, CancellationToken cancellationToken = default)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var auth = await AuthorizeAsync(db, session, Capabilities.UsersManage, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        var target = await db.Users.SingleOrDefaultAsync(u => u.Id == targetUserId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Result.Fail(ResultStatus.NotFound, "User not found.");
        }

        var roles = await RoleKeysOfAsync(db, target.Id, cancellationToken).ConfigureAwait(false);
        var delegation = CheckDelegation(session, roles, roles);
        if (delegation is not null)
        {
            return Result.Fail(ResultStatus.Forbidden, delegation);
        }

        if (target.IsActive == active)
        {
            return Result.Ok();
        }

        if (!active && roles.Contains(RoleTemplates.Owner)
            && await CountOtherActiveOwnersAsync(db, target.Id, cancellationToken).ConfigureAwait(false) == 0)
        {
            return Result.Fail(ResultStatus.Conflict, "The last active Owner cannot be deactivated.");
        }

        var now = clock.UtcNowMilliseconds;
        target.IsActive = active;
        target.SecurityVersion++;
        target.UpdatedUtc = now;
        target.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, active ? "Security.UserActivated" : "Security.UserDeactivated",
            "User", target.Id.ToString(), reason, new { username = target.Username });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<IReadOnlyList<UserSummary>>> ListUsersAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var auth = await AuthorizeAsync(db, session, Capabilities.UsersManage, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<IReadOnlyList<UserSummary>>();
        }

        var users = await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUsername).ToListAsync(cancellationToken).ConfigureAwait(false);
        var roleRows = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Key })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<UserSummary> summaries = users
            .Select(u => new UserSummary(u.Id, u.Username, u.DisplayName, u.IsActive, u.MustChangePassword,
                roleRows.Where(r => r.UserId == u.Id).Select(r => r.Key).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        return Result<IReadOnlyList<UserSummary>>.Ok(summaries);
    }

    // ---------------------------------------------------------------- internals

    private async Task<Result<Unit>> AuthorizeAsync(PosDbContext db, UserSession? session, string capability, bool requireRecentReauthentication, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return Result.Fail(ResultStatus.Unauthenticated, "Sign in first.");
        }

        var (status, user) = await ValidateSessionAsync(db, session, cancellationToken).ConfigureAwait(false);
        if (status is not null)
        {
            return status;
        }

        if (user!.MustChangePassword || session.MustChangePassword)
        {
            return Result.Fail(ResultStatus.PasswordChangeRequired, "Change your temporary password before continuing.");
        }

        if (!session.Has(capability))
        {
            return Result.Fail(ResultStatus.Forbidden, "You do not have permission for this action.");
        }

        var nowMono = clock.MonotonicMilliseconds;
        if (requireRecentReauthentication
            && (session.ReauthenticatedMonotonicMs is not { } at || nowMono - at > (long)ReauthenticationWindow.TotalMilliseconds))
        {
            return Result.Fail(ResultStatus.ReauthenticationRequired, "Enter your password again to confirm this action.");
        }

        session.Touch(nowMono);
        return Result.Ok();
    }

    /// <summary>Returns a failure result, or (null, user) when the session is still valid.</summary>
    private static async Task<(Result<Unit>? Failure, User? User)> ValidateSessionAsync(
        PosDbContext db, UserSession session, CancellationToken cancellationToken, bool allowLocked = false)
    {
        if (session.IsEnded)
        {
            return (Result.Fail(ResultStatus.Unauthenticated, "Your session has ended. Sign in again."), null);
        }

        if (session.IsLocked && !allowLocked)
        {
            return (Result.Fail(ResultStatus.Unauthenticated, "The session is locked. Enter your password to continue."), null);
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive || user.SecurityVersion != session.SecurityVersion)
        {
            session.End();
            return (Result.Fail(ResultStatus.Unauthenticated, "Your session has ended. Sign in again."), null);
        }

        return (null, user);
    }

    private static async Task<bool> IsBootstrapRequiredAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var state = await db.InstallationState.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return state is { BootstrapCompletedUtc: null } && !await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, Guid>> EnsureRolesAndPermissionsAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var permissions = await db.Permissions.ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var key in Capabilities.All.Where(k => !permissions.ContainsKey(k)))
        {
            var permission = new Permission { Id = Guid.NewGuid(), Key = key, Description = key };
            db.Permissions.Add(permission);
            permissions[key] = permission.Id;
        }

        var roles = await db.Roles.ToDictionaryAsync(r => r.Key, r => r.Id, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var (key, grants) in RoleTemplates.Grants)
        {
            if (roles.ContainsKey(key))
            {
                continue;
            }

            var role = new Role { Id = Guid.NewGuid(), Key = key, Name = RoleTemplates.Names[key], Description = "Default template (security design matrix)" };
            db.Roles.Add(role);
            roles[key] = role.Id;
            foreach (var capability in grants)
            {
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[capability] });
            }
        }

        return roles;
    }

    private static async Task<Dictionary<string, Guid>> RoleIdsAsync(PosDbContext db, CancellationToken cancellationToken) =>
        await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.Id, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

    private static async Task<HashSet<string>> RoleKeysOfAsync(PosDbContext db, Guid userId, CancellationToken cancellationToken) =>
        (await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    private static async Task<IReadOnlySet<string>> LoadCapabilitiesAsync(PosDbContext db, Guid userId, CancellationToken cancellationToken) =>
        (await (from ur in db.UserRoles
                join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                join p in db.Permissions on rp.PermissionId equals p.Id
                where ur.UserId == userId
                select p.Key).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    private static async Task<int> CountOtherActiveOwnersAsync(PosDbContext db, Guid excludingUserId, CancellationToken cancellationToken) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               join u in db.Users on ur.UserId equals u.Id
               where r.Key == RoleTemplates.Owner && u.IsActive && u.Id != excludingUserId
               select u.Id).CountAsync(cancellationToken).ConfigureAwait(false);

    private static string? CheckDelegation(UserSession actor, IEnumerable<string> accountRoles, IEnumerable<string> rolesToManage) =>
        RoleDelegation.Check(actor, accountRoles, rolesToManage);

    private static string? ValidateRoleKeys(IReadOnlyCollection<string>? roleKeys)
    {
        if (roleKeys is null || roleKeys.Count == 0)
        {
            return "Choose at least one role.";
        }

        var unknown = roleKeys.FirstOrDefault(k => !RoleTemplates.Grants.ContainsKey(k));
        return unknown is null ? null : $"Unknown role '{unknown}'.";
    }

    private static bool IsCoolingDown(User user, long nowUtc) =>
        user.LockedUntilUtc is { } until && nowUtc < until;

    /// <summary>Counts a failure; on the fifth consecutive failure starts a 5-minute cooldown.</summary>
    private static string RegisterFailedAttempt(User user, long nowUtc)
    {
        user.FailedCount++;
        user.Version++;
        if (user.FailedCount >= MaxFailedAttempts)
        {
            user.FailedCount = 0;
            user.LockedUntilUtc = nowUtc + (long)Cooldown.TotalMilliseconds;
            return "CooldownStarted";
        }

        return "WrongPassword";
    }

    private static async Task AuditFailureAsync(PosDbContext db, long now, User? user, string attempted, string outcome, CancellationToken cancellationToken)
    {
        // Login failures are audited in their own operation, independent of any business write.
        AuditRecorder.Record(db, now, user?.Id, user?.DisplayName ?? AuditRecorder.UnknownActorName, "Security.SignInFailed", "User",
            user?.Id.ToString(), null, new { attemptedUsername = attempted.Length > 64 ? attempted[..64] : attempted, outcome });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsUnknownNameThrottled(string normalized, long nowUtc) =>
        _unknownNameAttempts.TryGetValue(normalized, out var entry)
        && entry.Count >= MaxFailedAttempts
        && nowUtc - entry.WindowStartUtc < (long)Cooldown.TotalMilliseconds;

    private void RecordUnknownName(string normalized, long nowUtc) =>
        _unknownNameAttempts.AddOrUpdate(
            normalized,
            _ => (1, nowUtc),
            (_, e) => nowUtc - e.WindowStartUtc >= (long)Cooldown.TotalMilliseconds ? (1, nowUtc) : (e.Count + 1, e.WindowStartUtc));

    private static User NewUser(string username, string displayName, string hash, bool mustChangePassword, long now) => new()
    {
        Id = Guid.NewGuid(),
        Username = username.Trim(),
        NormalizedUsername = UsernameRules.Normalize(username),
        DisplayName = displayName.Trim(),
        PasswordHash = hash,
        IsActive = true,
        MustChangePassword = mustChangePassword,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    internal static string GenerateTemporaryPassword() =>
        RandomNumberGenerator.GetString(TemporaryPasswordAlphabet, TemporaryPasswordLength);
}
