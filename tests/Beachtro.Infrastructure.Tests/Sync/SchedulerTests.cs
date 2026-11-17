using Beachtro.Application.Security;
using Beachtro.Infrastructure.Tests.Sales;

namespace Beachtro.Infrastructure.Tests.Sync;

/// <summary>T-16 schedule catch-up: daily backup and missed completed months.</summary>
public sealed class SchedulerTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private string? Job(string kind, string column) =>
        _h.Security.Database.Column($"SELECT {column} FROM ScheduledJobs WHERE Kind = '{kind}' AND {column} IS NOT NULL").SingleOrDefault();

    [Fact]
    public async Task The_daily_backup_runs_once_per_business_day()
    {
        Assert.Single(await _h.Scheduler.RunDueAsync(_h.Owner));
        Assert.Empty(await _h.Scheduler.RunDueAsync(_h.Owner));
        Assert.Equal(1L, _h.Count("Backups"));
        Assert.Equal("2025-11-16", Job("DailyBackup", "LastCompletedKey"));

        _h.Clock.Advance(TimeSpan.FromDays(1));
        await _h.ReauthOwnerAsync();
        await _h.Scheduler.RunDueAsync(_h.Owner);
        Assert.Equal(2L, _h.Count("Backups"));
    }

    [Fact]
    public async Task Jobs_wait_when_nobody_with_the_capability_is_signed_in()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        Assert.Empty(await _h.Scheduler.RunDueAsync(cashier));
        Assert.Empty(await _h.Scheduler.RunDueAsync(null));
        Assert.Equal(0L, _h.Count("Backups"));
        Assert.Equal("2025-11-16", Job("DailyBackup", "LastDueKey"));
        Assert.Null(Job("DailyBackup", "LastCompletedKey"));
    }

    [Fact]
    public async Task Missed_completed_months_are_caught_up_with_stored_files()
    {
        await _h.SellCashAsync((_h.Tea, 1));                         // November 2025
        Assert.DoesNotContain(await _h.Scheduler.RunDueAsync(_h.Owner), m => m.StartsWith("Monthly", StringComparison.Ordinal));

        // The app stays closed until February: November, December and January are due.
        _h.Clock.Advance(TimeSpan.FromDays(80));
        await _h.ReauthOwnerAsync();
        var messages = await _h.Scheduler.RunDueAsync(_h.Owner);
        Assert.Equal(3, messages.Count(m => m.StartsWith("Monthly", StringComparison.Ordinal)));
        Assert.Equal(3L, _h.Count("ReportRuns", "Type = 'MonthlySales'"));
        Assert.Equal(6L, _h.Count("ArtifactRecords", "Type IN ('ReportCsv', 'ReportPdf')"));
        Assert.Equal("2026-01", Job("MonthlyReport", "LastCompletedKey"));

        Assert.DoesNotContain(await _h.Scheduler.RunDueAsync(_h.Owner), m => m.StartsWith("Monthly", StringComparison.Ordinal));
    }
}
