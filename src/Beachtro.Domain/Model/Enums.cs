namespace Beachtro.Domain.Model;

// Persisted vocabularies (database design §Data dictionary). Stored as constrained TEXT using
// these exact names; renaming a member is a schema change.

public enum OrderType { DineIn, Takeout }

/// <summary>The financial Orders table holds completed sales only; drafts/requests live elsewhere.</summary>
public enum OrderState { Completed }

public enum PaymentMethod { Cash, GCash }

/// <summary>Manual payment evidence. Sales: CashReceived/Verified. Refunds: CashPaidOut/ManuallyRefunded/NoTransfer.</summary>
public enum PaymentVerificationState { CashReceived, Verified, CashPaidOut, ManuallyRefunded, NoTransfer }

public enum RequestState { Prepared, Completed, Abandoned }

public enum NumberSeriesStatus { Ready, Blocked, Exhausted }

public enum CorrectionKind { Refund, Void }

public enum StockDisposition { Return, Discard, NotTracked }

public enum InventoryMovementType { Opening, Sale, Adjustment, Return }

public enum CashDirection { In, Out }

public enum DiscountType { FixedM, PercentPpm }

public enum PolicyKind { Calculation, Calendar, Retention, AccessPolicy }

public enum InstallationMode { Normal, Maintenance, RecoveryRequired }

public enum PrintPurpose { Original, Reprint, Test }

public enum PrintJobState { Pending, Submitting, SpoolAccepted, Failed, OutcomeUnknown, OperatorConfirmed }

public enum ReportType { DailySales, PaymentSummary, ProductSales, InventoryStatus, CashierShift, MonthlySales, TransactionDetail }

public enum ArtifactType { SaleJson, CorrectionJson, ReportCsv, ReportPdf, Backup }

public enum ArtifactState { Intent, Ready }

public enum SyncState { Pending, InProgress, Succeeded, Failed, RecoveryHold }

public enum SyncOutcome { Succeeded, Failed, Unknown }

public enum BackupVerificationState { Created, Verified, Failed }

public enum BackupUploadState { NotQueued, Pending, Uploaded, Failed }

public enum ScheduledJobKind { DailyBackup, DailyReport, MonthlyReport }

public enum ScheduledJobState { Idle, Running, Failed }

public enum RecoveryDisposition { Hold, Completed, Failed }
