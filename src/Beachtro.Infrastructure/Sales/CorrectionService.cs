using System.Text.Json;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Corrections;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sales;

/// <summary>
/// Refunds and voids as linked, immutable corrections (finance design §Corrections and void
/// accounting; D-04 provisional; IB-11). Amounts come from the original snapshot by cumulative
/// allocation, never from today's prices or tax settings.
/// </summary>
public sealed class CorrectionService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IProfileService profiles,
    IClock clock,
    WriteGate writeGate) : ICorrectionService
{
    public async Task<Result<CorrectionPreview>> PrepareAsync(UserSession session, Guid requestId, CorrectionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var auth = await AuthorizeKindAsync(session, input.Kind, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CorrectionPreview>();
        }

        var invalid = requestId == Guid.Empty ? "A request id is required." : TextRules.ValidateRequired(input.Reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<CorrectionPreview>.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await CheckoutService.RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<CorrectionPreview>();
        }

        var intentJson = IntentJson(input);
        var fingerprint = CanonicalJson.Sha256(intentJson);
        var existing = await db.CorrectionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Fingerprint != fingerprint)
        {
            return Result<CorrectionPreview>.Fail(ResultStatus.Conflict, "This request id was already used for a different correction.");
        }

        var plan = await PlanAsync(db, input, cancellationToken).ConfigureAwait(false);
        if (!plan.IsSuccess)
        {
            return plan.As<CorrectionPreview>();
        }

        if (existing is null)
        {
            db.CorrectionRequests.Add(new CorrectionRequest
            {
                Id = requestId,
                ActorUserId = session.UserId,
                OriginalOrderId = input.OriginalOrderId,
                Fingerprint = fingerprint,
                IntendedItemsJson = intentJson,
                State = RequestState.Prepared,
                PreparedUtc = clock.UtcNowMilliseconds,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (existing.State != RequestState.Prepared)
        {
            return Result<CorrectionPreview>.Fail(ResultStatus.Conflict, $"This correction is already {existing.State}.");
        }

        var p = plan.Value!;
        return Result<CorrectionPreview>.Ok(new CorrectionPreview(requestId, p.Lines.Sum(l => l.Amounts.NetMinor), p.Lines.Sum(l => l.Amounts.TaxMinor),
            p.Lines.Sum(l => l.Amounts.TotalMinor), p.Payment.Method));
    }

    public async Task<Result<CorrectionReceipt>> CompleteAsync(UserSession session, Guid requestId, RefundEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var request = await db.CorrectionRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Result<CorrectionReceipt>.Fail(ResultStatus.NotFound, "Prepare the correction first.");
        }

        var input = ReadIntent(request.IntendedItemsJson);
        var auth = await AuthorizeKindAsync(session, input.Kind, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CorrectionReceipt>();
        }

        if (request.State == RequestState.Completed)
        {
            return Result<CorrectionReceipt>.Ok(await LoadReceiptAsync(db, request.CorrectionId!.Value, cancellationToken).ConfigureAwait(false));
        }

        if (request.State == RequestState.Abandoned)
        {
            return Result<CorrectionReceipt>.Fail(ResultStatus.Conflict, "This correction was abandoned.");
        }

        var blocked = await CheckoutService.RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<CorrectionReceipt>();
        }

        var now = clock.UtcNowMilliseconds;
        var calendar = await profiles.GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return Result<CorrectionReceipt>.Fail(ResultStatus.Validation, "No calendar profile is in effect; the business date cannot be determined.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Re-read all committed corrections inside the transaction: a competing request that already
        // took the remaining quantity makes this one fail instead of over-refunding (F-11).
        var planResult = await PlanAsync(db, input, cancellationToken).ConfigureAwait(false);
        if (!planResult.IsSuccess)
        {
            return planResult.As<CorrectionReceipt>();
        }

        var plan = planResult.Value!;
        var total = plan.Lines.Sum(l => l.Amounts.TotalMinor);
        var refund = BuildRefund(evidence, plan.Payment.Method, total, session.UserId, now);
        if (refund.Failure is not null)
        {
            return refund.Failure;
        }

        var correction = new Correction
        {
            Id = Guid.NewGuid(),
            OriginalOrderId = plan.Order.Id,
            CorrectionRequestId = request.Id,
            CorrectionNumber = string.Empty,
            Kind = input.Kind,
            Reason = input.Reason.Trim(),
            ActorUserId = request.ActorUserId,
            ApprovedByUserId = session.UserId,
            ApprovalUtc = now,
            CreatedUtc = now,
            BusinessDate = calendar.Value!.Policy.BusinessDateFor(now),
            TimezoneId = calendar.Value.Policy.TimeZoneId,
            ShiftId = await db.CashierShifts.Where(s => s.CashierUserId == session.UserId && s.ClosedUtc == null)
                .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            NetM = plan.Lines.Sum(l => l.Amounts.NetMinor),
            DiscountExTaxM = plan.Lines.Sum(l => l.Amounts.DiscountExTaxMinor),
            TaxM = plan.Lines.Sum(l => l.Amounts.TaxMinor),
            TotalM = total,
            SnapshotJson = "{}",
            SnapshotSha256 = new string('0', 64),
        };
        correction.CorrectionNumber = $"RF-{correction.Id:D}";

        var items = new List<CorrectionItem>();
        var taxes = new List<CorrectionTaxComponent>();
        foreach (var line in plan.Lines)
        {
            var item = new CorrectionItem
            {
                Id = Guid.NewGuid(),
                CorrectionId = correction.Id,
                OriginalOrderItemId = line.Source.Id,
                QuantityQ = line.Amounts.QuantityQ,
                CumulativeReturnedQ = line.Amounts.CumulativeReturnedQ,
                NetM = line.Amounts.NetMinor,
                DiscountExTaxM = line.Amounts.DiscountExTaxMinor,
                TaxM = line.Amounts.TaxMinor,
                TotalM = line.Amounts.TotalMinor,
                DiscountBasisM = line.Amounts.DiscountBasisMinor,
                StockDisposition = line.Disposition,
                StockReturnedQ = line.Disposition == StockDisposition.Return ? line.Amounts.QuantityQ : 0,
            };
            items.Add(item);
            taxes.AddRange(line.Amounts.TaxComponents.Select(t => new CorrectionTaxComponent
            {
                Id = Guid.NewGuid(),
                CorrectionItemId = item.Id,
                OriginalTaxComponentId = line.Taxes.Single(o => o.ComponentKey == t.Key).Id,
                TaxM = t.TaxMinor,
            }));
        }

        var refundPayment = refund.Payment!;
        refundPayment.CorrectionId = correction.Id;

        var actorName = await db.Users.Where(u => u.Id == request.ActorUserId).Select(u => u.DisplayName).SingleAsync(cancellationToken).ConfigureAwait(false);
        correction.SnapshotJson = LedgerSnapshots.Correction(correction, plan.Order, items, taxes,
            plan.Lines.ToDictionary(l => l.Source.Id, l => l.Source), plan.Lines.SelectMany(l => l.Taxes).ToDictionary(t => t.Id),
            refundPayment, actorName, session.DisplayName);
        correction.SnapshotSha256 = CanonicalJson.Sha256(correction.SnapshotJson);

        db.Corrections.Add(correction);
        db.CorrectionItems.AddRange(items);
        db.CorrectionTaxComponents.AddRange(taxes);
        db.RefundPayments.Add(refundPayment);

        // Stock comes back only for an explicit Return disposition.
        foreach (var item in items.Where(i => i.StockDisposition == StockDisposition.Return))
        {
            var source = plan.Lines.Single(l => l.Source.Id == item.OriginalOrderItemId).Source;
            var balance = await db.Inventory.SingleAsync(i => i.ProductId == source.ProductId, cancellationToken).ConfigureAwait(false);
            balance.QuantityQ = checked(balance.QuantityQ + item.StockReturnedQ);
            balance.UpdatedUtc = now;
            balance.Version++;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductId = source.ProductId,
                DeltaQ = item.StockReturnedQ,
                Type = InventoryMovementType.Return,
                Reason = correction.Reason,
                ActorUserId = session.UserId,
                CorrectionItemId = item.Id,
                RequestId = request.Id,
                MovementKey = $"{request.Id:D}:{item.Id:D}:Return",
                CreatedUtc = now,
                BalanceAfterQ = balance.QuantityQ,
            });
        }

        db.ArtifactRecords.Add(new ArtifactRecord
        {
            Id = Guid.NewGuid(),
            LogicalKey = $"correction/{correction.Id:D}/schema1",
            Type = ArtifactType.CorrectionJson,
            SourceId = correction.Id,
            CorrectionId = correction.Id,
            PayloadSchemaVersion = 1,
            State = ArtifactState.Intent,
            CreatedUtc = now,
        });

        var business = await db.BusinessProfiles.AsNoTracking().SingleAsync(b => b.Id == plan.Order.BusinessProfileId, cancellationToken).ConfigureAwait(false);
        var printModel = LedgerSnapshots.ReceiptIntent("Correction", correction.Id, correction.CorrectionNumber);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            CorrectionId = correction.Id,
            RequestId = Guid.NewGuid(),
            Purpose = PrintPurpose.Original,
            TemplateVersion = business.TemplateVersion,
            ModelJson = printModel,
            ModelSha256 = CanonicalJson.Sha256(printModel),
            State = PrintJobState.Pending,
            CreatedByUserId = session.UserId,
            CreatedUtc = now,
        });

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, $"Sales.{input.Kind}Completed", "Correction", correction.Id.ToString(), correction.Reason,
            new
            {
                originalTransactionNumber = plan.Order.TransactionNumber,
                totalM = correction.TotalM,
                method = refundPayment.Method.ToString(),
                preparedBy = request.ActorUserId,
                approvedBy = session.UserId,
            },
            request.Id);

        request.State = RequestState.Completed;
        request.CorrectionId = correction.Id;
        request.ResolvedUtc = now;
        request.PaymentEvidenceJson = CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("method", refundPayment.Method.ToString());
            w.WriteString("verification", refundPayment.VerificationState.ToString());
            w.WriteEndObject();
        });
        request.Version++;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return Result<CorrectionReceipt>.Fail(ResultStatus.OutcomeUnknown, "The correction could not be saved. Check its outcome before trying again; no money was moved by the system.");
        }

        return Result<CorrectionReceipt>.Ok(new CorrectionReceipt(correction.Id, correction.CorrectionNumber, correction.Kind, correction.NetM, correction.TaxM,
            correction.TotalM, correction.BusinessDate));
    }

    public async Task<Result<Unit>> AbandonAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var request = await db.CorrectionRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Result.Fail(ResultStatus.NotFound, "Correction not found.");
        }

        var auth = await AuthorizeKindAsync(session, ReadIntent(request.IntendedItemsJson).Kind, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        if (request.State != RequestState.Prepared)
        {
            return Result.Fail(ResultStatus.Conflict, $"This correction is already {request.State}.");
        }

        var now = clock.UtcNowMilliseconds;
        request.State = RequestState.Abandoned;
        request.ResolvedUtc = now;
        request.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Sales.CorrectionAbandoned", "CorrectionRequest", request.Id.ToString(), reason, null, request.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    // ---------------------------------------------------------------- internals

    private sealed record PlannedLine(OrderItem Source, IReadOnlyList<ItemTaxComponent> Taxes, StockDisposition Disposition, CorrectionLineAmounts Amounts);

    private sealed record Plan(Order Order, Payment Payment, IReadOnlyList<PlannedLine> Lines);

    private static async Task<Result<Plan>> PlanAsync(PosDbContext db, CorrectionInput input, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(input.Kind) || input.Lines is null || input.Lines.Count == 0)
        {
            return Result<Plan>.Fail(ResultStatus.Validation, "Choose at least one line to correct.");
        }

        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == input.OriginalOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return Result<Plan>.Fail(ResultStatus.NotFound, "Original sale not found.");
        }

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == order.Id, cancellationToken).ConfigureAwait(false);
        var sourceItems = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).ToDictionaryAsync(i => i.Id, cancellationToken).ConfigureAwait(false);
        var ids = sourceItems.Keys.ToArray();
        var sourceTaxes = await db.ItemTaxComponents.AsNoTracking().Where(t => ids.Contains(t.OrderItemId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var returned = await db.CorrectionItems.AsNoTracking().Where(c => ids.Contains(c.OriginalOrderItemId))
            .GroupBy(c => c.OriginalOrderItemId).Select(g => new { g.Key, Q = g.Sum(c => c.QuantityQ) })
            .ToDictionaryAsync(x => x.Key, x => x.Q, cancellationToken).ConfigureAwait(false);

        if (input.Lines.Select(l => l.OrderItemId).Distinct().Count() != input.Lines.Count)
        {
            return Result<Plan>.Fail(ResultStatus.Validation, "Each original line may appear once per correction.");
        }

        if (input.Kind == CorrectionKind.Void)
        {
            if (returned.Count > 0)
            {
                return Result<Plan>.Fail(ResultStatus.Conflict, "A sale with an earlier refund cannot be voided; refund the remaining items instead.");
            }

            if (input.Lines.Count != sourceItems.Count || input.Lines.Any(l => !sourceItems.TryGetValue(l.OrderItemId, out var s) || l.QuantityQ != s.QuantityQ))
            {
                return Result<Plan>.Fail(ResultStatus.Validation, "A void reverses every line at its full quantity.");
            }
        }

        var lines = new List<PlannedLine>();
        foreach (var line in input.Lines)
        {
            if (!sourceItems.TryGetValue(line.OrderItemId, out var source))
            {
                return Result<Plan>.Fail(ResultStatus.Validation, "A correction line does not belong to this sale.");
            }

            var disposition = line.Disposition;
            if (!Enum.IsDefined(disposition)
                || (source.TrackStockSnapshot && disposition == StockDisposition.NotTracked)
                || (!source.TrackStockSnapshot && disposition != StockDisposition.NotTracked))
            {
                return Result<Plan>.Fail(ResultStatus.Validation, $"Line {source.LineNo}: choose Return or Discard for stock-tracked items, NotTracked otherwise.");
            }

            if (disposition == StockDisposition.Return
                && !await db.Products.AnyAsync(p => p.Id == source.ProductId && p.TrackStock, cancellationToken).ConfigureAwait(false))
            {
                return Result<Plan>.Fail(ResultStatus.Validation, $"Line {source.LineNo}: the product no longer tracks stock; reconcile inventory explicitly instead of returning it.");
            }

            var taxes = sourceTaxes.Where(t => t.OrderItemId == source.Id).ToArray();
            var original = new OriginalLineComponents(source.QuantityQ, source.UnitStepQ, source.NetM, source.DiscountExTaxM, source.DiscountBasisM,
                taxes.Select(t => new OriginalTaxComponent(t.ComponentKey, t.TaxM)).ToArray());
            try
            {
                var amounts = CorrectionAllocator.Allocate(original, returned.GetValueOrDefault(source.Id), line.QuantityQ);
                lines.Add(new PlannedLine(source, taxes, disposition, amounts));
            }
            catch (FinancialRuleException ex) when (ex.Code == FinancialErrorCode.OverRefund)
            {
                return Result<Plan>.Fail(ResultStatus.Conflict, $"Line {source.LineNo}: that quantity exceeds what remains to be refunded.");
            }
            catch (FinancialRuleException ex)
            {
                return Result<Plan>.Fail(ResultStatus.Validation, $"Line {source.LineNo}: {ex.Message}");
            }
        }

        if (lines.All(l => l.Amounts.TotalMinor == 0 && l.Amounts.QuantityQ == 0))
        {
            return Result<Plan>.Fail(ResultStatus.Validation, "Nothing to correct.");
        }

        return Result<Plan>.Ok(new Plan(order, payment, lines));
    }

    private sealed record RefundBuild(Result<CorrectionReceipt>? Failure, RefundPayment? Payment);

    /// <summary>Refunds go back by the original tender (B-02); a zero-value correction records no transfer.</summary>
    private static RefundBuild BuildRefund(RefundEvidence evidence, PaymentMethod originalMethod, long total, Guid actor, long now)
    {
        RefundPayment Make(PaymentVerificationState state, string? reference) => new()
        {
            Id = Guid.NewGuid(),
            Method = originalMethod,
            AmountM = total,
            VerificationState = state,
            VerifiedByUserId = actor,
            VerifiedUtc = now,
            Reference = reference,
            RecordedByUserId = actor,
        };

        if (total == 0)
        {
            return new RefundBuild(null, Make(PaymentVerificationState.NoTransfer, null));
        }

        return (evidence, originalMethod) switch
        {
            (CashPaidOut, PaymentMethod.Cash) => new RefundBuild(null, Make(PaymentVerificationState.CashPaidOut, null)),
            (GCashRefunded g, PaymentMethod.GCash) => g.Reference is { Length: > CheckoutService.ReferenceMaximum }
                ? new RefundBuild(Result<CorrectionReceipt>.Fail(ResultStatus.Validation, "The GCash reference is too long."), null)
                : new RefundBuild(null, Make(PaymentVerificationState.ManuallyRefunded, string.IsNullOrWhiteSpace(g.Reference) ? null : g.Reference.Trim())),
            _ => new RefundBuild(Result<CorrectionReceipt>.Fail(ResultStatus.Validation, $"Refunds go back by the original payment method ({originalMethod})."), null),
        };
    }

    private async Task<Result<Unit>> AuthorizeKindAsync(UserSession session, CorrectionKind kind, CancellationToken cancellationToken) =>
        await identity.AuthorizeAsync(session, kind == CorrectionKind.Void ? Capabilities.CorrectionsVoid : Capabilities.CorrectionsRefund, true, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<CorrectionReceipt> LoadReceiptAsync(PosDbContext db, Guid correctionId, CancellationToken cancellationToken)
    {
        var c = await db.Corrections.AsNoTracking().SingleAsync(x => x.Id == correctionId, cancellationToken).ConfigureAwait(false);
        return new CorrectionReceipt(c.Id, c.CorrectionNumber, c.Kind, c.NetM, c.TaxM, c.TotalM, c.BusinessDate);
    }

    private static string IntentJson(CorrectionInput input) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", "correction-request.v1");
            w.WriteString("originalOrderId", input.OriginalOrderId.ToString("D"));
            w.WriteString("kind", input.Kind.ToString());
            w.WriteString("reason", input.Reason?.Trim() ?? string.Empty);
            w.WriteStartArray("lines");
            foreach (var l in input.Lines ?? [])
            {
                w.WriteStartObject();
                w.WriteString("orderItemId", l.OrderItemId.ToString("D"));
                w.WriteNumber("quantityQ", l.QuantityQ);
                w.WriteString("disposition", l.Disposition.ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });

    private static CorrectionInput ReadIntent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new CorrectionInput(
            Guid.Parse(r.GetProperty("originalOrderId").GetString()!),
            Enum.Parse<CorrectionKind>(r.GetProperty("kind").GetString()!),
            r.GetProperty("lines").EnumerateArray().Select(l => new CorrectionLineInput(
                Guid.Parse(l.GetProperty("orderItemId").GetString()!),
                l.GetProperty("quantityQ").GetInt64(),
                Enum.Parse<StockDisposition>(l.GetProperty("disposition").GetString()!))).ToArray(),
            r.GetProperty("reason").GetString()!);
    }
}
