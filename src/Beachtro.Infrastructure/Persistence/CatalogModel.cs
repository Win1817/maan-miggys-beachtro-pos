using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using static Beachtro.Infrastructure.Persistence.ModelBuilderHelpers;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>Catalog, stock and shift tables (database design §Catalog, stock and shifts).</summary>
internal static class CatalogModel
{
    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<Category>(b =>
        {
            b.ToTable("Categories", t => t.Check("Name", NotBlank("Name")).Check("Version", """ "Version" >= 1 """));
            b.HasIndex(x => x.NormalizedName).IsUnique();
        });

        mb.Entity<Product>(b =>
        {
            b.ToTable("Products", t => t
                .Check("Price", """ "UnitPriceM" > 0 AND "UnitPriceM" <= 100000000000 """)
                .Check("Currency", """ length("CurrencyCode") = 3 AND "CurrencyCode" = upper("CurrencyCode") AND "CurrencyExponent" BETWEEN 0 AND 4 """)
                .Check("UnitStep", """ "UnitStepQ" IN (1, 1000) """)
                .Check("Sku", NotBlank("Sku") + " AND " + NotBlank("NormalizedSku"))
                .Check("Name", NotBlank("Name"))
                .Check("TaxKey", NotBlank("TaxKey"))
                .Check("Version", """ "Version" >= 1 """));
            b.HasIndex(x => x.NormalizedSku).IsUnique();
            b.HasIndex(x => x.CategoryId);
            b.Fk<Product, Category>(x => x.CategoryId);
        });

        mb.Entity<InventoryBalance>(b =>
        {
            // Quantity may be signed only for an explicit, permissioned negative-stock override.
            b.ToTable("Inventory", t => t.Check("Reorder", """ "ReorderQ" >= 0 """).Check("Version", """ "Version" >= 1 """));
            b.HasKey(x => x.ProductId);
            b.Fk<InventoryBalance, Product>(x => x.ProductId);
        });

        mb.Entity<InventoryMovement>(b =>
        {
            b.ToTable("InventoryMovements", t => t
                .Check("Delta", """ "DeltaQ" <> 0 """)
                .Check("Source", """
                    ("Type" = 'Sale' AND "OrderId" IS NOT NULL AND "OrderItemId" IS NOT NULL AND "CorrectionItemId" IS NULL AND "DeltaQ" < 0)
                    OR ("Type" = 'Return' AND "CorrectionItemId" IS NOT NULL AND "OrderItemId" IS NULL AND "DeltaQ" > 0)
                    OR ("Type" IN ('Opening', 'Adjustment') AND "OrderId" IS NULL AND "OrderItemId" IS NULL AND "CorrectionItemId" IS NULL
                        AND "Reason" IS NOT NULL AND length(trim("Reason")) > 0)
                    """)
                .Check("MovementKey", NotBlank("MovementKey")));
            b.HasIndex(x => x.MovementKey).IsUnique();
            b.HasIndex(x => new { x.ProductId, x.CreatedUtc, x.Id });
            b.HasIndex(x => x.OrderItemId);
            b.HasIndex(x => x.CorrectionItemId);
            b.Fk<InventoryMovement, Product>(x => x.ProductId);
            b.Fk<InventoryMovement, User>(x => x.ActorUserId);
            b.Fk<InventoryMovement, User>(x => x.ApprovedByUserId);
            b.Fk<InventoryMovement, Order>(x => x.OrderId);
            b.Fk<InventoryMovement, OrderItem>(x => x.OrderItemId);
            b.Fk<InventoryMovement, CorrectionItem>(x => x.CorrectionItemId);
        });

        mb.Entity<CashierShift>(b =>
        {
            b.ToTable("CashierShifts", t => t
                .Check("Opening", """ "OpeningCashM" >= 0 """)
                .Check("Close", """
                    ("ClosedUtc" IS NULL AND "CountedCashM" IS NULL AND "ExpectedCashM" IS NULL AND "VarianceM" IS NULL AND "ClosedByUserId" IS NULL)
                    OR ("ClosedUtc" IS NOT NULL AND "CountedCashM" IS NOT NULL AND "ExpectedCashM" IS NOT NULL AND "VarianceM" IS NOT NULL
                        AND "ClosedByUserId" IS NOT NULL AND "CountedCashM" >= 0 AND "ClosedUtc" >= "OpenedUtc"
                        AND "VarianceM" = "CountedCashM" - "ExpectedCashM")
                    """)
                .Check("Version", """ "Version" >= 1 """));
            b.HasIndex(x => x.CashierUserId).IsUnique().HasFilter(""" "ClosedUtc" IS NULL """).HasDatabaseName("UX_CashierShifts_OneOpenPerCashier");
            b.HasIndex(x => new { x.CashierUserId, x.OpenedUtc });
            b.Fk<CashierShift, User>(x => x.CashierUserId);
            b.Fk<CashierShift, User>(x => x.ClosedByUserId);
        });

        mb.Entity<CashMovement>(b =>
        {
            b.ToTable("CashMovements", t => t.Check("Amount", """ "AmountM" > 0 AND "AmountM" <= 100000000000 """).Check("Reason", NotBlank("Reason")));
            b.HasIndex(x => x.RequestId).IsUnique();
            b.HasIndex(x => x.ShiftId);
            b.Fk<CashMovement, CashierShift>(x => x.ShiftId);
            b.Fk<CashMovement, User>(x => x.ActorUserId);
            b.Fk<CashMovement, User>(x => x.ApprovedByUserId);
        });

        mb.Entity<Discount>(b =>
        {
            b.ToTable("Discounts", t => t
                .Check("Value", """
                    ("Type" = 'FixedM' AND "ValueScaled" > 0 AND "ValueScaled" <= 100000000000)
                    OR ("Type" = 'PercentPpm' AND "ValueScaled" > 0 AND "ValueScaled" <= 1000000)
                    """)
                .Check("Name", NotBlank("Name")));
        });
    }
}
