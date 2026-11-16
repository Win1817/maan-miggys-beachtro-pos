using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Receipts;

/// <summary>
/// A rendered receipt: fixed-width text lines from the immutable snapshot and a versioned template.
/// Never built from the current catalog or settings.
/// </summary>
public sealed record ReceiptDocument(string SourceType, Guid SourceId, string Number, int TemplateVersion, int Width, bool IsTest, bool IsReprint, IReadOnlyList<string> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}

/// <summary>What the Windows spooler reported. Spool acceptance is not proof that paper came out.</summary>
public sealed record PrintSubmission(bool Accepted, string? SpoolJobId, string? Error);

public sealed record PrintJobStatus(Guid JobId, PrintPurpose Purpose, PrintJobState State, string? PrinterName, string? LastError, int AttemptCount, ReceiptDocument? Document);

/// <summary>Printer adapter port; implemented by the desktop layer with Windows printing.</summary>
public interface IReceiptPrinter
{
    IReadOnlyList<string> InstalledPrinters();

    Task<PrintSubmission> PrintAsync(ReceiptDocument document, string printerName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Receipt rendering and the durable print-job lifecycle (FR-016; IB-15): the job is recorded before
/// spooling; physical outcome is confirmed by the operator; reprints are new jobs with a reason.
/// </summary>
public interface IPrintService
{
    Task<Result<ReceiptDocument>> RenderSaleAsync(UserSession session, Guid orderId, CancellationToken cancellationToken = default);

    Task<Result<ReceiptDocument>> RenderCorrectionAsync(UserSession session, Guid correctionId, CancellationToken cancellationToken = default);

    /// <summary>Prints (or retries) the single original receipt job of a sale or correction.</summary>
    Task<Result<PrintJobStatus>> PrintOriginalAsync(UserSession session, Guid? orderId, Guid? correctionId, CancellationToken cancellationToken = default);

    Task<Result<PrintJobStatus>> ReprintAsync(UserSession session, Guid? orderId, Guid? correctionId, string reason, CancellationToken cancellationToken = default);

    Task<Result<PrintJobStatus>> TestPrintAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>The operator says whether the receipt actually came out.</summary>
    Task<Result<Unit>> ConfirmAsync(UserSession session, Guid jobId, bool printed, CancellationToken cancellationToken = default);

    /// <summary>Startup: jobs left in Submitting by a crash become OutcomeUnknown, never silently resent.</summary>
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}
