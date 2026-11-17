using System.Globalization;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Recovery;
using Beachtro.Application.Reports;
using Beachtro.Application.Security;
using Beachtro.Application.Sync;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sync;

/// <summary>
/// In-process scheduler with persisted due keys (sync design §Scheduling; IB-23). Called about once a
/// minute and at startup, so a day or month missed while the app was closed is caught up on the next
/// run. Every job records who ran it, so jobs run as the signed-in user and wait when nobody with the
/// needed capability is signed in. Report and backup identities are idempotent, so a repeat is harmless.
/// </summary>
public sealed class JobScheduler(
    IDbContextFactory<PosDbContext> contextFactory,
    IProfileService profiles,
    IClock clock,
    WriteGate writeGate,
    IBackupService backups,
    ReportService reports) : IJobScheduler, IDisposable
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task<IReadOnlyList<string>> RunDueAsync(UserSession? session, CancellationToken cancellationToken = default)
    {
        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        try
        {
            var now = clock.UtcNowMilliseconds;
            var calendar = await profiles.GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false);
            if (!calendar.IsSuccess)
            {
                return [];
            }

            var today = calendar.Value!.Policy.BusinessDateFor(now);
            var usable = session is { IsLocked: false, IsEnded: false } ? session : null;
            var messages = new List<string>();
            await DailyBackupAsync(usable, today, messages, cancellationToken).ConfigureAwait(false);
            await MonthlyReportsAsync(usable, today, messages, cancellationToken).ConfigureAwait(false);
            return messages;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Records a business-day close as the daily report job's progress.</summary>
    internal static async Task RecordDailyCloseAsync(PosDbContext db, string businessDate, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(db, ScheduledJobKind.DailyReport, cancellationToken).ConfigureAwait(false);
        if (job.LastCompletedKey is null || string.CompareOrdinal(businessDate, job.LastCompletedKey) > 0)
        {
            job.LastCompletedKey = businessDate;
            job.LastDueKey = businessDate;
            job.State = ScheduledJobState.Idle;
            job.LastError = null;
            job.Version++;
        }
    }

    private async Task DailyBackupAsync(UserSession? session, string today, List<string> messages, CancellationToken cancellationToken)
    {
        if (await CompletedKeyAsync(ScheduledJobKind.DailyBackup, cancellationToken).ConfigureAwait(false) == today)
        {
            return;
        }

        if (session is null || !session.Has(Capabilities.BackupCreateVerify))
        {
            await UpdateAsync(ScheduledJobKind.DailyBackup, j => j.LastDueKey = today, cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await backups.CreateBackupAsync(session, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(ScheduledJobKind.DailyBackup, j =>
        {
            j.LastDueKey = today;
            if (result.IsSuccess)
            {
                j.LastCompletedKey = today;
                j.State = ScheduledJobState.Idle;
                j.LastError = null;
            }
            else if (result.Status != ResultStatus.Unauthenticated)
            {
                j.State = ScheduledJobState.Failed;
                j.LastError = result.Message;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            messages.Add($"Daily backup saved ({Path.GetFileName(result.Value!.PackagePath)}).");
        }
    }

    private async Task MonthlyReportsAsync(UserSession? session, string today, List<string> messages, CancellationToken cancellationToken)
    {
        string? firstDate;
        string? completed;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var firstSale = await db.Orders.MinAsync(o => (string?)o.BusinessDate, cancellationToken).ConfigureAwait(false);
            var firstCorrection = await db.Corrections.MinAsync(c => (string?)c.BusinessDate, cancellationToken).ConfigureAwait(false);
            firstDate = new[] { firstSale, firstCorrection }.Where(d => d is not null).DefaultIfEmpty(null).Min(StringComparer.Ordinal);
            completed = (await LoadAsync(db, ScheduledJobKind.MonthlyReport, cancellationToken).ConfigureAwait(false)).LastCompletedKey;
        }

        if (firstDate is null)
        {
            return;
        }

        var lastComplete = Month(today).AddMonths(-1);
        var month = completed is null ? Month(firstDate) : Month(completed).AddMonths(1);
        for (; month <= lastComplete; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (session is null || !session.Has(Capabilities.ReportsViewExport))
            {
                await UpdateAsync(ScheduledJobKind.MonthlyReport, j => j.LastDueKey = key, cancellationToken).ConfigureAwait(false);
                return;
            }

            var run = await reports.GenerateAsync(session, new ReportRequest(ReportType.MonthlySales, Iso(month), Iso(month.AddMonths(1))), cancellationToken).ConfigureAwait(false);
            if (!run.IsSuccess)
            {
                await UpdateAsync(ScheduledJobKind.MonthlyReport, j =>
                {
                    j.LastDueKey = key;
                    if (run.Status != ResultStatus.Unauthenticated)
                    {
                        j.State = ScheduledJobState.Failed;
                        j.LastError = run.Message;
                    }
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await reports.QueueRunFilesAsync(run.Value!.RunId, null, cancellationToken).ConfigureAwait(false);
            await UpdateAsync(ScheduledJobKind.MonthlyReport, j =>
            {
                j.LastDueKey = key;
                j.LastCompletedKey = key;
                j.State = ScheduledJobState.Idle;
                j.LastError = null;
            }, cancellationToken).ConfigureAwait(false);
            messages.Add($"Monthly report for {key} stored.");
        }
    }

    private async Task<string?> CompletedKeyAsync(ScheduledJobKind kind, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ScheduledJobs.Where(j => j.Kind == kind).Select(j => j.LastCompletedKey).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(ScheduledJobKind kind, Action<ScheduledJob> change, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await LoadAsync(db, kind, cancellationToken).ConfigureAwait(false);
        var before = (job.LastDueKey, job.LastCompletedKey, job.State, job.LastError);
        change(job);
        if (before != (job.LastDueKey, job.LastCompletedKey, job.State, job.LastError))
        {
            job.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ScheduledJob> LoadAsync(PosDbContext db, ScheduledJobKind kind, CancellationToken cancellationToken)
    {
        var job = await db.ScheduledJobs.SingleOrDefaultAsync(j => j.Kind == kind, cancellationToken).ConfigureAwait(false)
            ?? db.ScheduledJobs.Local.SingleOrDefault(j => j.Kind == kind);
        if (job is null)
        {
            job = new ScheduledJob { Kind = kind, State = ScheduledJobState.Idle };
            db.ScheduledJobs.Add(job);
        }

        return job;
    }

    private static DateOnly Month(string isoDateOrMonth) =>
        new(int.Parse(isoDateOrMonth[..4], CultureInfo.InvariantCulture), int.Parse(isoDateOrMonth[5..7], CultureInfo.InvariantCulture), 1);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose() => _running.Dispose();
}
