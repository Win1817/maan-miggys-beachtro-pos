using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Beachtro.Application.Common;
using Beachtro.Application.Runtime;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Recovery;
using Beachtro.Infrastructure.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>
/// IB-27 (POS-DEC-011): manual update with a verified pre-update backup and automatic revert on failure, an
/// older version refusing newer data and rolling back only through a pre-update backup, the missing-database
/// guard, private data folder permissions, and the local maintenance reset.
/// </summary>
public sealed class UpdateAndMaintenanceTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private LocalDataPaths Paths => _h.Security.Database.Paths;

    private UpdateManager Updates => new(_h.Security.Database.Factory, _h.Backups, _h.Clock, Paths);

    private MaintenanceService Maintenance =>
        new(_h.Security.Database.Factory, _h.Backups, _h.Security.Hasher, _h.Clock, _h.Security.Gate, Paths);

    /// <summary>Makes the database look like it was left by the previous version (latest migration rolled back).</summary>
    private string RollBackLatestMigration()
    {
        using var db = _h.Security.Database.NewContext();
        var all = db.Database.GetMigrations().ToArray();
        db.GetService<IMigrator>().Migrate(all[^2]);
        return all[^1];
    }

    private void AddFutureMigration() =>
        _h.Security.Database.Execute("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FutureChange', '99.0.0')");

    [Fact]
    public async Task An_update_takes_a_verified_backup_first_then_migrates_and_records_itself()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var latest = RollBackLatestMigration();

        var inspection = Updates.Inspect();
        Assert.Equal(DatabaseCondition.NeedsUpdate, inspection.Condition);
        Assert.Equal([latest], inspection.Pending);

        var pre = Updates.PrepareUpdate(inspection);
        Assert.StartsWith(UpdateManager.PreUpdatePrefix, Path.GetFileName(pre.PackagePath), StringComparison.Ordinal);
        Assert.DoesNotContain(latest, pre.FromMigrations);
        Ok(await _h.Backups.ValidateCandidateAsync(_h.Owner, pre.PackagePath)).Problems.ToList().ForEach(p => Assert.Fail(p));

        new DatabaseInitializer(_h.Security.Database.Factory, _h.Clock).Initialize(RuntimeProfile.Test);
        Updates.RecordUpdate(pre, inspection);

        Assert.Equal(DatabaseCondition.Current, Updates.Inspect().Condition);
        Assert.Equal(1L, _h.Count("Backups", $"RelativePath LIKE 'backups/{UpdateManager.PreUpdatePrefix}%'"));
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'System.Updated' AND ActorNameSnapshot = 'System' AND ActorUserId IS NULL"));
        Assert.Equal(1L, _h.Count("Orders"));
    }

    [Fact]
    public async Task A_failed_update_puts_the_pre_update_database_back_and_keeps_the_failed_one()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        RollBackLatestMigration();
        var inspection = Updates.Inspect();
        var pre = Updates.PrepareUpdate(inspection);

        // The "migration" half-ran before failing: a table created, data changed.
        _h.Security.Database.Execute("CREATE TABLE HalfMigrated (X INTEGER); UPDATE Products SET Name = 'damaged';");
        Assert.Equal(1L, _h.Count("sqlite_master", "name = 'HalfMigrated'"));

        var evidence = Updates.RevertFailedUpdate(pre);
        Assert.True(File.Exists(Path.Combine(evidence, "pos.db")));
        Assert.Equal(0L, _h.Count("sqlite_master", "name = 'HalfMigrated'"));
        Assert.Equal(0L, _h.Count("Products", "Name = 'damaged'"));
        Assert.Equal(1L, _h.Count("Orders"));
        Assert.Equal(inspection.Applied, BackupService.AppliedMigrations(Paths.DatabaseFile));
        Assert.Equal(DatabaseCondition.NeedsUpdate, Updates.Inspect().Condition);
    }

    [Fact]
    public async Task An_older_version_refuses_newer_data_and_rolls_back_only_through_a_backup_from_before_the_update()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var before = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));
        Assert.Equal(ResultStatus.Forbidden,
            (await _h.Backups.RestoreForUpdateRollbackAsync(before.PackagePath, OwnerUsername, OwnerPassword, "not needed")).Status);

        await _h.SellCashAsync((_h.Cookie, 1));                              // recorded by the "newer" version
        AddFutureMigration();
        var inspection = Updates.Inspect();
        Assert.Equal(DatabaseCondition.NewerThanApp, inspection.Condition);
        Assert.Equal(["29990101000000_FutureChange"], inspection.Unknown);

        Assert.Equal(ResultStatus.Unauthenticated,
            (await _h.Backups.RestoreForUpdateRollbackAsync(before.PackagePath, OwnerUsername, "not the password at all", "rollback")).Status);
        var rolled = Ok(await _h.Backups.RestoreForUpdateRollbackAsync(before.PackagePath, OwnerUsername, OwnerPassword, "new version broke printing"));

        Assert.True(File.Exists(Path.Combine(rolled.PreservedFolder, "pos.db")));       // the newer data is kept as evidence
        Assert.DoesNotContain("29990101000000_FutureChange", BackupService.AppliedMigrations(Paths.DatabaseFile));
        Assert.Equal(InstallationMode.RecoveryRequired.ToString(), _h.Security.Database.Scalar<string>("SELECT Mode FROM InstallationState"));
        Assert.Equal(1L, _h.Count("Orders"));
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Recovery.UpdateRolledBack'"));

        // The live journal still knows the later sale's number, so it is never reused.
        Assert.Empty(rolled.BlockedSeries);
        Assert.Equal(2L, _h.Security.Database.Scalar<long>("SELECT LastReservedSequence FROM NumberSeries"));
    }

    [Fact]
    public async Task A_missing_database_on_a_used_pc_is_never_treated_as_a_first_run()
    {
        await _h.SellCashAsync((_h.Tea, 1));                                 // the number journal now exists
        SqliteConnection.ClearAllPools();
        File.Delete(Paths.DatabaseFile);
        Assert.Equal(DatabaseCondition.MissingExpected, Updates.Inspect().Condition);
    }

    [Fact]
    public void The_data_folder_is_made_private_and_a_broader_grant_is_reported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Paths.Root, "acl-check");
        Directory.CreateDirectory(root);
        var protection = DataFolderSecurity.Protect(root);
        Assert.True(protection.IsPrivate, string.Join(" ", protection.Problems));

        GrantEveryoneWrite(root);
        var check = DataFolderSecurity.Check(root);
        Assert.False(check.IsPrivate);
        Assert.Contains(check.Problems, p => p.Contains("can change files", StringComparison.Ordinal));

        Assert.True(DataFolderSecurity.Protect(root).IsPrivate);             // the next start fixes it again
    }

    [SupportedOSPlatform("windows")]
    private static void GrantEveryoneWrite(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public async Task The_maintenance_reset_backs_up_first_issues_a_one_time_password_and_ends_every_session()
    {
        var owner = _h.Owner;
        Assert.Equal(ResultStatus.NotFound, (await Maintenance.ResetOwnerPasswordAsync("nobody", "PC\\admin")).Status);

        var issued = Ok(await Maintenance.ResetOwnerPasswordAsync(OwnerUsername, "PC\\admin"));

        Assert.Equal(1L, _h.Count("Backups", "RelativePath LIKE 'backups/pos_premaintenance%'"));
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Maintenance.OwnerPasswordReset' AND ActorNameSnapshot = 'System' AND ActorUserId IS NULL"));
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Security.Identity.AuthorizeAsync(owner, Application.Security.Capabilities.PosSell)).Status);
        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Security.Identity.SignInAsync(OwnerUsername, OwnerPassword)).Status);
        var session = Ok(await _h.Security.Identity.SignInAsync(OwnerUsername, issued.TemporaryPassword));
        Assert.True(session.MustChangePassword);

        var status = await Maintenance.GetStatusAsync();
        Assert.Equal([OwnerUsername], status.ActiveOwners);
        Assert.Equal(status.KnownMigrations, status.AppliedMigrations);
    }
}
