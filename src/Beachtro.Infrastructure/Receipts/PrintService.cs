using System.Text.Json;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Receipts;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Receipts;

/// <summary>
/// Receipts and print jobs (system design §Receipts; IB-15). Each attempt is persisted as Submitting
/// before the spooler is called and its result recorded afterwards; nothing about printing touches
/// the financial records.
/// </summary>
public sealed class PrintService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IReceiptPrinter printer,
    IClock clock,
    WriteGate writeGate) : IPrintService
{
    public async Task<Result<ReceiptDocument>> RenderSaleAsync(UserSession session, Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var order = await VisibleOrderAsync(db, session, orderId, cancellationToken).ConfigureAwait(false);
        return order.IsSuccess ? Result<ReceiptDocument>.Ok(ReceiptRenderer.RenderSale(order.Value!.SnapshotJson, reprint: false)) : order.As<ReceiptDocument>();
    }

    public async Task<Result<ReceiptDocument>> RenderCorrectionAsync(UserSession session, Guid correctionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rendered = await RenderCorrectionAsync(db, session, correctionId, reprint: false, cancellationToken).ConfigureAwait(false);
        return rendered;
    }

    public async Task<Result<PrintJobStatus>> PrintOriginalAsync(UserSession session, Guid? orderId, Guid? correctionId, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReceiptsPrint, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<PrintJobStatus>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var document = await RenderSourceAsync(db, session, orderId, correctionId, reprint: false, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return document.As<PrintJobStatus>();
        }

        var job = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Purpose == PrintPurpose.Original && j.OrderId == orderId && j.CorrectionId == correctionId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Result<PrintJobStatus>.Fail(ResultStatus.NotFound, "No receipt job exists for this record.");
        }

        if (job.State is PrintJobState.SpoolAccepted or PrintJobState.OperatorConfirmed or PrintJobState.Submitting)
        {
            return Result<PrintJobStatus>.Fail(ResultStatus.Conflict, "The original receipt was already sent to the printer. Use Reprint (with a reason) for another copy.");
        }

        return await SubmitAsync(session, job.Id, document.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PrintJobStatus>> ReprintAsync(UserSession session, Guid? orderId, Guid? correctionId, string reason, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReceiptsReprint, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<PrintJobStatus>();
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<PrintJobStatus>.Fail(ResultStatus.Validation, invalid);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var document = await RenderSourceAsync(db, session, orderId, correctionId, reprint: true, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return document.As<PrintJobStatus>();
        }

        var jobId = await CreateJobAsync(session, PrintPurpose.Reprint, orderId, correctionId, document.Value!, reason.Trim(), cancellationToken).ConfigureAwait(false);
        return await SubmitAsync(session, jobId, document.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PrintJobStatus>> TestPrintAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsOperational, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<PrintJobStatus>();
        }

        var printerName = await ConfiguredPrinterAsync(cancellationToken).ConfigureAwait(false) ?? "(no printer selected)";
        var document = ReceiptRenderer.RenderTestPage(printerName, DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMilliseconds));
        var jobId = await CreateJobAsync(session, PrintPurpose.Test, null, null, document, null, cancellationToken).ConfigureAwait(false);
        return await SubmitAsync(session, jobId, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Unit>> ConfirmAsync(UserSession session, Guid jobId, bool printed, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReceiptsPrint, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            auth = await identity.AuthorizeAsync(session, Capabilities.SettingsOperational, false, cancellationToken).ConfigureAwait(false);
            if (!auth.IsSuccess)
            {
                return auth;
            }
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.PrintJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Result.Fail(ResultStatus.NotFound, "Print job not found.");
        }

        if (job.State is not (PrintJobState.SpoolAccepted or PrintJobState.OutcomeUnknown))
        {
            return Result.Fail(ResultStatus.Conflict, $"This print job is {job.State}; there is nothing to confirm.");
        }

        job.State = printed ? PrintJobState.OperatorConfirmed : PrintJobState.Failed;
        job.LastError = printed ? null : "Operator reported that the receipt did not print.";
        AuditRecorder.Record(db, clock.UtcNowMilliseconds, session.UserId, session.DisplayName, printed ? "Print.Confirmed" : "Print.ReportedFailed",
            "PrintJob", job.Id.ToString(), null, new { purpose = job.Purpose.ToString() });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stuck = await db.PrintJobs.Where(j => j.State == PrintJobState.Submitting).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in stuck)
        {
            job.State = PrintJobState.OutcomeUnknown;
            job.LastError = "The application stopped while this receipt was being sent. Check whether it printed.";
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stuck.Count;
    }

    // ---------------------------------------------------------------- internals

    private async Task<Result<PrintJobStatus>> SubmitAsync(UserSession session, Guid jobId, ReceiptDocument document, CancellationToken cancellationToken)
    {
        var printerName = await ConfiguredPrinterAsync(cancellationToken).ConfigureAwait(false);

        using (var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = await db.PrintJobs.SingleAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
            job.AttemptCount++;
            job.PrinterName = printerName;
            if (printerName is null)
            {
                job.State = PrintJobState.Failed;
                job.LastError = "No receipt printer is selected. Choose one in Settings.";
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Result<PrintJobStatus>.Ok(Status(job, document));
            }

            // Recorded before spooling: a crash from here on surfaces as OutcomeUnknown, never a silent resend.
            job.State = PrintJobState.Submitting;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        PrintSubmission submission;
        try
        {
            submission = await printer.PrintAsync(document, printerName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            submission = new PrintSubmission(false, null, "The printer could not be reached.");
        }

        using (var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = await db.PrintJobs.SingleAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNowMilliseconds;
            job.State = submission.Accepted ? PrintJobState.SpoolAccepted : PrintJobState.Failed;
            job.SpoolJobId = submission.SpoolJobId;
            job.SubmittedUtc = submission.Accepted ? now : job.SubmittedUtc;
            job.LastError = submission.Accepted ? null : submission.Error ?? "The print was not accepted.";
            AuditRecorder.Record(db, now, session.UserId, session.DisplayName, $"Print.{job.Purpose}{(submission.Accepted ? "Sent" : "Failed")}",
                "PrintJob", job.Id.ToString(), job.Reason, new { number = document.Number, printer = printerName });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<PrintJobStatus>.Ok(Status(job, document));
        }
    }

    private async Task<Guid> CreateJobAsync(UserSession session, PrintPurpose purpose, Guid? orderId, Guid? correctionId, ReceiptDocument document, string? reason, CancellationToken cancellationToken)
    {
        var model = CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", "receipt-intent.v1");
            w.WriteString("sourceType", document.SourceType);
            w.WriteString("sourceId", document.SourceId.ToString("D"));
            w.WriteString("number", document.Number);
            w.WriteNumber("templateVersion", document.TemplateVersion);
            w.WriteBoolean("reprint", document.IsReprint);
            w.WriteEndObject();
        });

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CorrectionId = correctionId,
            RequestId = Guid.NewGuid(),
            Purpose = purpose,
            IsTestModel = purpose == PrintPurpose.Test,
            TemplateVersion = document.TemplateVersion,
            ModelJson = model,
            ModelSha256 = CanonicalJson.Sha256(model),
            State = PrintJobState.Pending,
            CreatedByUserId = session.UserId,
            CreatedUtc = clock.UtcNowMilliseconds,
            Reason = reason,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return job.Id;
    }

    private async Task<Result<ReceiptDocument>> RenderSourceAsync(PosDbContext db, UserSession session, Guid? orderId, Guid? correctionId, bool reprint, CancellationToken cancellationToken)
    {
        if ((orderId is null) == (correctionId is null))
        {
            return Result<ReceiptDocument>.Fail(ResultStatus.Validation, "Choose exactly one sale or correction.");
        }

        if (orderId is { } id)
        {
            var order = await VisibleOrderAsync(db, session, id, cancellationToken).ConfigureAwait(false);
            return order.IsSuccess ? Result<ReceiptDocument>.Ok(ReceiptRenderer.RenderSale(order.Value!.SnapshotJson, reprint)) : order.As<ReceiptDocument>();
        }

        return await RenderCorrectionAsync(db, session, correctionId!.Value, reprint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ReceiptDocument>> RenderCorrectionAsync(PosDbContext db, UserSession session, Guid correctionId, bool reprint, CancellationToken cancellationToken)
    {
        var correction = await db.Corrections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == correctionId, cancellationToken).ConfigureAwait(false);
        if (correction is null)
        {
            return Result<ReceiptDocument>.Fail(ResultStatus.NotFound, "Correction not found.");
        }

        var order = await VisibleOrderAsync(db, session, correction.OriginalOrderId, cancellationToken).ConfigureAwait(false);
        return order.IsSuccess
            ? Result<ReceiptDocument>.Ok(ReceiptRenderer.RenderCorrection(correction.SnapshotJson, order.Value!.SnapshotJson, reprint))
            : order.As<ReceiptDocument>();
    }

    /// <summary>Same scope rule as order history: own sales unless orders.view.all.</summary>
    private async Task<Result<Order>> VisibleOrderAsync(PosDbContext db, UserSession session, Guid orderId, CancellationToken cancellationToken)
    {
        var all = await identity.AuthorizeAsync(session, Capabilities.OrdersViewAll, false, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            var own = await identity.AuthorizeAsync(session, Capabilities.OrdersViewOwn, false, cancellationToken).ConfigureAwait(false);
            if (!own.IsSuccess)
            {
                return own.As<Order>();
            }
        }

        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
        return order is null || (!all.IsSuccess && order.CashierUserId != session.UserId)
            ? Result<Order>.Fail(ResultStatus.NotFound, "Sale not found.")
            : Result<Order>.Ok(order);
    }

    private async Task<string?> ConfiguredPrinterAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.BusinessSettings.AsNoTracking().Where(s => s.Key == OperationalSettingKeys.PrinterName).Select(s => s.ValueJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(doc.RootElement.GetString()) ? doc.RootElement.GetString() : null;
    }

    private static PrintJobStatus Status(PrintJob job, ReceiptDocument document) =>
        new(job.Id, job.Purpose, job.State, job.PrinterName, job.LastError, job.AttemptCount, document);
}
