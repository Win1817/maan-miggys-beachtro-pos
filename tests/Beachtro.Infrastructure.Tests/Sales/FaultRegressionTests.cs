using Beachtro.Application.Common;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Sales;
using Beachtro.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>
/// IB-26 fault regression against real SQLite and real files (see design/FAULT_REGRESSION.md for the full
/// fault-to-test matrix). Each fault must leave no partial financial record, and the same request must
/// complete exactly once after the fault clears.
/// </summary>
public sealed class FaultRegressionTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_unwritable_number_journal_refuses_the_sale_and_writes_nothing()
    {
        await _h.SellCashAsync((_h.Tea, 1));                                 // the journal now exists
        var journal = _h.Security.Database.Paths.NumberJournalFile;
        var stock = _h.Stock(_h.Tea);
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));

        File.SetAttributes(journal, FileAttributes.ReadOnly);               // disk full / permission lost
        try
        {
            var refused = await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00));
            Assert.Equal(ResultStatus.StorageUnavailable, refused.Status);
            Assert.Equal(1L, _h.Count("Orders"));
            Assert.Equal(stock, _h.Stock(_h.Tea));
        }
        finally
        {
            File.SetAttributes(journal, FileAttributes.Normal);
        }

        // The same request completes once the fault clears, with the next number.
        var sale = Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00)));
        Assert.EndsWith("-000002", sale.TransactionNumber, StringComparison.Ordinal);
        Assert.Equal(2L, _h.Count("Orders"));
    }

    [Fact]
    public async Task A_database_held_busy_by_another_writer_leaves_no_partial_sale_and_the_retry_completes_once()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));

        using (var other = new SqliteConnection($"Data Source={_h.Security.Database.DatabaseFile};Pooling=False"))
        {
            other.Open();
            using var begin = other.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";                           // another process holds the write lock
            begin.ExecuteNonQuery();

            var busy = await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00));
            Assert.Equal(ResultStatus.StorageUnavailable, busy.Status);

            using var rollback = other.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        Assert.Equal(0L, _h.Count("Orders"));
        var sale = Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00)));
        Assert.Equal(sale.OrderId, Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00))).OrderId);
        Assert.Equal(1L, _h.Count("Orders"));
        Assert.Equal(9_000L, _h.Stock(_h.Tea));
    }

    [Fact]
    public async Task A_session_ended_between_prepare_and_complete_cannot_complete_and_leaves_the_request_prepared()
    {
        var (cashierId, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(cashier, requestId, SalesHarness.Cart((_h.Tea, 1))));

        await _h.ReauthOwnerAsync();
        Ok(await _h.Security.Identity.SetUserActiveAsync(_h.Owner, cashierId, false, "left the business"));

        Assert.Equal(ResultStatus.Unauthenticated, (await _h.Checkout.CompleteAsync(cashier, requestId, new CashPayment(100_00))).Status);
        Assert.Equal(0L, _h.Count("Orders"));
        Assert.Equal("Prepared", _h.Security.Database.Scalar<string>($"SELECT State FROM CheckoutRequests WHERE Id = '{requestId}'"));

        // A manager or owner can still close it with a reason.
        Ok(await _h.Checkout.AbandonAsync(_h.Owner, requestId, "cashier left before taking payment"));
    }

    [Fact]
    public async Task An_unwritable_artifact_folder_never_blocks_selling_and_files_appear_once_it_clears()
    {
        var root = Path.Combine(_h.Security.Database.Paths.Root, "artifacts", "transactions");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        await File.WriteAllTextAsync(root, "a file where the folder should be");   // the folder cannot be created

        var sale = await _h.SellCashAsync((_h.Tea, 1));                       // selling does not depend on files
        var (ready, _) = await _h.Materializer.MaterializePendingAsync(10);
        Assert.Equal(0, ready);
        Assert.Equal(1L, _h.Count("ArtifactRecords", $"OrderId = '{sale.OrderId}' AND State = 'Intent'"));

        File.Delete(root);
        Assert.Equal(1, (await _h.Materializer.MaterializePendingAsync(10)).Ready);
        Assert.Equal(1L, _h.Count("ArtifactRecords", $"OrderId = '{sale.OrderId}' AND State = 'Ready'"));
    }

    [Fact]
    public async Task A_refund_prepared_before_a_restart_completes_once_after_it()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var requestId = Guid.NewGuid();
        var item = _h.Security.Database.Scalar<string>($"SELECT Id FROM OrderItems WHERE OrderId = '{sale.OrderId}'");
        var input = new CorrectionInput(sale.OrderId, CorrectionKind.Refund,
            [new CorrectionLineInput(Guid.Parse(item), 1000, StockDisposition.Return)], "customer changed mind");
        await _h.ReauthOwnerAsync();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, requestId, input));

        // The application restarts: a new service instance over the same database.
        var s = _h.Security;
        var restarted = new CorrectionService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        var first = Ok(await restarted.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));
        var again = Ok(await restarted.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));
        Assert.Equal(first.CorrectionId, again.CorrectionId);
        Assert.Equal(1L, _h.Count("Corrections"));
        Assert.Equal(9_000L, _h.Stock(_h.Tea));
    }

    [Fact]
    public async Task Closing_the_day_during_an_upload_outage_keeps_every_file_and_uploads_each_once_later()
    {
        Ok(await _h.Sync.SetSimulatedDestinationAsync(_h.Owner, true));
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        Ok(await _h.Reports.CloseBusinessDayAsync(_h.Owner, "2025-11-16"));

        _h.Remote.Offline = true;
        var outage = await _h.Sync.RunCycleAsync();
        Assert.Equal(3, outage.Materialized);                               // sale JSON, report CSV and PDF
        Assert.Equal(0, outage.Uploaded);
        Assert.Equal(3, outage.Retrying);
        _h.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, (await _h.Sync.RunCycleAsync()).Uploaded);       // still offline: nothing lost, nothing failed
        Assert.Equal(0L, _h.Count("SyncQueue", "State IN ('Failed', 'RecoveryHold')"));

        // Connectivity returns; every file is uploaded exactly once.
        _h.Remote.Offline = false;
        for (var i = 0; i < 5 && _h.Count("SyncQueue", "State <> 'Succeeded'") > 0; i++)
        {
            _h.Clock.Advance(TimeSpan.FromHours(1));
            await _h.Sync.RunCycleAsync();
        }

        Assert.Equal(0L, _h.Count("SyncQueue", "State <> 'Succeeded'"));
        Assert.Equal(3, _h.Remote.Files.Count);
        Assert.Equal(1, _h.Remote.CountFor($"sale/{sale.OrderId:D}/schema1"));
    }
}
