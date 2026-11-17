using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Runtime;
using Beachtro.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Recovery;

public sealed record MaintenanceStatus(
    string DataRoot,
    int AppliedMigrations,
    int KnownMigrations,
    string? Mode,
    IReadOnlyList<string> ActiveOwners,
    string? LastBackup);

/// <summary>
/// Local maintenance tool (security design §Last-owner recovery; IB-27, POS-DEC-011). Reached only through
/// the separate maintenance window, which requires Windows administrator rights and the app closed. Every
/// action is preceded by a verified backup (proof of backup) and audited as the System actor together with
/// the Windows account that ran it. Not a hidden bypass: it needs administrative control of the PC.
/// </summary>
public sealed class MaintenanceService(
    IDbContextFactory<PosDbContext> contextFactory,
    BackupService backups,
    PosPasswordHasher hasher,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths)
{
    public async Task<MaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var known = db.Database.GetMigrations().Count();
        var applied = File.Exists(paths.DatabaseFile) ? BackupService.AppliedMigrations(paths.DatabaseFile).Count : 0;
        if (applied == 0 || applied > known)
        {
            return new MaintenanceStatus(paths.Root, applied, known, null, [], null);
        }

        var mode = await db.InstallationState.AsNoTracking().Select(s => s.Mode).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var owners = await (from u in db.Users
                            join ur in db.UserRoles on u.Id equals ur.UserId
                            join r in db.Roles on ur.RoleId equals r.Id
                            where r.Key == RoleTemplates.Owner && u.IsActive
                            orderby u.NormalizedUsername
                            select u.Username).ToListAsync(cancellationToken).ConfigureAwait(false);
        var last = await db.Backups.AsNoTracking().OrderByDescending(b => b.CreatedUtc).Select(b => b.RelativePath).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new MaintenanceStatus(paths.Root, applied, known, mode.ToString(), owners, last);
    }

    /// <summary>
    /// Lost last-owner password: verified backup first, then a temporary password shown once, a forced change
    /// at next sign-in, and every session ended (all accounts' security versions move on).
    /// </summary>
    public async Task<Result<IssuedCredential>> ResetOwnerPasswordAsync(string username, string windowsAccount, CancellationToken cancellationToken = default)
    {
        var normalized = UsernameRules.Normalize(username ?? string.Empty);
        await using (var check = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var isOwner = await (from u in check.Users
                                 join ur in check.UserRoles on u.Id equals ur.UserId
                                 join r in check.Roles on ur.RoleId equals r.Id
                                 where u.NormalizedUsername == normalized && u.IsActive && r.Key == RoleTemplates.Owner
                                 select u.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!isOwner)
            {
                return Result<IssuedCredential>.Fail(ResultStatus.NotFound, "No active Owner account has that username.");
            }
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var backupId = Guid.NewGuid();
        BackupService.BuiltPackage? built;
        string? problem;
        try
        {
            (built, problem) = backups.BuildPackage("pos_premaintenance", backupId, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            (built, problem) = (null, ex.Message);
        }

        if (built is null)
        {
            return Result<IssuedCredential>.Fail(ResultStatus.StorageUnavailable, "Nothing was changed: the backup before maintenance could not be written. " + problem);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        BackupService.RecordPackage(db, backupId, built, now, null, AuditRecorder.SystemActorName, "Backup.PreMaintenanceCreated");

        var target = await db.Users.SingleAsync(u => u.NormalizedUsername == normalized, cancellationToken).ConfigureAwait(false);
        var temporary = IdentityService.GenerateTemporaryPassword();
        target.PasswordHash = hasher.Hash(temporary);
        target.MustChangePassword = true;
        target.FailedCount = 0;
        target.LockedUntilUtc = null;
        target.UpdatedUtc = now;
        foreach (var user in await db.Users.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            user.SecurityVersion++;
            user.Version++;
        }

        AuditRecorder.Record(db, now, null, AuditRecorder.SystemActorName, "Maintenance.OwnerPasswordReset", "User", target.Id.ToString(),
            "Lost owner password reset with local administrator access",
            new { username = target.Username, windowsAccount, machine = Environment.MachineName, backup = Path.GetFileName(built.Package) });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<IssuedCredential>.Ok(new IssuedCredential(target.Id, temporary));
    }
}
