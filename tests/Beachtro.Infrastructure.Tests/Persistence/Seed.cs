using System.Security.Cryptography;
using System.Text;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;

namespace Beachtro.Infrastructure.Tests.Persistence;

public sealed record SeededSale(
    Guid UserId,
    Guid CategoryId,
    Guid ProductId,
    Guid PolicyId,
    Guid BusinessProfileId,
    string SeriesKey,
    Guid RequestId,
    Guid OrderId,
    Guid OrderItemId,
    Guid PaymentId);

/// <summary>
/// Obviously synthetic seed data (never real business identity, prices or tax): one completed
/// cash sale of 2 × 100.00, tendered 250.00, through the full request → order → stock path.
/// </summary>
public static class Seed
{
    public const long Now = 1_763_300_000_000;
    public const string BusinessDate = "2026-11-16";

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static SeededSale Sale(TestDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var user = User("cashier.test");
        var category = new Category { Id = Guid.NewGuid(), Name = "Test Drinks", NormalizedName = "TEST DRINKS", IsActive = true };
        var product = new Product
        {
            Id = Guid.NewGuid(), CategoryId = category.Id, Sku = "TEST-001", NormalizedSku = "TEST-001", Name = "Synthetic Iced Tea",
            UnitPriceM = 100_00, CurrencyCode = "PHP", CurrencyExponent = 2, UnitStepQ = 1000, TrackStock = true, IsActive = true, UpdatedUtc = Now,
        };
        var policy = new PolicyVersion
        {
            Id = Guid.NewGuid(), Kind = PolicyKind.Calculation, Revision = 1, EffectiveFromUtc = Now - 1000, ApprovalState = ProfileApprovalState.Test,
            PayloadSchemaVersion = 1, CanonicalPayloadJson = "{\"strategy\":\"generic-line-v1\"}", PayloadSha256 = Sha("policy"), CreatedUtc = Now,
        };
        var profile = new BusinessProfile
        {
            Id = Guid.NewGuid(), Revision = 1, RegisteredName = "TEST BUSINESS - NOT REAL", Address = "TEST ADDRESS", RegistrationJson = "{}",
            TemplateVersion = 1, ApprovalState = ProfileApprovalState.Test, CreatedUtc = Now,
        };
        var series = new NumberSeries { SeriesKey = "MB-20261116", BusinessDate = BusinessDate, Prefix = "MB", LastReservedSequence = 1, Status = NumberSeriesStatus.Ready };
        var request = new CheckoutRequest
        {
            Id = Guid.NewGuid(), ActorUserId = user.Id, RequestFingerprint = "fp-1", QuoteJson = "{}", QuoteSha256 = Sha("quote"),
            State = RequestState.Prepared, PreparedUtc = Now,
        };

        using (var db = database.NewContext())
        {
            db.AddRange(user, category, product, policy, profile, series, request);
            db.Inventory.Add(new InventoryBalance { ProductId = product.Id, QuantityQ = 10_000, UpdatedUtc = Now });
            db.SaveChanges();
        }

        var order = Order(user.Id, request.Id, policy.Id, profile.Id, series.SeriesKey, sequence: 1);
        var item = Item(order.Id, product.Id);
        var payment = new Payment
        {
            Id = Guid.NewGuid(), OrderId = order.Id, Method = PaymentMethod.Cash, AppliedM = 200_00, TenderedM = 250_00, ChangeM = 50_00,
            VerificationState = PaymentVerificationState.CashReceived, RecordedByUserId = user.Id, PaidUtc = Now,
        };

        // One atomic financial write, as the checkout command will do it.
        using (var db = database.NewContext())
        using (var tx = db.Database.BeginTransaction())
        {
            db.Orders.Add(order);
            db.OrderItems.Add(item);
            db.Payments.Add(payment);
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductId = product.Id, DeltaQ = -2000, Type = InventoryMovementType.Sale, ActorUserId = user.Id,
                OrderId = order.Id, OrderItemId = item.Id, RequestId = request.Id, MovementKey = $"{request.Id}:{product.Id}:Sale:1",
                CreatedUtc = Now, BalanceAfterQ = 8000,
            });
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(), OccurredUtc = Now, ActorUserId = user.Id, ActorNameSnapshot = user.DisplayName, Action = "SaleCompleted",
                EntityType = "Order", EntityId = order.Id.ToString(), SafeDetailsJson = "{}", CorrelationId = Guid.NewGuid(), RequestId = request.Id,
            });
            db.SaveChanges();

            var tracked = db.CheckoutRequests.Single(r => r.Id == request.Id);
            tracked.State = RequestState.Completed;
            tracked.OrderId = order.Id;
            tracked.ResolvedUtc = Now;
            tracked.Version++;
            db.SaveChanges();
            tx.Commit();
        }

        return new SeededSale(user.Id, category.Id, product.Id, policy.Id, profile.Id, series.SeriesKey, request.Id, order.Id, item.Id, payment.Id);
    }

    public static User User(string username) => new()
    {
        Id = Guid.NewGuid(), Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = "Test " + username,
        PasswordHash = "TEST-HASH-NOT-A-REAL-CREDENTIAL", IsActive = true, CreatedUtc = Now, UpdatedUtc = Now,
    };

    public static CheckoutRequest Request(Guid userId) => new()
    {
        Id = Guid.NewGuid(), ActorUserId = userId, RequestFingerprint = "fp-" + Guid.NewGuid(), QuoteJson = "{}", QuoteSha256 = Sha("q"),
        State = RequestState.Prepared, PreparedUtc = Now,
    };

    public static Order Order(Guid cashierId, Guid requestId, Guid policyId, Guid profileId, string seriesKey, int sequence) => new()
    {
        Id = Guid.NewGuid(), CheckoutRequestId = requestId, TransactionNumber = $"{seriesKey}-{sequence:D6}", NumberSeriesKey = seriesKey,
        Sequence = sequence, OrderType = OrderType.DineIn, State = OrderState.Completed, CashierUserId = cashierId, CreatedUtc = Now,
        CompletedUtc = Now, BusinessDate = BusinessDate, TimezoneId = "Asia/Manila", CutoffMinute = 0, CurrencyCode = "PHP", CurrencyExponent = 2,
        CalculationPolicyId = policyId, BusinessProfileId = profileId, NetM = 200_00, DiscountExTaxM = 0, TaxM = 0, TotalM = 200_00,
        PriceBasisSubtotalM = 200_00, DiscountBasisM = 0, SnapshotSchemaVersion = 1, SnapshotJson = "{}", SnapshotSha256 = Sha("snapshot" + sequence),
    };

    public static OrderItem Item(Guid orderId, Guid productId, int lineNo = 1) => new()
    {
        Id = Guid.NewGuid(), OrderId = orderId, LineNo = lineNo, ProductId = productId, SkuSnapshot = "TEST-001", NameSnapshot = "Synthetic Iced Tea",
        UnitPriceM = 100_00, QuantityQ = 2000, UnitStepQ = 1000, TrackStockSnapshot = true, PriceMode = PriceMode.Inclusive,
        TreatmentCode = TreatmentCode.TestNoTax, PriceBasisM = 200_00, DiscountBasisM = 0, NetBeforeDiscountM = 200_00, DiscountExTaxM = 0,
        NetM = 200_00, TaxM = 0, TotalM = 200_00, AppliedPolicyJson = "{}",
    };
}
