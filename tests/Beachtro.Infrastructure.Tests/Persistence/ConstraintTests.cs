using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Tests.Persistence;

/// <summary>
/// Database design §Enforcement 1–3: tests deliberately bypass services to prove primitive
/// constraints and immutability guards hold on the real SQLite provider.
/// </summary>
public sealed class ConstraintTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SeededSale _sale;

    public ConstraintTests()
    {
        _sale = Seed.Sale(_db);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Asserts SQLite refused the write and returns its message.</summary>
    private static string Refused(Action write)
    {
        var ex = Assert.ThrowsAny<Exception>(write);
        var sqlite = ex as SqliteException ?? ex.InnerException as SqliteException;
        Assert.True(sqlite is not null, $"Expected a SQLite refusal but got {ex.GetType().Name}: {ex.Message}");
        return sqlite!.Message;
    }

    private void Save(params object[] entities)
    {
        using var db = _db.NewContext();
        db.AddRange(entities);
        db.SaveChanges();
    }

    // ---- Valid path and representation ----

    [Fact]
    public void Seeded_sale_round_trips_with_lowercase_uuid_text_and_integer_money()
    {
        using var db = _db.NewContext();
        var order = db.Orders.AsNoTracking().Single(o => o.Id == _sale.OrderId);

        Assert.Equal(200_00, order.TotalM);
        Assert.Equal("MB-20261116-000001", order.TransactionNumber);
        Assert.Equal(_sale.OrderId.ToString(), _db.Scalar<string>("SELECT Id FROM Orders"));
        Assert.Equal("integer", _db.Scalar<string>("SELECT typeof(TotalM) FROM Orders"));
        Assert.Equal("text", _db.Scalar<string>("SELECT typeof(Id) FROM Orders"));
        Assert.Equal("Completed", _db.Scalar<string>("SELECT State FROM CheckoutRequests"));
    }

    [Fact]
    public void Uppercase_or_malformed_uuid_text_is_rejected()
    {
        var upper = _sale.ProductId.ToString().ToUpperInvariant();
        Assert.Contains("CHECK constraint failed", Refused(() => _db.Execute($"UPDATE Inventory SET ProductId = '{upper}'")));
        Assert.Contains("CHECK constraint failed", Refused(() => _db.Execute("UPDATE Users SET Id = 'not-a-uuid'")));
    }

    [Fact]
    public void Enum_and_boolean_columns_only_accept_defined_values()
    {
        Assert.Contains("CK_NumberSeries_Status_Domain", Refused(() => _db.Execute("UPDATE NumberSeries SET Status = 'Bogus'")));
        Assert.Contains("CK_Products_TrackStock_Domain", Refused(() => _db.Execute("UPDATE Products SET TrackStock = 2")));
    }

    // ---- Immutability guards ----

    [Theory]
    [InlineData("UPDATE Orders SET TotalM = TotalM", "Orders rows are immutable")]
    [InlineData("DELETE FROM Orders", "Orders rows cannot be deleted")]
    [InlineData("UPDATE OrderItems SET NameSnapshot = 'Renamed'", "OrderItems rows are immutable")]
    [InlineData("UPDATE Payments SET Reference = 'x'", "Payments rows are immutable")]
    [InlineData("DELETE FROM InventoryMovements", "InventoryMovements rows cannot be deleted")]
    [InlineData("UPDATE AuditLogs SET Action = 'Edited'", "AuditLogs rows are immutable")]
    [InlineData("DELETE FROM AuditLogs", "AuditLogs rows cannot be deleted")]
    [InlineData("UPDATE CheckoutRequests SET ExternalEvidenceJson = '{}'", "CheckoutRequests: resolved requests are final")]
    [InlineData("DELETE FROM CheckoutRequests", "CheckoutRequests rows cannot be deleted")]
    [InlineData("UPDATE NumberSeries SET LastReservedSequence = 0", "NumberSeries: sequence cannot decrease")]
    [InlineData("UPDATE NumberSeries SET Prefix = 'XX'", "NumberSeries: identity columns are immutable")]
    [InlineData("DELETE FROM NumberSeries", "NumberSeries rows cannot be deleted")]
    [InlineData("UPDATE BusinessProfiles SET RegisteredName = 'Changed'", "BusinessProfiles: a revision used by a sale is immutable")]
    [InlineData("UPDATE PolicyVersions SET CanonicalPayloadJson = '{}'", "PolicyVersions: identity columns are immutable")]
    public void Financial_history_cannot_be_edited_or_deleted(string sql, string message)
    {
        Assert.Contains(message, Refused(() => _db.Execute(sql)));
    }

    [Fact]
    public void Ef_update_of_a_completed_order_is_refused()
    {
        using var db = _db.NewContext();
        var order = db.Orders.Single();
        order.LegalInvoiceNumber = "SNEAKY-EDIT";
        Assert.Contains("Orders rows are immutable", Refused(() => db.SaveChanges()));
    }

    [Fact]
    public void Number_series_moves_forward_only()
    {
        Assert.Equal(1, _db.Execute("UPDATE NumberSeries SET LastReservedSequence = 2"));
        Assert.Contains("CHECK constraint failed", Refused(() => _db.Execute("UPDATE NumberSeries SET LastReservedSequence = 1000000")));
    }

    [Fact]
    public void Policy_interval_closes_once_and_is_then_frozen()
    {
        Assert.Equal(1, _db.Execute($"UPDATE PolicyVersions SET EffectiveToUtc = {Seed.Now + 1}"));
        Assert.Contains("a closed revision cannot change", Refused(() => _db.Execute($"UPDATE PolicyVersions SET EffectiveToUtc = {Seed.Now + 2}")));
        Assert.Contains("PolicyVersions rows cannot be deleted", Refused(() => _db.Execute("DELETE FROM PolicyVersions")));
    }

    // ---- Single-row financial identities ----

    [Fact]
    public void Order_total_must_equal_net_plus_tax_and_be_positive()
    {
        var request = Seed.Request(_sale.UserId);
        Save(request);

        var order = Seed.Order(_sale.UserId, request.Id, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 2);
        order.TotalM = order.NetM + 1;
        Assert.Contains("CK_Orders_TotalIdentity", Refused(() => Save(order)));

        var zero = Seed.Order(_sale.UserId, request.Id, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 2);
        zero.NetM = zero.TotalM = zero.PriceBasisSubtotalM = 0;
        Assert.Contains("CK_Orders_Amounts", Refused(() => Save(zero)));
    }

    [Fact]
    public void Transaction_numbers_series_slots_and_requests_are_unique()
    {
        var request = Seed.Request(_sale.UserId);
        Save(request);

        var sameNumber = Seed.Order(_sale.UserId, request.Id, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 1);
        Assert.Contains("UNIQUE constraint failed: Orders.CheckoutRequestId", Refused(() => Save(
            Seed.Order(_sale.UserId, _sale.RequestId, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 2))));
        Assert.Contains("UNIQUE constraint failed", Refused(() => Save(sameNumber)));
    }

    [Fact]
    public void Order_item_quantity_must_align_to_its_step_and_satisfy_identities()
    {
        var misaligned = Seed.Item(_sale.OrderId, _sale.ProductId, lineNo: 2);
        misaligned.QuantityQ = 1500;
        Assert.Contains("CK_OrderItems_Quantity", Refused(() => Save(misaligned)));

        var badNet = Seed.Item(_sale.OrderId, _sale.ProductId, lineNo: 2);
        badNet.NetBeforeDiscountM += 1;
        Assert.Contains("CK_OrderItems_NetIdentity", Refused(() => Save(badNet)));

        var duplicateLine = Seed.Item(_sale.OrderId, _sale.ProductId, lineNo: 1);
        Assert.Contains("UNIQUE constraint failed: OrderItems.OrderId, OrderItems.LineNo", Refused(() => Save(duplicateLine)));
    }

    [Fact]
    public void Cash_and_gcash_payment_rules_are_enforced()
    {
        var request = Seed.Request(_sale.UserId);
        var order = Seed.Order(_sale.UserId, request.Id, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 2);
        Save(request, order);

        var wrongChange = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.Cash, AppliedM = 200_00, TenderedM = 300_00, ChangeM = 50_00,
            VerificationState = PaymentVerificationState.CashReceived, RecordedByUserId = _sale.UserId, PaidUtc = Seed.Now,
        };
        Assert.Contains("CK_Payments_Method", Refused(() => Save(wrongChange)));

        var unverifiedGcash = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.GCash, AppliedM = 200_00,
            VerificationState = PaymentVerificationState.Verified, RecordedByUserId = _sale.UserId, PaidUtc = Seed.Now,
        };
        Assert.Contains("CK_Payments_Method", Refused(() => Save(unverifiedGcash)));

        var verifiedGcash = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.GCash, AppliedM = 200_00, Reference = "TEST-REF",
            VerificationState = PaymentVerificationState.Verified, VerifiedByUserId = _sale.UserId, VerifiedUtc = Seed.Now,
            RecordedByUserId = _sale.UserId, PaidUtc = Seed.Now,
        };
        Save(verifiedGcash);

        var second = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.Cash, AppliedM = 200_00, TenderedM = 200_00, ChangeM = 0,
            VerificationState = PaymentVerificationState.CashReceived, RecordedByUserId = _sale.UserId, PaidUtc = Seed.Now,
        };
        Assert.Contains("UNIQUE constraint failed: Payments.OrderId", Refused(() => Save(second)));
    }

    [Fact]
    public void Checkout_request_state_must_match_its_order_link()
    {
        var completedWithoutOrder = Seed.Request(_sale.UserId);
        completedWithoutOrder.State = RequestState.Completed;
        completedWithoutOrder.ResolvedUtc = Seed.Now;
        Assert.Contains("CK_CheckoutRequests_Completion", Refused(() => Save(completedWithoutOrder)));

        var prepared = Seed.Request(_sale.UserId);
        Save(prepared);
        Assert.Contains("identity columns are immutable", Refused(() => _db.Execute($"UPDATE CheckoutRequests SET QuoteJson = '{{\"changed\":1}}' WHERE Id = '{prepared.Id}'")));
        Assert.Equal(1, _db.Execute($"UPDATE CheckoutRequests SET State = 'Abandoned', ResolvedUtc = {Seed.Now} WHERE Id = '{prepared.Id}'"));
    }

    // ---- Stock, shifts and referential integrity ----

    [Fact]
    public void Inventory_movements_require_a_matching_source_and_nonzero_delta()
    {
        var saleWithoutLine = new InventoryMovement
        {
            Id = Guid.NewGuid(), ProductId = _sale.ProductId, DeltaQ = -1000, Type = InventoryMovementType.Sale, ActorUserId = _sale.UserId,
            RequestId = Guid.NewGuid(), MovementKey = "k1", CreatedUtc = Seed.Now, BalanceAfterQ = 7000,
        };
        Assert.Contains("CK_InventoryMovements_Source", Refused(() => Save(saleWithoutLine)));

        var adjustmentWithoutReason = new InventoryMovement
        {
            Id = Guid.NewGuid(), ProductId = _sale.ProductId, DeltaQ = 1000, Type = InventoryMovementType.Adjustment, ActorUserId = _sale.UserId,
            RequestId = Guid.NewGuid(), MovementKey = "k2", CreatedUtc = Seed.Now, BalanceAfterQ = 9000,
        };
        Assert.Contains("CK_InventoryMovements_Source", Refused(() => Save(adjustmentWithoutReason)));

        var zero = new InventoryMovement
        {
            Id = Guid.NewGuid(), ProductId = _sale.ProductId, DeltaQ = 0, Type = InventoryMovementType.Adjustment, Reason = "count", ActorUserId = _sale.UserId,
            RequestId = Guid.NewGuid(), MovementKey = "k3", CreatedUtc = Seed.Now, BalanceAfterQ = 8000,
        };
        Assert.Contains("CK_InventoryMovements_Delta", Refused(() => Save(zero)));
    }

    [Fact]
    public void One_open_shift_per_cashier_and_close_fields_all_or_nothing()
    {
        CashierShift Open() => new() { Id = Guid.NewGuid(), CashierUserId = _sale.UserId, OpenedUtc = Seed.Now, OpeningCashM = 100_00 };

        var first = Open();
        Save(first);
        Assert.Contains("UNIQUE constraint failed: CashierShifts.CashierUserId", Refused(() => Save(Open())));

        Assert.Contains("CK_CashierShifts_Close", Refused(() => _db.Execute($"UPDATE CashierShifts SET ClosedUtc = {Seed.Now + 1}")));

        // F-10 close snapshot: expected 320.00, counted 315.00, variance -5.00.
        Assert.Equal(1, _db.Execute(
            $"UPDATE CashierShifts SET ClosedUtc = {Seed.Now + 1}, CountedCashM = 31500, ExpectedCashM = 32000, VarianceM = -500, ClosedByUserId = '{_sale.UserId}'"));
        Assert.Contains("a closed shift is immutable", Refused(() => _db.Execute("UPDATE CashierShifts SET CloseReason = 'edit'")));

        Save(Open()); // a new shift may open once the previous one is closed
    }

    [Fact]
    public void Referenced_master_data_cannot_be_deleted_and_orphans_are_rejected()
    {
        Assert.Contains("FOREIGN KEY constraint failed", Refused(() => _db.Execute("DELETE FROM Products")));
        Assert.Contains("FOREIGN KEY constraint failed", Refused(() => _db.Execute("DELETE FROM Users")));

        var orphan = Seed.Item(Guid.NewGuid(), _sale.ProductId, lineNo: 3);
        Assert.Contains("FOREIGN KEY constraint failed", Refused(() => Save(orphan)));
    }

    // ---- Printing and artifacts ----

    [Fact]
    public void Print_jobs_have_exactly_one_valid_source()
    {
        PrintJob Job(PrintPurpose purpose, Guid? orderId, bool test = false, string? reason = null) => new()
        {
            Id = Guid.NewGuid(), OrderId = orderId, RequestId = Guid.NewGuid(), Purpose = purpose, IsTestModel = test, TemplateVersion = 1,
            ModelJson = "{}", ModelSha256 = Seed.Sha("model"), State = PrintJobState.Pending, CreatedByUserId = _sale.UserId, CreatedUtc = Seed.Now, Reason = reason,
        };

        Save(Job(PrintPurpose.Original, _sale.OrderId));
        // Partial unique index UX_PrintJobs_OriginalPerOrder: one Original receipt per sale.
        Assert.Contains("UNIQUE constraint failed: PrintJobs.OrderId", Refused(() => Save(Job(PrintPurpose.Original, _sale.OrderId))));
        Assert.Contains("CK_PrintJobs_ReprintReason", Refused(() => Save(Job(PrintPurpose.Reprint, _sale.OrderId))));
        Save(Job(PrintPurpose.Reprint, _sale.OrderId, reason: "Customer copy"));

        Assert.Contains("CK_PrintJobs_Source", Refused(() => Save(Job(PrintPurpose.Original, null))));
        Assert.Contains("CK_PrintJobs_Source", Refused(() => Save(Job(PrintPurpose.Test, _sale.OrderId, test: true))));
        Save(Job(PrintPurpose.Test, null, test: true));
    }

    [Fact]
    public void Artifact_source_must_match_its_type_and_ready_artifacts_are_frozen()
    {
        ArtifactRecord Artifact(ArtifactType type, Guid? orderId) => new()
        {
            Id = Guid.NewGuid(), LogicalKey = "sale/" + Guid.NewGuid(), Type = type, SourceId = orderId ?? Guid.NewGuid(), OrderId = orderId,
            PayloadSchemaVersion = 1, State = ArtifactState.Intent, CreatedUtc = Seed.Now,
        };

        Assert.Contains("CK_ArtifactRecords_Source", Refused(() => Save(Artifact(ArtifactType.ReportCsv, _sale.OrderId))));

        var intent = Artifact(ArtifactType.SaleJson, _sale.OrderId);
        Save(intent);
        Assert.Contains("CK_ArtifactRecords_Materialized", Refused(() => _db.Execute($"UPDATE ArtifactRecords SET State = 'Ready' WHERE Id = '{intent.Id}'")));

        var hash = Seed.Sha("bytes");
        Assert.Equal(1, _db.Execute(
            $"UPDATE ArtifactRecords SET State = 'Ready', RelativePath = 'sales/x.json', Sha256 = '{hash}', SizeBytes = 10, MaterializedUtc = {Seed.Now} WHERE Id = '{intent.Id}'"));
        Assert.Contains("Ready artifacts are immutable", Refused(() => _db.Execute($"UPDATE ArtifactRecords SET SizeBytes = 11 WHERE Id = '{intent.Id}'")));
        Assert.Contains("Ready artifacts cannot be deleted", Refused(() => _db.Execute($"DELETE FROM ArtifactRecords WHERE Id = '{intent.Id}'")));
    }

    // ---- Atomicity primitive (T-07) ----

    [Fact]
    public void A_failed_write_inside_the_checkout_transaction_leaves_no_partial_sale()
    {
        var request = Seed.Request(_sale.UserId);
        Save(request);
        var order = Seed.Order(_sale.UserId, request.Id, _sale.PolicyId, _sale.BusinessProfileId, _sale.SeriesKey, 2);
        var item = Seed.Item(order.Id, _sale.ProductId);
        var badPayment = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.Cash, AppliedM = 200_00, TenderedM = 100_00, ChangeM = 0,
            VerificationState = PaymentVerificationState.CashReceived, RecordedByUserId = _sale.UserId, PaidUtc = Seed.Now,
        };

        Refused(() =>
        {
            using var db = _db.NewContext();
            using var tx = db.Database.BeginTransaction();
            db.Orders.Add(order);
            db.OrderItems.Add(item);
            db.SaveChanges();
            db.Payments.Add(badPayment);
            db.SaveChanges(); // fails: tendered < applied
            tx.Commit();
        });

        Assert.Equal(1L, _db.Scalar<long>("SELECT count(*) FROM Orders"));
        Assert.Equal(1L, _db.Scalar<long>("SELECT count(*) FROM OrderItems"));
        Assert.Equal(1L, _db.Scalar<long>("SELECT count(*) FROM Payments"));
    }
}
