using System.Text;
using Beachtro.Application.Reports;
using Beachtro.Infrastructure.Reports;
using Beachtro.Infrastructure.Sync;
using Beachtro.Infrastructure.Tests.Sales;
using Microsoft.EntityFrameworkCore;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sync;

/// <summary>T-14: crash points around file creation, regeneration and holds.</summary>
public sealed class ArtifactTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private string SalePath(Guid orderId) =>
        Path.Combine(_h.Security.Database.Paths.Root, "artifacts", "transactions", "2025", "11", $"sale_{orderId:D}_v1.json");

    private byte[] Snapshot(Guid orderId) =>
        Encoding.UTF8.GetBytes(_h.Security.Database.Scalar<string>($"SELECT SnapshotJson FROM Orders WHERE Id = '{orderId}'"));

    [Fact]
    public async Task A_crash_after_rename_but_before_ready_is_completed_by_hash()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        var path = SalePath(sale.OrderId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Snapshot(sale.OrderId));   // the file made it; the database update did not

        var (ready, held) = await _h.Materializer.MaterializePendingAsync(10);
        Assert.Equal((1, 0), (ready, held));
        Assert.Equal(1L, _h.Count("ArtifactRecords", $"OrderId = '{sale.OrderId}' AND State = 'Ready'"));
    }

    [Fact]
    public async Task A_different_file_at_the_path_is_held_and_never_overwritten()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        var path = SalePath(sale.OrderId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "someone else's bytes");

        var (ready, held) = await _h.Materializer.MaterializePendingAsync(10);
        Assert.Equal((0, 1), (ready, held));
        Assert.Equal("someone else's bytes", await File.ReadAllTextAsync(path));
        Assert.Equal(1L, _h.Count("ArtifactRecords", $"OrderId = '{sale.OrderId}' AND State = 'Intent'"));
        Assert.Equal("ExistingFileDiffers", _h.Security.Database.Scalar<string>("SELECT Code FROM ArtifactHolds"));

        // A held artifact is not retried every cycle.
        Assert.Equal((0, 0), await _h.Materializer.MaterializePendingAsync(10));
    }

    [Fact]
    public async Task A_missing_file_is_regenerated_only_with_identical_bytes()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await _h.Materializer.MaterializePendingAsync(10);
        File.Delete(SalePath(sale.OrderId));

        await using var db = _h.Security.Database.NewContext();
        var artifact = await db.ArtifactRecords.AsNoTracking().SingleAsync(a => a.OrderId == sale.OrderId);
        var bytes = await _h.Materializer.ReadReadyAsync(artifact);
        Assert.Equal(Snapshot(sale.OrderId), bytes.Content);
        Assert.True(File.Exists(SalePath(sale.OrderId)));

        // A damaged copy is kept as evidence beside the regenerated file.
        await File.WriteAllTextAsync(SalePath(sale.OrderId), "damaged");
        Assert.NotNull((await _h.Materializer.ReadReadyAsync(artifact)).Content);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(SalePath(sale.OrderId))!, "*.damaged-*"));
    }

    [Fact]
    public async Task Temp_files_left_by_a_crash_are_removed()
    {
        var dir = Path.Combine(_h.Security.Database.Paths.Root, "artifacts", "transactions", "2025", "11");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "sale_x_v1.json" + ArtifactMaterializer.TempMarker + "abc"), "half");
        Assert.Equal(1, _h.Materializer.CleanupTempFiles());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_business_day_close_stores_its_csv_and_pdf_from_the_report_run()
    {
        await _h.SellCashAsync((_h.Tea, 2));
        var close = Ok(await _h.Reports.CloseBusinessDayAsync(_h.Owner, "2025-11-16"));
        var cycle = await _h.Sync.RunCycleAsync();
        Assert.Equal(3, cycle.Materialized);                         // the sale plus CSV and PDF

        await using var db = _h.Security.Database.NewContext();
        var run = await db.ReportRuns.AsNoTracking().SingleAsync();
        foreach (var format in new[] { ReportFormat.Csv, ReportFormat.Pdf })
        {
            var file = Path.Combine(_h.Security.Database.Paths.Root, "artifacts", "daily-reports", "2025", "11", ArtifactMaterializer.ReportName(run, format));
            Assert.Equal(ReportService.Render(run, format).Content, await File.ReadAllBytesAsync(file));
        }

        Assert.Equal("2025-11-16", _h.Security.Database.Scalar<string>("SELECT LastCompletedKey FROM ScheduledJobs WHERE Kind = 'DailyReport'"));
        Assert.Equal(1, close.Revision);
    }
}
