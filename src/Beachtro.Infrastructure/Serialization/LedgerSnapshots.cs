using System.Text.Json;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Infrastructure.Persistence.Entities;

namespace Beachtro.Infrastructure.Serialization;

/// <summary>Facts that identify how and where a sale was issued, frozen into its snapshot.</summary>
internal sealed record IssueContext(
    Guid InstallationId,
    string CashierName,
    BusinessProfile Business,
    Guid CalculationPolicyId,
    int CalculationRevision,
    ProfileApprovalState CalculationState,
    string CalculationSha256,
    string StrategyKey,
    Guid CalendarPolicyId,
    int CalendarRevision,
    OrderDiscount Discount);

/// <summary>
/// Canonical immutable snapshots (finance design §Immutable transaction snapshot). The relational
/// rows and this JSON are two representations of one write; receipts and exports read these facts,
/// never the current catalog or settings.
/// </summary>
internal static class LedgerSnapshots
{
    public const string SaleSchema = "sale.v1";
    public const string CorrectionSchema = "correction.v1";

    public static string Sale(Order order, IReadOnlyList<OrderItem> items, IReadOnlyList<ItemTaxComponent> taxes, Payment payment, IssueContext ctx) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", SaleSchema);
            w.WriteString("saleId", order.Id.ToString("D"));
            w.WriteString("requestId", order.CheckoutRequestId.ToString("D"));
            w.WriteString("transactionNumber", order.TransactionNumber);
            w.WriteString("installationId", ctx.InstallationId.ToString("D"));
            w.WriteNumber("completedUtc", order.CompletedUtc);
            w.WriteString("businessDate", order.BusinessDate);
            w.WriteString("timeZoneId", order.TimezoneId);
            w.WriteNumber("cutoffMinute", order.CutoffMinute);
            w.WriteStartObject("cashier");
            w.WriteString("userId", order.CashierUserId.ToString("D"));
            w.WriteString("name", ctx.CashierName);
            w.WriteEndObject();
            WriteOptionalGuid(w, "shiftId", order.ShiftId);
            w.WriteString("orderType", order.OrderType.ToString());
            WriteBusiness(w, ctx.Business);
            WritePolicy(w, ctx);
            w.WriteStartObject("currency");
            w.WriteString("code", order.CurrencyCode);
            w.WriteNumber("exponent", order.CurrencyExponent);
            w.WriteEndObject();
            w.WriteStartObject("discount");
            w.WriteString("kind", ctx.Discount.Kind.ToString());
            w.WriteNumber("value", ctx.Discount.Value);
            w.WriteEndObject();

            w.WriteStartArray("lines");
            foreach (var item in items.OrderBy(i => i.LineNo))
            {
                w.WriteStartObject();
                w.WriteNumber("lineNo", item.LineNo);
                w.WriteString("productId", item.ProductId.ToString("D"));
                w.WriteString("sku", item.SkuSnapshot);
                w.WriteString("name", item.NameSnapshot);
                w.WriteNumber("unitPriceM", item.UnitPriceM);
                w.WriteNumber("quantityQ", item.QuantityQ);
                w.WriteNumber("unitStepQ", item.UnitStepQ);
                w.WriteBoolean("trackStock", item.TrackStockSnapshot);
                w.WriteString("priceMode", item.PriceMode.ToString());
                w.WriteString("treatment", item.TreatmentCode.ToString());
                w.WriteNumber("priceBasisM", item.PriceBasisM);
                w.WriteNumber("discountBasisM", item.DiscountBasisM);
                w.WriteNumber("netBeforeDiscountM", item.NetBeforeDiscountM);
                w.WriteNumber("discountExTaxM", item.DiscountExTaxM);
                w.WriteNumber("netM", item.NetM);
                w.WriteNumber("taxM", item.TaxM);
                w.WriteNumber("totalM", item.TotalM);
                w.WriteStartArray("taxes");
                foreach (var t in taxes.Where(t => t.OrderItemId == item.Id).OrderBy(t => t.ComponentKey, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("key", t.ComponentKey);
                    w.WriteNumber("ratePpm", t.RatePpm);
                    w.WriteNumber("taxableBaseM", t.TaxableBaseM);
                    w.WriteNumber("taxM", t.TaxM);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartObject("payment");
            w.WriteString("method", payment.Method.ToString());
            w.WriteNumber("appliedM", payment.AppliedM);
            WriteOptionalLong(w, "tenderedM", payment.TenderedM);
            WriteOptionalLong(w, "changeM", payment.ChangeM);
            WriteOptionalString(w, "reference", payment.Reference);
            w.WriteString("verification", payment.VerificationState.ToString());
            WriteOptionalGuid(w, "verifiedBy", payment.VerifiedByUserId);
            WriteOptionalLong(w, "verifiedUtc", payment.VerifiedUtc);
            w.WriteString("recordedBy", payment.RecordedByUserId.ToString("D"));
            w.WriteEndObject();

            w.WriteStartObject("totals");
            w.WriteNumber("priceBasisSubtotalM", order.PriceBasisSubtotalM);
            w.WriteNumber("discountBasisM", order.DiscountBasisM);
            w.WriteNumber("discountExTaxM", order.DiscountExTaxM);
            w.WriteNumber("netM", order.NetM);
            w.WriteNumber("taxM", order.TaxM);
            w.WriteNumber("totalM", order.TotalM);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    public static string Correction(Correction correction, Order original, IReadOnlyList<CorrectionItem> items, IReadOnlyList<CorrectionTaxComponent> taxes,
        IReadOnlyDictionary<Guid, OrderItem> originalItems, IReadOnlyDictionary<Guid, ItemTaxComponent> originalTaxes, RefundPayment refund, string actorName, string approverName) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", CorrectionSchema);
            w.WriteString("correctionId", correction.Id.ToString("D"));
            w.WriteString("requestId", correction.CorrectionRequestId.ToString("D"));
            w.WriteString("correctionNumber", correction.CorrectionNumber);
            w.WriteString("kind", correction.Kind.ToString());
            w.WriteString("originalSaleId", original.Id.ToString("D"));
            w.WriteString("originalTransactionNumber", original.TransactionNumber);
            w.WriteString("reason", correction.Reason);
            w.WriteStartObject("actor");
            w.WriteString("userId", correction.ActorUserId.ToString("D"));
            w.WriteString("name", actorName);
            w.WriteEndObject();
            w.WriteStartObject("approver");
            w.WriteString("userId", correction.ApprovedByUserId.ToString("D"));
            w.WriteString("name", approverName);
            w.WriteNumber("approvalUtc", correction.ApprovalUtc);
            w.WriteEndObject();
            w.WriteNumber("createdUtc", correction.CreatedUtc);
            w.WriteString("businessDate", correction.BusinessDate);
            w.WriteString("timeZoneId", correction.TimezoneId);
            WriteOptionalGuid(w, "shiftId", correction.ShiftId);
            w.WriteStartArray("lines");
            foreach (var item in items.OrderBy(i => originalItems[i.OriginalOrderItemId].LineNo))
            {
                var source = originalItems[item.OriginalOrderItemId];
                w.WriteStartObject();
                w.WriteNumber("originalLineNo", source.LineNo);
                w.WriteString("originalItemId", source.Id.ToString("D"));
                w.WriteString("sku", source.SkuSnapshot);
                w.WriteString("name", source.NameSnapshot);
                w.WriteNumber("quantityQ", item.QuantityQ);
                w.WriteNumber("cumulativeReturnedQ", item.CumulativeReturnedQ);
                w.WriteNumber("netM", item.NetM);
                w.WriteNumber("discountExTaxM", item.DiscountExTaxM);
                w.WriteNumber("discountBasisM", item.DiscountBasisM);
                w.WriteNumber("taxM", item.TaxM);
                w.WriteNumber("totalM", item.TotalM);
                w.WriteString("stockDisposition", item.StockDisposition.ToString());
                w.WriteNumber("stockReturnedQ", item.StockReturnedQ);
                w.WriteStartArray("taxes");
                foreach (var t in taxes.Where(t => t.CorrectionItemId == item.Id).OrderBy(t => originalTaxes[t.OriginalTaxComponentId].ComponentKey, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("key", originalTaxes[t.OriginalTaxComponentId].ComponentKey);
                    w.WriteNumber("taxM", t.TaxM);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("refund");
            w.WriteString("method", refund.Method.ToString());
            w.WriteNumber("amountM", refund.AmountM);
            w.WriteString("verification", refund.VerificationState.ToString());
            WriteOptionalString(w, "reference", refund.Reference);
            w.WriteString("verifiedBy", refund.VerifiedByUserId.ToString("D"));
            w.WriteNumber("verifiedUtc", refund.VerifiedUtc);
            w.WriteEndObject();
            w.WriteStartObject("totals");
            w.WriteNumber("netM", correction.NetM);
            w.WriteNumber("discountExTaxM", correction.DiscountExTaxM);
            w.WriteNumber("taxM", correction.TaxM);
            w.WriteNumber("totalM", correction.TotalM);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    /// <summary>Print intent until the receipt model exists (IB-15): identifies the immutable source only.</summary>
    public static string ReceiptIntent(string sourceType, Guid sourceId, string number) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", "receipt-intent.v1");
            w.WriteString("sourceType", sourceType);
            w.WriteString("sourceId", sourceId.ToString("D"));
            w.WriteString("number", number);
            w.WriteEndObject();
        });

    private static void WriteBusiness(Utf8JsonWriter w, BusinessProfile b)
    {
        w.WriteStartObject("business");
        w.WriteString("profileId", b.Id.ToString("D"));
        w.WriteNumber("revision", b.Revision);
        w.WriteString("approvalState", b.ApprovalState.ToString());
        w.WriteString("registeredName", b.RegisteredName);
        w.WriteString("address", b.Address);
        WriteOptionalString(w, "contact", b.Contact);
        WriteOptionalString(w, "taxIdentifier", b.TaxIdentifier);
        w.WriteNumber("templateVersion", b.TemplateVersion);
        w.WriteEndObject();
    }

    private static void WritePolicy(Utf8JsonWriter w, IssueContext ctx)
    {
        w.WriteStartObject("policy");
        w.WriteString("calculationPolicyId", ctx.CalculationPolicyId.ToString("D"));
        w.WriteNumber("calculationRevision", ctx.CalculationRevision);
        w.WriteString("calculationApprovalState", ctx.CalculationState.ToString());
        w.WriteString("calculationPayloadSha256", ctx.CalculationSha256);
        w.WriteString("strategy", ctx.StrategyKey);
        w.WriteString("calendarPolicyId", ctx.CalendarPolicyId.ToString("D"));
        w.WriteNumber("calendarRevision", ctx.CalendarRevision);
        w.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteOptionalLong(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteOptionalGuid(Utf8JsonWriter w, string name, Guid? value) =>
        WriteOptionalString(w, name, value?.ToString("D"));
}
