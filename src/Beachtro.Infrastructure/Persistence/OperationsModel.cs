using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using static Beachtro.Infrastructure.Persistence.ModelBuilderHelpers;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>Printing, artifacts, synchronization, backup, recovery and audit tables.</summary>
internal static class OperationsModel
{
    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<PrintJob>(b =>
        {
            b.ToTable("PrintJobs", t => t
                .Check("Source", """
                    ("Purpose" = 'Test' AND "IsTestModel" = 1 AND "OrderId" IS NULL AND "CorrectionId" IS NULL)
                    OR ("Purpose" <> 'Test' AND "IsTestModel" = 0
                        AND (("OrderId" IS NULL AND "CorrectionId" IS NOT NULL) OR ("OrderId" IS NOT NULL AND "CorrectionId" IS NULL)))
                    """)
                .Check("ReprintReason", """ "Purpose" <> 'Reprint' OR ("Reason" IS NOT NULL AND length(trim("Reason")) > 0) """)
                .Check("Attempts", """ "AttemptCount" >= 0 AND "TemplateVersion" >= 1 """)
                .Check("Model", Sha256("ModelSha256")));
            b.HasIndex(x => x.RequestId).IsUnique();
            b.HasIndex(x => x.OrderId).IsUnique().HasFilter(""" "Purpose" = 'Original' AND "OrderId" IS NOT NULL """).HasDatabaseName("UX_PrintJobs_OriginalPerOrder");
            b.HasIndex(x => x.CorrectionId).IsUnique().HasFilter(""" "Purpose" = 'Original' AND "CorrectionId" IS NOT NULL """).HasDatabaseName("UX_PrintJobs_OriginalPerCorrection");
            b.HasIndex(x => x.State);
            b.Fk<PrintJob, Order>(x => x.OrderId);
            b.Fk<PrintJob, Correction>(x => x.CorrectionId);
            b.Fk<PrintJob, User>(x => x.CreatedByUserId);
        });

        mb.Entity<ReportRun>(b =>
        {
            b.ToTable("ReportRuns", t => t
                .Check("Range", IsoDate("RangeStartDate") + " AND " + IsoDate("RangeEndExclusiveDate") + """ AND "RangeEndExclusiveDate" > "RangeStartDate" """)
                .Check("Revision", """ "Revision" >= 1 """)
                .Check("Hashes", Sha256("FiltersSha256") + " AND " + Sha256("SnapshotSha256")));
            b.HasIndex(x => new { x.Type, x.RangeStartDate, x.RangeEndExclusiveDate, x.FiltersSha256, x.Revision }).IsUnique();
            b.Fk<ReportRun, PolicyVersion>(x => x.CalendarPolicyId);
            b.Fk<ReportRun, User>(x => x.GeneratedByUserId);
        });

        mb.Entity<ArtifactRecord>(b =>
        {
            b.ToTable("ArtifactRecords", t => t
                .Check("Source", """
                    ("Type" = 'SaleJson' AND "OrderId" IS NOT NULL AND "CorrectionId" IS NULL AND "ReportRunId" IS NULL AND "BackupId" IS NULL AND "SourceId" = "OrderId")
                    OR ("Type" = 'CorrectionJson' AND "CorrectionId" IS NOT NULL AND "OrderId" IS NULL AND "ReportRunId" IS NULL AND "BackupId" IS NULL AND "SourceId" = "CorrectionId")
                    OR ("Type" IN ('ReportCsv', 'ReportPdf') AND "ReportRunId" IS NOT NULL AND "OrderId" IS NULL AND "CorrectionId" IS NULL AND "BackupId" IS NULL AND "SourceId" = "ReportRunId")
                    OR ("Type" = 'Backup' AND "BackupId" IS NOT NULL AND "OrderId" IS NULL AND "CorrectionId" IS NULL AND "ReportRunId" IS NULL AND "SourceId" = "BackupId")
                    """)
                .Check("Materialized", """
                    ("State" = 'Intent' AND "Sha256" IS NULL AND "SizeBytes" IS NULL AND "MaterializedUtc" IS NULL)
                    OR ("State" = 'Ready' AND "RelativePath" IS NOT NULL AND "Sha256" IS NOT NULL AND "SizeBytes" IS NOT NULL
                        AND "SizeBytes" >= 0 AND "MaterializedUtc" IS NOT NULL
                        AND length("Sha256") = 64 AND "Sha256" NOT GLOB '*[^0-9a-f]*')
                    """)
                .Check("LogicalKey", NotBlank("LogicalKey")));
            b.HasIndex(x => x.LogicalKey).IsUnique();
            b.HasIndex(x => x.OrderId);
            b.HasIndex(x => x.CorrectionId);
            b.HasIndex(x => x.ReportRunId);
            b.HasIndex(x => x.BackupId);
            b.Fk<ArtifactRecord, Order>(x => x.OrderId);
            b.Fk<ArtifactRecord, Correction>(x => x.CorrectionId);
            b.Fk<ArtifactRecord, ReportRun>(x => x.ReportRunId);
            b.Fk<ArtifactRecord, Backup>(x => x.BackupId);
        });

        mb.Entity<SyncQueueItem>(b =>
        {
            b.ToTable("SyncQueue", t => t
                .Check("Lease", """
                    ("State" = 'InProgress' AND "LeaseOwner" IS NOT NULL AND "LeaseExpiresUtc" IS NOT NULL)
                    OR ("State" <> 'InProgress' AND "LeaseOwner" IS NULL AND "LeaseExpiresUtc" IS NULL)
                    """)
                .Check("Succeeded", """ "State" <> 'Succeeded' OR ("RemoteFileId" IS NOT NULL AND "CompletedUtc" IS NOT NULL) """)
                .Check("Counters", """ "AttemptCount" >= 0 AND "Version" >= 1 """));
            b.HasIndex(x => x.ArtifactId).IsUnique();
            b.HasIndex(x => new { x.State, x.NextAttemptUtc });
            b.HasIndex(x => x.LeaseExpiresUtc).HasFilter(""" "State" = 'InProgress' """).HasDatabaseName("IX_SyncQueue_InProgressLease");
            b.Fk<SyncQueueItem, ArtifactRecord>(x => x.ArtifactId);
        });

        mb.Entity<SyncAttempt>(b =>
        {
            b.ToTable("SyncHistory", t => t
                .Check("Outcome", """ ("FinishedUtc" IS NULL) = ("Outcome" IS NULL) """)
                .Check("AttemptNo", """ "AttemptNo" >= 1 """));
            b.HasIndex(x => new { x.QueueId, x.AttemptNo }).IsUnique();
            b.Fk<SyncAttempt, SyncQueueItem>(x => x.QueueId);
        });

        mb.Entity<Backup>(b =>
        {
            b.ToTable("Backups", t => t
                .Check("Size", """ "SizeBytes" >= 0 AND "SchemaVersion" >= 1 """)
                .Check("Sha256", Sha256("Sha256"))
                .Check("Verified", """ "VerificationState" <> 'Verified' OR "VerifiedUtc" IS NOT NULL """));
            b.HasIndex(x => x.RelativePath).IsUnique();
            b.HasIndex(x => x.CreatedUtc);
        });

        mb.Entity<ScheduledJob>(b =>
        {
            b.ToTable("ScheduledJobs", t => t.Check("Version", """ "Version" >= 1 """));
            b.HasKey(x => x.Kind);
        });

        mb.Entity<RecoveryRun>(b =>
        {
            b.ToTable("RecoveryRuns", t => t
                .Check("Finished", """ ("Disposition" = 'Hold') OR "FinishedUtc" IS NOT NULL """)
                .Check("Times", """ "FinishedUtc" IS NULL OR "FinishedUtc" >= "StartedUtc" """));
            b.Fk<RecoveryRun, User>(x => x.ActorUserId);
            b.Fk<RecoveryRun, Backup>(x => x.SourceBackupId);
        });

        mb.Entity<AuditLog>(b =>
        {
            b.ToTable("AuditLogs", t => t
                .Check("Action", NotBlank("Action") + " AND " + NotBlank("EntityType"))
                .Check("Actor", NotBlank("ActorNameSnapshot")));
            b.HasIndex(x => new { x.OccurredUtc, x.Action });
            b.HasIndex(x => x.ActorUserId);
            b.HasIndex(x => x.RequestId);
            b.Fk<AuditLog, User>(x => x.ActorUserId);
        });
    }
}
