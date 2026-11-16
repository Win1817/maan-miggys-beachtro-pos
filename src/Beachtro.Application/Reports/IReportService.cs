using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Reports;

/// <summary>How a stored cell value is interpreted. Values are stored as invariant strings.</summary>
public enum ReportCellKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Signed integer minor units.</summary>
    Money,

    /// <summary>Signed integer thousandths of a unit.</summary>
    Quantity,

    /// <summary>Signed integer count.</summary>
    Count,
}

public sealed record ReportColumn(string Header, ReportCellKind Kind, int Width);

/// <summary>One labelled figure of the reporting dictionary, e.g. "Net revenue (ex-tax)".</summary>
public sealed record ReportMetric(string Label, ReportCellKind Kind, string Value);

public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string>? Totals);

/// <summary>
/// Immutable report content. It is the stored source for preview, CSV and PDF, so all outputs show
/// identical figures (T-12). It holds no generation time or user; those belong to the run.
/// </summary>
public sealed record ReportDocument(
    ReportType Type,
    string Title,
    string StartDate,
    string EndExclusiveDate,
    string CurrencyCode,
    int CurrencyExponent,
    string TimeZoneId,
    bool IsTest,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ReportMetric> Metrics,
    IReadOnlyList<ReportTable> Tables);

/// <summary>A stored report run: content plus who/when/revision and the ledger watermark. PreviewLines are the PDF text.</summary>
public sealed record ReportRunView(
    Guid RunId,
    int Revision,
    long GeneratedUtc,
    string GeneratedByName,
    string WatermarkSha256,
    bool IsBusinessClose,
    bool ReusedExistingRun,
    ReportDocument Document,
    IReadOnlyList<string> PreviewLines);

public sealed record ReportRequest(ReportType Type, string StartDate, string EndExclusiveDate);

public enum ReportFormat { Csv, Pdf }

public sealed record ReportFile(string FileName, byte[] Content);

public sealed record DashboardView(
    string BusinessDate,
    int SaleCount,
    long NetRevenueExTaxMinor,
    long NetTaxMinor,
    long NetCollectedMinor,
    long NetCashMinor,
    long NetGCashMinor,
    long AverageSaleMinor,
    int CorrectionCount,
    long CorrectionsTotalMinor,
    int OpenShiftCount,
    int OutOfStockCount,
    string? LastCloseSummary,
    int CurrencyExponent);

public sealed record BusinessCloseView(string BusinessDate, int Revision, long ClosedUtc, string ClosedByName, bool ChangedSinceClose);

/// <summary>
/// Dashboard, reports and exports (FR-021 to FR-023). Everything requires reports.view.export.
/// Reports read committed sale and correction events on their own business dates, each once.
/// </summary>
public interface IReportService
{
    Task<Result<DashboardView>> GetDashboardAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default);

    /// <summary>Generates and stores a run, or returns the latest run if nothing changed.</summary>
    Task<Result<ReportRunView>> GenerateAsync(UserSession session, ReportRequest request, CancellationToken cancellationToken = default);

    Task<Result<ReportRunView>> GetRunAsync(UserSession session, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Renders a stored run. The same run always yields the same bytes.</summary>
    Task<Result<ReportFile>> RenderAsync(UserSession session, Guid runId, ReportFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a business day was reviewed: a Daily Sales run with the ledger watermark. It
    /// never locks or changes sales; a later correction makes the close show as changed.
    /// </summary>
    Task<Result<BusinessCloseView>> CloseBusinessDayAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default);

    Task<Result<BusinessCloseView?>> GetBusinessCloseAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default);
}
