using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using static Beachtro.Infrastructure.Persistence.ModelBuilderHelpers;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>
/// Sales, snapshots and corrections (database design §Sales, immutable snapshots and corrections).
/// Single-row identities are CHECKs; cross-row aggregates (order = Σ lines, payment = total) are
/// validated by the command transaction, as SQLite CHECK cannot express them.
/// </summary>
internal static class SalesModel
{
    private const string MaxMoney = "100000000000";

    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<Draft>(b =>
        {
            b.ToTable("Drafts", t => t.Check("Version", """ "Version" >= 1 """));
            b.Fk<Draft, User>(x => x.OwnerUserId);
        });

        mb.Entity<CheckoutRequest>(b =>
        {
            b.ToTable("CheckoutRequests", t => t
                .Check("Completion", """
                    ("State" = 'Completed' AND "OrderId" IS NOT NULL AND "ResolvedUtc" IS NOT NULL)
                    OR ("State" = 'Abandoned' AND "OrderId" IS NULL AND "ResolvedUtc" IS NOT NULL)
                    OR ("State" = 'Prepared' AND "OrderId" IS NULL AND "ResolvedUtc" IS NULL)
                    """)
                .Check("QuoteSha256", Sha256("QuoteSha256"))
                .Check("Fingerprint", NotBlank("RequestFingerprint"))
                .Check("Version", """ "Version" >= 1 """));
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => new { x.State, x.PreparedUtc });
            b.Fk<CheckoutRequest, User>(x => x.ActorUserId);
            b.Fk<CheckoutRequest, Order>(x => x.OrderId);
        });

        mb.Entity<NumberSeries>(b =>
        {
            b.ToTable("NumberSeries", t => t
                .Check("Sequence", """ "LastReservedSequence" BETWEEN 0 AND 999999 """)
                .Check("BusinessDate", IsoDate("BusinessDate"))
                .Check("Prefix", NotBlank("Prefix"))
                .Check("Version", """ "Version" >= 1 """));
            b.HasKey(x => x.SeriesKey);
            b.HasIndex(x => new { x.Prefix, x.BusinessDate }).IsUnique();
        });

        mb.Entity<Order>(b =>
        {
            b.ToTable("Orders", t => t
                .Check("TotalIdentity", """ "TotalM" = "NetM" + "TaxM" """)
                .Check("Amounts", NonNegative("NetM", "DiscountExTaxM", "TaxM", "PriceBasisSubtotalM", "DiscountBasisM")
                    + $""" AND "TotalM" > 0 AND "TotalM" <= {MaxMoney} AND "DiscountBasisM" <= "PriceBasisSubtotalM" """)
                .Check("Sequence", """ "Sequence" BETWEEN 1 AND 999999 """)
                .Check("BusinessDate", IsoDate("BusinessDate"))
                .Check("Cutoff", """ "CutoffMinute" BETWEEN 0 AND 1439 """)
                .Check("Currency", """ length("CurrencyCode") = 3 AND "CurrencyExponent" BETWEEN 0 AND 4 """)
                .Check("Times", """ "CompletedUtc" >= "CreatedUtc" """)
                .Check("Snapshot", Sha256("SnapshotSha256") + """ AND "SnapshotSchemaVersion" >= 1 """)
                .Check("TransactionNumber", NotBlank("TransactionNumber")));
            b.HasIndex(x => x.CheckoutRequestId).IsUnique();
            b.HasIndex(x => x.TransactionNumber).IsUnique();
            b.HasIndex(x => new { x.NumberSeriesKey, x.Sequence }).IsUnique();
            b.HasIndex(x => new { x.BusinessDate, x.CompletedUtc, x.Id });
            b.HasIndex(x => new { x.CashierUserId, x.CompletedUtc });
            b.HasIndex(x => x.ShiftId);
            b.Fk<Order, CheckoutRequest>(x => x.CheckoutRequestId);
            b.Fk<Order, NumberSeries>(x => x.NumberSeriesKey);
            b.Fk<Order, User>(x => x.CashierUserId);
            b.Fk<Order, CashierShift>(x => x.ShiftId);
            b.Fk<Order, PolicyVersion>(x => x.CalculationPolicyId);
            b.Fk<Order, BusinessProfile>(x => x.BusinessProfileId);
        });

        mb.Entity<OrderItem>(b =>
        {
            b.ToTable("OrderItems", t => t
                .Check("LineNo", """ "LineNo" BETWEEN 1 AND 100 """)
                .Check("Quantity", """ "QuantityQ" > 0 AND "UnitStepQ" IN (1, 1000) AND "QuantityQ" % "UnitStepQ" = 0 """)
                .Check("Price", $""" "UnitPriceM" > 0 AND "UnitPriceM" <= {MaxMoney} """)
                .Check("NetIdentity", """ "NetBeforeDiscountM" = "NetM" + "DiscountExTaxM" """)
                .Check("TotalIdentity", """ "TotalM" = "NetM" + "TaxM" """)
                .Check("Discount", """ "DiscountBasisM" <= "PriceBasisM" """)
                .Check("Amounts", NonNegative("PriceBasisM", "DiscountBasisM", "NetBeforeDiscountM", "DiscountExTaxM", "NetM", "TaxM", "TotalM")));
            b.HasIndex(x => new { x.OrderId, x.LineNo }).IsUnique();
            b.HasIndex(x => new { x.ProductId, x.OrderId });
            b.Fk<OrderItem, Order>(x => x.OrderId);
            b.Fk<OrderItem, Product>(x => x.ProductId);
        });

        mb.Entity<ItemTaxComponent>(b =>
        {
            b.ToTable("ItemTaxComponents", t => t
                .Check("Rate", """ "RatePpm" BETWEEN 0 AND 1000000 """)
                .Check("Amounts", NonNegative("TaxableBaseM", "TaxM"))
                .Check("Key", NotBlank("ComponentKey")));
            b.HasIndex(x => new { x.OrderItemId, x.ComponentKey }).IsUnique();
            b.Fk<ItemTaxComponent, OrderItem>(x => x.OrderItemId);
        });

        mb.Entity<Payment>(b =>
        {
            b.ToTable("Payments", t => t
                .Check("Applied", $""" "AppliedM" > 0 AND "AppliedM" <= {MaxMoney} """)
                .Check("Method", """
                    ("Method" = 'Cash' AND "VerificationState" = 'CashReceived'
                        AND "TenderedM" IS NOT NULL AND "ChangeM" IS NOT NULL
                        AND "TenderedM" >= "AppliedM" AND "ChangeM" = "TenderedM" - "AppliedM")
                    OR ("Method" = 'GCash' AND "VerificationState" = 'Verified'
                        AND "TenderedM" IS NULL AND "ChangeM" IS NULL
                        AND "VerifiedByUserId" IS NOT NULL AND "VerifiedUtc" IS NOT NULL)
                    """));
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => x.Reference);
            b.Fk<Payment, Order>(x => x.OrderId);
            b.Fk<Payment, User>(x => x.VerifiedByUserId);
            b.Fk<Payment, User>(x => x.RecordedByUserId);
        });

        mb.Entity<CorrectionRequest>(b =>
        {
            b.ToTable("CorrectionRequests", t => t
                .Check("Completion", """
                    ("State" = 'Completed' AND "CorrectionId" IS NOT NULL AND "ResolvedUtc" IS NOT NULL)
                    OR ("State" = 'Abandoned' AND "CorrectionId" IS NULL AND "ResolvedUtc" IS NOT NULL)
                    OR ("State" = 'Prepared' AND "CorrectionId" IS NULL AND "ResolvedUtc" IS NULL)
                    """)
                .Check("Fingerprint", NotBlank("Fingerprint"))
                .Check("Version", """ "Version" >= 1 """));
            b.HasIndex(x => x.CorrectionId).IsUnique();
            b.HasIndex(x => x.OriginalOrderId);
            b.Fk<CorrectionRequest, User>(x => x.ActorUserId);
            b.Fk<CorrectionRequest, Order>(x => x.OriginalOrderId);
            b.Fk<CorrectionRequest, Correction>(x => x.CorrectionId);
        });

        mb.Entity<Correction>(b =>
        {
            b.ToTable("Corrections", t => t
                .Check("TotalIdentity", """ "TotalM" = "NetM" + "TaxM" """)
                .Check("Amounts", NonNegative("NetM", "DiscountExTaxM", "TaxM", "TotalM") + $""" AND "TotalM" <= {MaxMoney} """)
                .Check("BusinessDate", IsoDate("BusinessDate"))
                .Check("Reason", NotBlank("Reason"))
                .Check("Snapshot", Sha256("SnapshotSha256")));
            b.HasIndex(x => x.CorrectionRequestId).IsUnique();
            b.HasIndex(x => x.CorrectionNumber).IsUnique();
            b.HasIndex(x => new { x.BusinessDate, x.CreatedUtc });
            b.HasIndex(x => x.OriginalOrderId);
            b.Fk<Correction, Order>(x => x.OriginalOrderId);
            b.Fk<Correction, CorrectionRequest>(x => x.CorrectionRequestId);
            b.Fk<Correction, User>(x => x.ActorUserId);
            b.Fk<Correction, User>(x => x.ApprovedByUserId);
            b.Fk<Correction, CashierShift>(x => x.ShiftId);
        });

        mb.Entity<CorrectionItem>(b =>
        {
            b.ToTable("CorrectionItems", t => t
                .Check("Quantity", """ "QuantityQ" > 0 AND "CumulativeReturnedQ" >= "QuantityQ" """)
                .Check("TotalIdentity", """ "TotalM" = "NetM" + "TaxM" """)
                .Check("Amounts", NonNegative("NetM", "DiscountExTaxM", "TaxM", "TotalM", "DiscountBasisM")))
                .ToTable("CorrectionItems", t => t.Check("Stock", """
                    ("StockDisposition" = 'Return' AND "StockReturnedQ" > 0 AND "StockReturnedQ" <= "QuantityQ")
                    OR ("StockDisposition" IN ('Discard', 'NotTracked') AND "StockReturnedQ" = 0)
                    """));
            b.HasIndex(x => x.OriginalOrderItemId);
            b.HasIndex(x => x.CorrectionId);
            b.Fk<CorrectionItem, Correction>(x => x.CorrectionId);
            b.Fk<CorrectionItem, OrderItem>(x => x.OriginalOrderItemId);
        });

        mb.Entity<CorrectionTaxComponent>(b =>
        {
            b.ToTable("CorrectionTaxComponents", t => t.Check("Amount", """ "TaxM" >= 0 """));
            b.HasIndex(x => new { x.CorrectionItemId, x.OriginalTaxComponentId }).IsUnique();
            b.HasIndex(x => x.OriginalTaxComponentId);
            b.Fk<CorrectionTaxComponent, CorrectionItem>(x => x.CorrectionItemId);
            b.Fk<CorrectionTaxComponent, ItemTaxComponent>(x => x.OriginalTaxComponentId);
        });

        mb.Entity<RefundPayment>(b =>
        {
            b.ToTable("RefundPayments", t => t
                .Check("Amount", $""" "AmountM" >= 0 AND "AmountM" <= {MaxMoney} """)
                .Check("Evidence", """
                    ("AmountM" = 0 AND "VerificationState" = 'NoTransfer')
                    OR ("AmountM" > 0 AND "Method" = 'Cash' AND "VerificationState" = 'CashPaidOut')
                    OR ("AmountM" > 0 AND "Method" = 'GCash' AND "VerificationState" = 'ManuallyRefunded')
                    """));
            b.HasIndex(x => x.CorrectionId).IsUnique();
            b.Fk<RefundPayment, Correction>(x => x.CorrectionId);
            b.Fk<RefundPayment, User>(x => x.VerifiedByUserId);
            b.Fk<RefundPayment, User>(x => x.RecordedByUserId);
        });
    }
}
