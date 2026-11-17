using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sync;

/// <summary>
/// Upload state after a database restore (sync design §Restore runbook step 6). The restored database
/// can be older than the remote side, so: in-flight work is released, an identity found in the live
/// upload journal is adopted (the next attempt reads that ID back before any create), and when the
/// journal is gone or damaged, items without a known identity are held instead of being created
/// under a fresh ID.
/// </summary>
public static class SyncReconciler
{
    public sealed record Summary(int Reset, int Adopted, int Held);

    public static async Task<Summary> AfterRestoreAsync(PosDbContext db, UploadJournal journal, long now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);
        var state = journal.Read();
        var anyIdentity = await db.SyncQueue.AnyAsync(q => q.RemoteFileId != null, cancellationToken).ConfigureAwait(false);
        var rows = await (from q in db.SyncQueue
                          join a in db.ArtifactRecords on q.ArtifactId equals a.Id
                          where q.State != SyncState.Succeeded
                          select new { Item = q, a.LogicalKey, a.Sha256 })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int reset = 0, adopted = 0, held = 0;
        foreach (var row in rows)
        {
            var q = row.Item;
            if (q.State == SyncState.InProgress)
            {
                q.State = SyncState.Pending;
                q.LeaseOwner = null;
                q.LeaseExpiresUtc = null;
                q.NextAttemptUtc = now;
                reset++;
            }

            if (q.RemoteFileId is null && q.State is SyncState.Pending or SyncState.Failed)
            {
                if (!state.Trustworthy || (!state.Exists && anyIdentity))
                {
                    q.State = SyncState.RecoveryHold;
                    q.LastErrorCode = state.Trustworthy ? SyncService.Codes.IdentityJournalMissing : SyncService.Codes.IdentityJournalDamaged;
                    held++;
                }
                else if (row.Sha256 is not null && state.Find(q.AccountBindingId, row.LogicalKey, row.Sha256) is { } known)
                {
                    q.RemoteFileId = known.RemoteId;
                    adopted++;
                }
            }

            q.Version++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new Summary(reset, adopted, held);
    }
}
