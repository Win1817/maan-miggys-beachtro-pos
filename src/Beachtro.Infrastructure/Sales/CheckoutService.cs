using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Financial.Payments;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Numbering;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sales;

/// <summary>
/// Checkout (finance design §Checkout protocol and crash recovery; IB-09, IB-10). Completion is one
/// local transaction: order, lines, tax components, payment, stock, audit, snapshot, artifact and
/// print intents, and the request's Completed state all commit together or not at all. No network
/// or printer work happens inside it.
/// </summary>
public sealed class CheckoutService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IProfileService profiles,
    NumberSeriesService numbering,
    IClock clock,
    WriteGate writeGate) : ICheckoutService
{
    public const int ReferenceMaximum = 128;

    public async Task<Result<CheckoutQuote>> QuoteAsync(UserSession session, CartInput cart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        var auth = await AuthorizeCartAsync(session, cart, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CheckoutQuote>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var priced = await SalePricing.PriceAsync(db, profiles, cart, clock.UtcNowMilliseconds, cancellationToken).ConfigureAwait(false);
        return priced.IsSuccess ? Result<CheckoutQuote>.Ok(priced.Value!.Quote) : priced.As<CheckoutQuote>();
    }

    public async Task<Result<CheckoutQuote>> PrepareAsync(UserSession session, Guid requestId, CartInput cart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        if (requestId == Guid.Empty)
        {
            return Result<CheckoutQuote>.Fail(ResultStatus.Validation, "A request id is required.");
        }

        var auth = await AuthorizeCartAsync(session, cart, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CheckoutQuote>();
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<CheckoutQuote>();
        }

        var noShift = await Shifts.ShiftService.RequireShiftIfConfiguredAsync(db, session.UserId, cancellationToken).ConfigureAwait(false);
        if (noShift is not null)
        {
            return noShift.As<CheckoutQuote>();
        }

        var now = clock.UtcNowMilliseconds;
        var priced = await SalePricing.PriceAsync(db, profiles, cart, now, cancellationToken).ConfigureAwait(false);
        if (!priced.IsSuccess)
        {
            return priced.As<CheckoutQuote>();
        }

        var quote = priced.Value!.Quote;
        var existing = await db.CheckoutRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != quote.CartFingerprint)
            {
                return Result<CheckoutQuote>.Fail(ResultStatus.Conflict, "This request id was already used for a different cart.");
            }

            return existing.State == RequestState.Prepared
                ? Result<CheckoutQuote>.Ok(quote)
                : Result<CheckoutQuote>.Fail(ResultStatus.Conflict, $"This checkout is already {existing.State}.");
        }

        var stock = await SalePricing.CheckStockAsync(db, priced.Value, cancellationToken).ConfigureAwait(false);
        if (stock is not null)
        {
            return Result<CheckoutQuote>.Fail(ResultStatus.InsufficientStock, stock);
        }

        if (quote.Calculation.TotalMinor <= 0)
        {
            return Result<CheckoutQuote>.Fail(ResultStatus.Validation, "Only positive-total sales can be completed under the current policy.");
        }

        var json = SalePricing.RequestJson(cart, quote);
        db.CheckoutRequests.Add(new CheckoutRequest
        {
            Id = requestId,
            ActorUserId = session.UserId,
            RequestFingerprint = quote.CartFingerprint,
            QuoteJson = json,
            QuoteSha256 = CanonicalJson.Sha256(json),
            State = RequestState.Prepared,
            PreparedUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<CheckoutQuote>.Ok(quote);
    }

    public async Task<Result<SaleReceipt>> CompleteAsync(UserSession session, Guid requestId, PaymentInput payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<SaleReceipt>();
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var request = await db.CheckoutRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.NotFound, "Prepare the checkout before taking payment.");
        }

        if (request.ActorUserId != session.UserId)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.Forbidden, "Only the cashier who prepared this checkout can complete it.");
        }

        // Idempotent repeat: the original sale is returned, nothing is charged or written again.
        if (request.State == RequestState.Completed)
        {
            return Result<SaleReceipt>.Ok(await LoadReceiptAsync(db, request.OrderId!.Value, [], cancellationToken).ConfigureAwait(false));
        }

        if (request.State == RequestState.Abandoned)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.Conflict, "This checkout was abandoned. Start a new one.");
        }

        var blocked = await RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<SaleReceipt>();
        }

        var noShift = await Shifts.ShiftService.RequireShiftIfConfiguredAsync(db, session.UserId, cancellationToken).ConfigureAwait(false);
        if (noShift is not null)
        {
            return noShift.As<SaleReceipt>();
        }

        var (cart, frozenHash, _) = SalePricing.ReadRequestJson(request.QuoteJson);
        if (cart.Discount is { Kind: not OrderDiscountKind.None })
        {
            var discountAuth = await identity.AuthorizeAsync(session, Capabilities.DiscountsApply, false, cancellationToken).ConfigureAwait(false);
            if (!discountAuth.IsSuccess)
            {
                return discountAuth.As<SaleReceipt>();
            }
        }

        var now = clock.UtcNowMilliseconds;
        var pricedResult = await SalePricing.PriceAsync(db, profiles, cart, now, cancellationToken).ConfigureAwait(false);
        if (!pricedResult.IsSuccess)
        {
            return pricedResult.As<SaleReceipt>();
        }

        var priced = pricedResult.Value!;
        var calc = priced.Quote.Calculation;
        if (priced.Quote.QuoteHash != frozenHash)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.StaleQuote,
                $"Prices or settings changed since this checkout was prepared. The new total is {calc.TotalMinor} minor units; review it and prepare again.");
        }

        var paymentCheck = ValidatePayment(payment, calc.TotalMinor);
        if (paymentCheck.Failure is not null)
        {
            return paymentCheck.Failure;
        }

        var stock = await SalePricing.CheckStockAsync(db, priced, cancellationToken).ConfigureAwait(false);
        if (stock is not null)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.InsufficientStock, stock);
        }

        var calendar = await profiles.GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.Validation, "No calendar profile is in effect; the business date cannot be determined.");
        }

        var installation = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var business = installation.CurrentBusinessProfileId is { } bpId
            ? await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bpId, cancellationToken).ConfigureAwait(false)
            : null;
        if (business is null)
        {
            return Result<SaleReceipt>.Fail(ResultStatus.Validation, "No business profile is configured for receipts.");
        }

        // Safe manual payment evidence is recorded on the request before the financial write.
        request.ExternalEvidenceJson = paymentCheck.EvidenceJson;
        request.Version++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var businessDate = calendar.Value!.Policy.BusinessDateFor(now);
        var reserved = await numbering.ReserveAsync(db, businessDate, requestId, cancellationToken).ConfigureAwait(false);
        if (!reserved.IsSuccess)
        {
            return reserved.As<SaleReceipt>();
        }

        var warnings = new List<string>();
        if (paymentCheck.Reference is { } reference
            && await db.Payments.AnyAsync(p => p.Reference == reference, cancellationToken).ConfigureAwait(false))
        {
            warnings.Add("This GCash reference was already recorded on another sale. Check it during reconciliation.");
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var orderId = await WriteSaleAsync(db, session, request, priced, payment, paymentCheck, reserved.Value!, calendar.Value, business, installation.InstallationId, now, cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<SaleReceipt>.Ok(await LoadReceiptAsync(db, orderId, warnings, cancellationToken).ConfigureAwait(false));
        }
        catch (DbUpdateException)
        {
            // The transaction rolled back: the reserved number is a recorded gap and is never reused.
            return Result<SaleReceipt>.Fail(ResultStatus.OutcomeUnknown,
                $"The sale could not be saved. Number {reserved.Value!.TransactionNumber} was skipped. Check this checkout's outcome before trying again.");
        }
    }

    public async Task<Result<CheckoutOutcome>> GetOutcomeAsync(UserSession session, Guid requestId, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CheckoutOutcome>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var request = await db.CheckoutRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(CheckoutState.NotFound, null, null));
        }

        if (request.ActorUserId != session.UserId && !session.Has(Capabilities.OrdersViewAll))
        {
            return Result<CheckoutOutcome>.Fail(ResultStatus.Forbidden, "You can only check your own checkouts.");
        }

        var (_, _, total) = SalePricing.ReadRequestJson(request.QuoteJson);
        return request.State switch
        {
            RequestState.Completed => Result<CheckoutOutcome>.Ok(new CheckoutOutcome(CheckoutState.Completed,
                await LoadReceiptAsync(db, request.OrderId!.Value, [], cancellationToken).ConfigureAwait(false), total)),
            RequestState.Abandoned => Result<CheckoutOutcome>.Ok(new CheckoutOutcome(CheckoutState.Abandoned, null, total)),
            _ => Result<CheckoutOutcome>.Ok(new CheckoutOutcome(CheckoutState.Prepared, null, total)),
        };
    }

    public async Task<Result<Unit>> AbandonAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var request = await db.CheckoutRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Result.Fail(ResultStatus.NotFound, "Checkout not found.");
        }

        if (request.ActorUserId != session.UserId && !session.Has(Capabilities.ShiftsManage))
        {
            return Result.Fail(ResultStatus.Forbidden, "Only the preparing cashier or a manager can abandon this checkout.");
        }

        if (request.State != RequestState.Prepared)
        {
            return Result.Fail(ResultStatus.Conflict, $"This checkout is already {request.State}.");
        }

        var now = clock.UtcNowMilliseconds;
        request.State = RequestState.Abandoned;
        request.ResolvedUtc = now;
        request.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Sales.CheckoutAbandoned", "CheckoutRequest", request.Id.ToString(), reason,
            new { hadPaymentEvidence = request.ExternalEvidenceJson is not null }, request.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    // ---------------------------------------------------------------- internals

    private sealed record PaymentCheck(Result<SaleReceipt>? Failure, CashSettlement? Cash, string? Reference, string? Note, string EvidenceJson);

    private PaymentCheck ValidatePayment(PaymentInput payment, long totalMinor)
    {
        static PaymentCheck Fail(ResultStatus status, string message) => new(Result<SaleReceipt>.Fail(status, message), null, null, null, "{}");

        switch (payment)
        {
            case CashPayment cash:
                try
                {
                    var settlement = CashTender.Settle(totalMinor, cash.TenderedMinor);
                    var json = CanonicalJson.Write(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("method", "Cash");
                        w.WriteNumber("tenderedM", settlement.TenderedMinor);
                        w.WriteNumber("recordedUtc", clock.UtcNowMilliseconds);
                        w.WriteEndObject();
                    });
                    return new PaymentCheck(null, settlement, null, null, json);
                }
                catch (FinancialRuleException ex)
                {
                    return Fail(ResultStatus.Validation, ex.Message);
                }

            case GCashPayment gcash:
                if (!gcash.ReceiptVerified)
                {
                    return Fail(ResultStatus.PaymentNotVerified, "Confirm that the GCash payment was received before completing the sale.");
                }

                if (totalMinor <= 0)
                {
                    return Fail(ResultStatus.Validation, "Only positive-total sales can be completed under the current policy.");
                }

                var reference = string.IsNullOrWhiteSpace(gcash.Reference) ? null : gcash.Reference.Trim();
                if (reference is { Length: > ReferenceMaximum })
                {
                    return Fail(ResultStatus.Validation, "The GCash reference is too long.");
                }

                var note = string.IsNullOrWhiteSpace(gcash.Note) ? null : gcash.Note.Trim();
                var noteInvalid = note is null ? null : TextRules.ValidateRequired(note, "Note", TextRules.ReasonMaximum);
                if (noteInvalid is not null)
                {
                    return Fail(ResultStatus.Validation, noteInvalid);
                }

                var evidence = CanonicalJson.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("method", "GCash");
                    w.WriteBoolean("verified", true);
                    w.WriteBoolean("hasReference", reference is not null);
                    w.WriteNumber("recordedUtc", clock.UtcNowMilliseconds);
                    w.WriteEndObject();
                });
                return new PaymentCheck(null, null, reference, note, evidence);

            default:
                return Fail(ResultStatus.Validation, "Choose Cash or GCash.");
        }
    }

    private async Task<Guid> WriteSaleAsync(
        PosDbContext db,
        UserSession session,
        CheckoutRequest request,
        PricedCart priced,
        PaymentInput paymentInput,
        PaymentCheck paymentCheck,
        ReservedNumber number,
        Application.Configuration.EffectiveCalendarProfile calendar,
        BusinessProfile business,
        Guid installationId,
        long now,
        CancellationToken cancellationToken)
    {
        await NumberSeriesService.ApplyReservationAsync(db, number, now, cancellationToken).ConfigureAwait(false);

        var calc = priced.Quote.Calculation;
        var shiftId = await db.CashierShifts.Where(s => s.CashierUserId == session.UserId && s.ClosedUtc == null)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CheckoutRequestId = request.Id,
            TransactionNumber = number.TransactionNumber,
            NumberSeriesKey = number.SeriesKey,
            Sequence = number.Sequence,
            OrderType = priced.Quote.OrderType,
            State = OrderState.Completed,
            CashierUserId = session.UserId,
            ShiftId = shiftId,
            CreatedUtc = request.PreparedUtc,
            CompletedUtc = now,
            BusinessDate = number.BusinessDate,
            TimezoneId = calendar.Policy.TimeZoneId,
            CutoffMinute = calendar.Policy.CutoffMinute,
            CurrencyCode = calc.Currency.Code,
            CurrencyExponent = calc.Currency.Exponent,
            CalculationPolicyId = priced.Profile.Profile.Id,
            BusinessProfileId = business.Id,
            NetM = calc.NetMinor,
            DiscountExTaxM = calc.DiscountExTaxMinor,
            TaxM = calc.TaxMinor,
            TotalM = calc.TotalMinor,
            PriceBasisSubtotalM = calc.PriceBasisSubtotalMinor,
            DiscountBasisM = calc.DiscountBasisMinor,
            SnapshotSchemaVersion = 1,
            SnapshotJson = "{}",
            SnapshotSha256 = new string('0', 64),
        };

        var items = new List<OrderItem>();
        var taxes = new List<ItemTaxComponent>();
        foreach (var line in priced.Quote.Lines)
        {
            var c = line.Calculation;
            var product = priced.Products[line.ProductId];
            var item = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                LineNo = c.LineNo,
                ProductId = product.Id,
                SkuSnapshot = product.Sku,
                NameSnapshot = product.Name,
                UnitPriceM = c.UnitPriceMinor,
                QuantityQ = c.QuantityQ,
                UnitStepQ = c.UnitStepQ,
                TrackStockSnapshot = product.TrackStock,
                PriceMode = c.PriceMode,
                TreatmentCode = c.Treatment,
                PriceBasisM = c.PriceBasisMinor,
                DiscountBasisM = c.DiscountBasisMinor,
                NetBeforeDiscountM = c.NetBeforeDiscountMinor,
                DiscountExTaxM = c.DiscountExTaxMinor,
                NetM = c.NetMinor,
                TaxM = c.TaxMinor,
                TotalM = c.TotalMinor,
                AppliedPolicyJson = CanonicalJson.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("taxKey", product.TaxKey);
                    w.WriteString("strategy", calc.Lines.Count > 0 ? priced.Profile.Profile.StrategyKey : string.Empty);
                    w.WriteEndObject();
                }),
            };
            items.Add(item);
            taxes.AddRange(c.TaxComponents.Select(t => new ItemTaxComponent
            {
                Id = Guid.NewGuid(),
                OrderItemId = item.Id,
                ComponentKey = t.Key,
                RatePpm = t.RatePpm,
                TaxableBaseM = t.TaxableBaseMinor,
                TaxM = t.TaxMinor,
                TreatmentCode = c.Treatment,
                RuleVersion = priced.Profile.Profile.StrategyKey,
            }));
        }

        var payment = paymentInput switch
        {
            CashPayment => new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Method = PaymentMethod.Cash,
                AppliedM = paymentCheck.Cash!.AppliedMinor,
                TenderedM = paymentCheck.Cash.TenderedMinor,
                ChangeM = paymentCheck.Cash.ChangeMinor,
                VerificationState = PaymentVerificationState.CashReceived,
                RecordedByUserId = session.UserId,
                PaidUtc = now,
            },
            _ => new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Method = PaymentMethod.GCash,
                AppliedM = calc.TotalMinor,
                Reference = paymentCheck.Reference,
                Note = paymentCheck.Note,
                VerificationState = PaymentVerificationState.Verified,
                VerifiedByUserId = session.UserId,
                VerifiedUtc = now,
                RecordedByUserId = session.UserId,
                PaidUtc = now,
            },
        };

        var context = new IssueContext(
            installationId,
            session.DisplayName,
            business,
            priced.Profile.Profile.Id,
            priced.Profile.Profile.Revision,
            priced.Profile.Profile.ApprovalState,
            priced.Profile.PayloadSha256,
            priced.Profile.Profile.StrategyKey,
            calendar.PolicyId,
            calendar.Revision,
            priced.Quote.Discount);
        order.SnapshotJson = LedgerSnapshots.Sale(order, items, taxes, payment, context);
        order.SnapshotSha256 = CanonicalJson.Sha256(order.SnapshotJson);

        db.Orders.Add(order);
        db.OrderItems.AddRange(items);
        db.ItemTaxComponents.AddRange(taxes);
        db.Payments.Add(payment);

        // Stock leaves only on completion (BRD §8), one movement per tracked line.
        foreach (var item in items.Where(i => i.TrackStockSnapshot))
        {
            var balance = await db.Inventory.SingleAsync(i => i.ProductId == item.ProductId, cancellationToken).ConfigureAwait(false);
            balance.QuantityQ = checked(balance.QuantityQ - item.QuantityQ);
            balance.UpdatedUtc = now;
            balance.Version++;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductId = item.ProductId,
                DeltaQ = -item.QuantityQ,
                Type = InventoryMovementType.Sale,
                ActorUserId = session.UserId,
                OrderId = order.Id,
                OrderItemId = item.Id,
                RequestId = request.Id,
                MovementKey = $"{request.Id:D}:{item.Id:D}:Sale",
                CreatedUtc = now,
                BalanceAfterQ = balance.QuantityQ,
            });
        }

        db.ArtifactRecords.Add(new ArtifactRecord
        {
            Id = Guid.NewGuid(),
            LogicalKey = $"sale/{order.Id:D}/schema1",
            Type = ArtifactType.SaleJson,
            SourceId = order.Id,
            OrderId = order.Id,
            PayloadSchemaVersion = 1,
            State = ArtifactState.Intent,
            CreatedUtc = now,
        });

        var printModel = LedgerSnapshots.ReceiptIntent("Sale", order.Id, order.TransactionNumber);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            RequestId = Guid.NewGuid(),
            Purpose = PrintPurpose.Original,
            TemplateVersion = business.TemplateVersion,
            ModelJson = printModel,
            ModelSha256 = CanonicalJson.Sha256(printModel),
            State = PrintJobState.Pending,
            CreatedByUserId = session.UserId,
            CreatedUtc = now,
        });

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Sales.Completed", "Order", order.Id.ToString(), null,
            new { transactionNumber = order.TransactionNumber, totalM = order.TotalM, method = payment.Method.ToString() }, request.Id);

        request.State = RequestState.Completed;
        request.OrderId = order.Id;
        request.ResolvedUtc = now;
        request.Version++;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return order.Id;
    }

    private static async Task<SaleReceipt> LoadReceiptAsync(PosDbContext db, Guid orderId, IReadOnlyList<string> warnings, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId, cancellationToken).ConfigureAwait(false);
        return new SaleReceipt(order.Id, order.CheckoutRequestId, order.TransactionNumber, order.BusinessDate, order.TotalM, payment.Method,
            payment.TenderedM, payment.ChangeM, warnings);
    }

    private async Task<Result<Unit>> AuthorizeCartAsync(UserSession session, CartInput cart, CancellationToken cancellationToken)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (auth.IsSuccess && cart.Discount is { Kind: not OrderDiscountKind.None })
        {
            auth = await identity.AuthorizeAsync(session, Capabilities.DiscountsApply, false, cancellationToken).ConfigureAwait(false);
        }

        return auth;
    }

    internal static async Task<Result<Unit>?> RequireNormalModeAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var mode = await db.InstallationState.AsNoTracking().Select(s => s.Mode).SingleAsync(cancellationToken).ConfigureAwait(false);
        return mode == InstallationMode.Normal
            ? null
            : Result.Fail(ResultStatus.RecoveryRequired, "Selling is paused: the installation is in recovery or maintenance.");
    }
}
