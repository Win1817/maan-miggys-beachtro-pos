**MA'AN AND MIGGY'S  
BEACHTRO**

**POINT OF SALE (POS) SYSTEM**

**BUSINESS REQUIREMENTS DOCUMENT  
SOFTWARE REQUIREMENTS SPECIFICATION  
TECHNICAL IMPLEMENTATION SPECIFICATION**

| **Document Version** | 1.2                                   |
|----------------------|---------------------------------------|
| **Date**             | November 16, 2026                     |
| **Status**           | For Business Review and Approval      |
| **Target Platform**  | Native Windows 11 Desktop Application |

*Prepared as a project baseline. Items marked pending require owner or
accountant confirmation before production.*

# Document Control

| **Item**         | **Details**                                                                                 |
|------------------|---------------------------------------------------------------------------------------------|
| Document title   | Ma'an and Miggy's Beachtro POS System – BRD, SRS and Technical Implementation Specification |
| Version          | 1.2                                                                                         |
| Prepared for     | Ma'an and Miggy's Beachtro                                                                  |
| Document status  | For Business Review and Approval                                                            |
| Review authority | Business owners and appointed business representative                                       |

## Revision History

| **Version** | **Date**          | **Description**                                                                     |
|-------------|-------------------|-------------------------------------------------------------------------------------|
| 1.0         | November 16, 2026 | Initial requirements baseline.                                                      |
| 1.1         | November 16, 2026 | Added Google Drive sales synchronization and cloud backup requirements.             |
| 1.2         | November 16, 2026 | Consolidated BRD, SRS, technical design, acceptance criteria and pending decisions. |

# Table of Contents

- 1\. Executive Summary

- 2\. Project Background

- 3\. Business Objectives

- 4\. Confirmed Business Requirements

- 5\. Project Scope

- 6\. Stakeholders and User Roles

- 7\. Functional Requirements

- 8\. Business Rules

- 9\. Non-Functional Requirements

- 10\. Technical Architecture

- 11\. Database Design Specification

- 12\. Application UI/UX Specification

- 13\. Use Case Specifications

- 14\. Sales and Transaction Processing

- 15\. Google Drive Synchronization

- 16\. Receipt and Printing Management

- 17\. Reporting and Analytics

- 18\. Security Requirements

- 19\. Backup and Disaster Recovery

- 20\. Philippine Business and Tax Considerations

- 21\. Hardware and Software Requirements

- 22\. Implementation Roadmap

- 23\. Testing and Quality Assurance

- 24\. Acceptance Criteria

- 25\. Risks and Mitigation

- 26\. Assumptions and Dependencies

- 27\. Future Enhancements

- 28\. Project Deliverables

- 29\. Outstanding Business Decisions

- 30\. Approval and Sign-Off

# 1. Executive Summary

Ma'an and Miggy's Beachtro requires a native Windows 11 Point of Sale
(POS) application to record sales, manage a finished-product catalog,
track finished-product stock, process cash and GCash payments, print
customer receipts, and provide operational and sales reports.

The initial deployment will use one Windows terminal. The solution
should be designed so that additional terminals can be introduced in a
later phase without requiring a complete redesign. The application must
remain usable when the internet connection is unavailable. Local storage
is the operational source of truth; Google Drive provides off-site
copies of sales records, reports and backups.

The proposed implementation uses C#/.NET with WPF, MVVM and SQLite.
Google Drive synchronization will run in the background and must not
block checkout or other core POS operations.

# 2. Project Background

The business needs a consistent way to record customer orders, calculate
totals, accept payments, maintain product availability and review sales
performance. A dedicated desktop POS is intended to reduce manual
calculation, improve transaction traceability and make end-of-day
reconciliation easier.

The system is designed for café operations with two order types: dine-in
and takeout. The initial inventory model covers finished products only;
it does not include ingredient-level stock, recipe costing or automatic
ingredient deductions.

# 3. Business Objectives

- Record every sale in a consistent, searchable transaction ledger.

- Reduce cashier calculation errors through automatic totals, discounts
  and change calculation.

- Support dine-in and takeout orders.

- Accept and record cash and GCash payments.

- Maintain stock quantities for finished products.

- Print clear customer receipts using a supported receipt printer.

- Provide daily, monthly and date-range sales reports.

- Keep checkout available during internet interruptions.

- Synchronize transaction exports, reports and backups to Google Drive.

- Establish a design that can support multiple terminals in a future
  phase.

# 4. Confirmed Business Requirements

| **Area**              | **Confirmed decision**                                                  |
|-----------------------|-------------------------------------------------------------------------|
| Deployment            | Native Windows 11 desktop application.                                  |
| Initial terminals     | Single terminal initially; future multi-terminal expansion is required. |
| Order types           | Dine-in and Takeout.                                                    |
| Payment methods       | Cash and GCash.                                                         |
| Inventory             | Finished products only; no ingredient or recipe inventory.              |
| Printing              | Customer receipt printing.                                              |
| Cloud synchronization | Sales data and supporting exports/backups synchronized to Google Drive. |
| Tax configuration     | Pending confirmation by the business owner and/or accountant.           |

# 5. Project Scope

## 5.1 In Scope

- Product and category maintenance, including selling price and
  active/inactive status.

- Finished-product inventory quantities and stock adjustments.

- Order creation, item quantity changes, order type selection and
  checkout.

- Cash and GCash payment recording.

- Receipt generation and printing.

- Transaction search, void/refund recording with audit trail and
  permissions.

- Cashier shifts and cash movement recording, subject to final business
  procedure.

- Sales dashboards and reports.

- User accounts, roles and access control.

- Local SQLite database and local backup.

- Google Drive synchronization for transaction exports, reports and
  database backups.

- Installer, configuration, documentation and test evidence.

## 5.2 Out of Scope for Initial Release

- Ingredient-level inventory, recipes, food costing and automatic
  ingredient consumption.

- Online ordering, delivery marketplace integration or customer-facing
  mobile application.

- Integrated GCash payment gateway or automatic payment confirmation;
  GCash is manually verified and recorded.

- Payroll, accounting general ledger, supplier purchasing and full
  procurement workflows.

- Real-time multi-terminal database sharing.

- Customer loyalty, points, memberships and CRM.

- Advanced kitchen display system or kitchen ticket routing.

- Automatic BIR filing or certification claims. Compliance requirements
  must be separately validated.

# 6. Stakeholders and User Roles

| **Role**      | **Primary responsibilities**                              | **Typical permissions**                                          |
|---------------|-----------------------------------------------------------|------------------------------------------------------------------|
| Owner         | Business configuration, oversight, reports and approvals. | Full business access; user and settings administration.          |
| Manager       | Daily operations, products, stock, shifts and reports.    | Operational management; restricted high-risk settings.           |
| Cashier       | Create orders, accept payment and issue receipts.         | POS checkout and permitted transaction lookup.                   |
| Administrator | Technical setup, accounts, backups and integrations.      | System administration; business permissions remain configurable. |

The business should decide whether one person may hold multiple roles.
The system should support role-based permissions rather than hard-coding
access by job title.

# 7. Functional Requirements

| **ID** | **Function**              | **Requirement**                                                                                                   | **Priority** |
|--------|---------------------------|-------------------------------------------------------------------------------------------------------------------|--------------|
| FR-001 | Authentication            | The system shall require an authorized user to sign in before accessing protected functions.                      | Must         |
| FR-002 | User management           | Authorized administrators shall create, edit, deactivate and reset user accounts.                                 | Must         |
| FR-003 | Role permissions          | The system shall enforce permissions for POS, products, inventory, reports, refunds, settings and administration. | Must         |
| FR-004 | Product catalog           | Authorized users shall create, edit, activate and deactivate finished products.                                   | Must         |
| FR-005 | Categories                | Authorized users shall organize products into categories.                                                         | Must         |
| FR-006 | Product pricing           | Each active product shall have a configured selling price and currency.                                           | Must         |
| FR-007 | Stock tracking            | The system shall maintain an on-hand quantity for each stock-tracked finished product.                            | Must         |
| FR-008 | Stock adjustment          | Authorized users shall record stock additions, deductions and corrections with a reason and audit record.         | Must         |
| FR-009 | Order type                | A cashier shall identify each order as dine-in or takeout.                                                        | Must         |
| FR-010 | Order entry               | A cashier shall add products, change quantities and remove items before payment.                                  | Must         |
| FR-011 | Order totals              | The system shall calculate line totals, subtotal, permitted discounts, tax where configured and grand total.      | Must         |
| FR-012 | Checkout                  | The system shall finalize a transaction after required payment details are recorded.                              | Must         |
| FR-013 | Cash payment              | The system shall record cash tendered and calculate change due.                                                   | Must         |
| FR-014 | GCash payment             | The system shall record GCash as the payment method and allow a reference number or note where available.         | Must         |
| FR-015 | Manual GCash verification | The cashier or authorized user shall confirm receipt of GCash payment before completing the sale.                 | Must         |
| FR-016 | Receipt                   | The system shall generate and print a receipt for a completed transaction.                                        | Must         |
| FR-017 | Transaction history       | Authorized users shall search and view completed transactions.                                                    | Must         |
| FR-018 | Voids and refunds         | The system shall record voids and refunds as traceable actions with reason, user and timestamp.                   | Must         |
| FR-019 | Cashier shifts            | The system should support opening and closing a cashier shift and recording counted cash.                         | Should       |
| FR-020 | Cash movements            | The system should record cash-in and cash-out movements with reason and authorization.                            | Should       |
| FR-021 | Dashboard                 | The system shall display key daily sales and transaction indicators.                                              | Must         |
| FR-022 | Reports                   | Authorized users shall generate daily, monthly and date-range sales reports.                                      | Must         |
| FR-023 | Export                    | Authorized users shall export applicable reports to CSV and PDF.                                                  | Must         |
| FR-024 | Audit log                 | The system shall record significant administrative and financial actions.                                         | Must         |
| FR-025 | Offline operation         | Core POS functions shall operate without an active internet connection.                                           | Must         |
| FR-026 | Drive sync queue          | The system shall queue cloud synchronization work locally when internet or Google Drive is unavailable.           | Must         |
| FR-027 | Sync status               | The system shall show pending, successful and failed synchronization status.                                      | Must         |
| FR-028 | Local backup              | The system shall create a scheduled local database backup.                                                        | Must         |
| FR-029 | Settings                  | Authorized users shall configure business details, receipt settings, tax settings and sync preferences.           | Must         |
| FR-030 | Future terminal readiness | The architecture shall avoid designs that prevent later migration to a centralized multi-terminal backend.        | Should       |

# 8. Business Rules

- Every completed transaction must have a unique transaction number.
  Suggested format: MB-YYYYMMDD-000001.

- A transaction must not be treated as completed until the payment
  requirements are satisfied.

- Stock deductions shall occur only after successful transaction
  completion. A failed or abandoned checkout must not reduce stock.

- Negative stock should be prevented by default. Any override must
  require explicit permission and be logged.

- Completed transactions must not be silently edited. Corrections must
  use a void, refund or adjustment workflow that preserves the original
  record.

- GCash payments are manually verified in the initial release. Recording
  GCash as a method alone is not proof of settlement.

- All monetary values must use decimal-safe arithmetic; binary
  floating-point must not be used for financial calculations.

- All transaction timestamps must be stored consistently and displayed
  in the business's configured local time zone.

- Local records remain available after successful cloud synchronization.

- Cloud synchronization failure must not prevent a sale from being
  completed.

- Discounts, tax treatment and refund authority must follow
  business-approved policies.

- Inventory movements must record the product, quantity change, reason,
  user and timestamp.

# 9. Non-Functional Requirements

| **Category**    | **Requirement**                                                                                                                   |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------|
| Usability       | Common checkout tasks should be achievable with a small number of clear interactions and readable controls.                       |
| Performance     | Product lookup and cart updates should feel immediate; checkout should not wait for cloud synchronization.                        |
| Availability    | Core POS workflows must work offline after the application and local database are initialized.                                    |
| Reliability     | Transactions must be committed atomically to prevent partial orders or inconsistent payment records.                              |
| Data integrity  | Use database constraints, transactions and validated input for critical records.                                                  |
| Security        | Use authenticated access, role-based permissions, protected secrets and audit logging.                                            |
| Maintainability | Use modular services, dependency injection, automated tests and documented configuration.                                         |
| Recoverability  | Provide verified local backup and documented restore procedures.                                                                  |
| Compatibility   | Target supported 64-bit Windows 11 systems and Windows-compatible receipt printers.                                               |
| Scalability     | Initial local deployment; future multi-terminal deployment should use a central API and server database.                          |
| Localization    | Display currency and date/time in formats approved by the business; Philippine peso (PHP) is expected unless confirmed otherwise. |

# 10. Technical Architecture

Proposed technology stack:

| **Layer**                | **Proposed technology**           | **Purpose**                                                      |
|--------------------------|-----------------------------------|------------------------------------------------------------------|
| Desktop UI               | C# / .NET 10 LTS and WPF          | Native Windows desktop interface.                                |
| Presentation pattern     | MVVM                              | Separates view state and business logic.                         |
| Local database           | SQLite with Entity Framework Core | Local transactional data store for the single-terminal release.  |
| Logging                  | Serilog                           | Structured application and diagnostic logs.                      |
| Background processing    | .NET BackgroundService            | Scheduled sync, exports and backup tasks.                        |
| Cloud integration        | Google Drive API v3               | Upload exports, reports and backup files.                        |
| Authentication to Google | OAuth 2.0                         | User-authorized access to the designated Drive account.          |
| Secret protection        | Windows DPAPI                     | Protect refresh/access tokens and sensitive local configuration. |
| Deployment               | MSIX or Windows Installer         | Repeatable installation and update process.                      |

## 10.1 Logical Components

- Presentation Layer: WPF views, view models, navigation and input
  validation.

- Application Layer: use cases for checkout, product maintenance,
  inventory, reporting and administration.

- Domain Layer: entities, business rules, money calculations and
  transaction state transitions.

- Infrastructure Layer: SQLite/EF Core repositories, logging, printer
  adapter, Google Drive client and file storage.

- Background Services: synchronization queue processor, scheduled
  exports and backup scheduler.

## 10.2 Deployment Principle

SQLite is suitable for the initial single-terminal deployment. The
SQLite database file must not be placed on a shared network drive for
concurrent multi-terminal use. When multiple terminals are introduced,
deploy a centralized application service/API and a server database such
as PostgreSQL. Terminals should communicate with the service over the
local network, with an explicit offline strategy designed for that
phase.

# 11. Database Design Specification

The following logical tables form the proposed initial schema. Final
column definitions, indexes, constraints and migrations should be
established during detailed design.

| **Table**            | **Purpose / key fields**                                                                                                         |
|----------------------|----------------------------------------------------------------------------------------------------------------------------------|
| Users                | UserId, Username, PasswordHash, DisplayName, IsActive, CreatedAt, UpdatedAt.                                                     |
| Roles                | RoleId, RoleName, Description.                                                                                                   |
| Permissions          | PermissionId, PermissionKey, Description.                                                                                        |
| UserRoles            | UserId, RoleId; maps users to roles.                                                                                             |
| RolePermissions      | RoleId, PermissionId; maps roles to permissions.                                                                                 |
| Categories           | CategoryId, Name, Description, IsActive.                                                                                         |
| Products             | ProductId, CategoryId, SKU, Name, Description, SellingPrice, TrackStock, IsActive.                                               |
| Orders               | OrderId, TransactionNumber, OrderType, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, CashierId, CreatedAt, CompletedAt. |
| OrderItems           | OrderItemId, OrderId, ProductId, ProductNameSnapshot, UnitPrice, Quantity, LineTotal.                                            |
| Payments             | PaymentId, OrderId, Method, Amount, ReferenceNumber, VerificationStatus, PaidAt, RecordedBy.                                     |
| Inventory            | InventoryId, ProductId, QuantityOnHand, ReorderLevel, UpdatedAt.                                                                 |
| InventoryMovements   | MovementId, ProductId, MovementType, QuantityChange, Reason, RelatedOrderId, UserId, CreatedAt.                                  |
| CashierShifts        | ShiftId, CashierId, OpenedAt, OpeningCash, ClosedAt, ClosingCash, ExpectedCash, Variance.                                        |
| CashMovements        | CashMovementId, ShiftId, Type, Amount, Reason, RecordedBy, CreatedAt.                                                            |
| Discounts            | DiscountId, Name, DiscountType, DiscountValue, IsActive, RequiresApproval.                                                       |
| TaxConfigurations    | TaxConfigId, TaxType, Rate, Inclusive, EffectiveFrom, EffectiveTo, IsActive.                                                     |
| BusinessSettings     | SettingKey, SettingValue, UpdatedBy, UpdatedAt.                                                                                  |
| AuditLogs            | AuditId, UserId, Action, EntityType, EntityId, Details, CreatedAt.                                                               |
| SyncQueue            | QueueId, ItemType, LocalRecordId, PayloadPath, Status, RetryCount, NextAttemptAt, CreatedAt.                                     |
| SyncHistory          | SyncId, QueueId, Status, DriveFileId, ErrorMessage, AttemptedAt, CompletedAt.                                                    |
| CloudStorageSettings | Provider, DriveFolderId, AccountEmail, LastConnectedAt, IsEnabled.                                                               |
| SyncFiles            | FileId, FileType, LocalPath, DriveFileId, Checksum, CreatedAt, UploadedAt.                                                       |

Design notes: preserve product name and unit price snapshots on order
items so historical receipts remain correct after catalog changes. Use
foreign keys and indexes for transaction number, order date, product,
cashier and sync status. Passwords must be stored as salted password
hashes using a current password-hashing algorithm, never as plaintext.

# 12. Application UI/UX Specification

| **Screen**        | **Main functions**                                                                                            |
|-------------------|---------------------------------------------------------------------------------------------------------------|
| Login             | Username/password sign-in, validation and session start.                                                      |
| Dashboard         | Today's sales, transaction count, payment split, low-stock items and sync status.                             |
| POS / Checkout    | Order type, product/category selection, cart, quantity editing, discounts if authorized, payment and receipt. |
| Orders            | Search by transaction number/date/status/cashier; view transaction details.                                   |
| Products          | Create/edit products, category, price, SKU and active status.                                                 |
| Categories        | Maintain product groupings.                                                                                   |
| Inventory         | On-hand quantities, stock adjustment and movement history.                                                    |
| Sales Reports     | Daily/monthly/date-range reports, payment breakdown and export.                                               |
| Cashier Shifts    | Open/close shift, cash count and variance.                                                                    |
| Google Drive Sync | Connection state, pending queue, last successful sync, errors and retry action.                               |
| Users and Roles   | User administration and permission configuration.                                                             |
| Settings          | Business details, receipt, tax, printer, backup and Google Drive settings.                                    |

UX principles: prioritize speed at the POS; use large touch-friendly
controls where a touchscreen may be used; clearly distinguish unpaid,
pending and completed orders; confirm irreversible actions; and make
payment method and total visible before final confirmation.

# 13. Use Case Specifications

## UC-01: Complete a Sale

1.  Cashier signs in and opens the POS screen.

2.  Cashier selects Dine-in or Takeout.

3.  Cashier adds products and adjusts quantities.

4.  System calculates subtotal, configured discounts, applicable tax and
    total.

5.  Cashier selects Cash or GCash.

6.  For cash, cashier enters amount received and system calculates
    change. For GCash, cashier verifies receipt and records the
    reference when available.

7.  Cashier confirms payment. System commits order, items, payment and
    inventory movement atomically.

8.  System generates transaction number and receipt, then queues
    eligible records for Google Drive synchronization.

## UC-02: Adjust Finished-Product Stock

9.  Authorized user opens Inventory and selects a product.

10. User enters quantity change and mandatory reason.

11. System validates permission and quantity rules.

12. System saves the new quantity and an inventory movement record.

13. System records the action in the audit log.

## UC-03: Review and Export Sales

14. Authorized user opens Sales Reports.

15. User selects a date range and applicable filters.

16. System calculates totals from completed transactions and applies
    void/refund treatment consistently.

17. User previews the report and exports CSV or PDF.

18. Export is saved locally and added to the cloud sync queue when
    configured.

# 14. Sales and Transaction Processing

A checkout must be treated as a single logical operation. The
application should validate cart contents, product status, stock policy,
discount authorization, tax configuration and payment details before
committing. Use a database transaction to save the order, order items,
payment and inventory movements together.

Suggested order lifecycle: Draft → Awaiting Payment → Completed.
Additional terminal states may include Cancelled, Voided and Partially/
Fully Refunded, subject to the approved refund policy. A completed order
is immutable except through auditable correction records.

Transaction number format: MB-YYYYMMDD-000001. The sequence must be
generated safely so that duplicate numbers cannot be issued.

# 15. Google Drive Synchronization

## 15.1 Purpose and Design

Google Drive is an off-site synchronization and retention destination,
not the live POS database. The local SQLite database remains the source
of truth for day-to-day checkout. Synchronization must be asynchronous
and must never block a sale.

## 15.2 Data to Synchronize

- Completed transaction exports, preferably append-only CSV or
  structured JSON/CSV records.

- Daily sales summary after business-day closing.

- Monthly sales reports, generated on the first day of the following
  month or on demand.

- Scheduled SQLite database backups.

- Selected PDF reports and operational exports.

## 15.3 Suggested Schedule

| **Item**                      | **Suggested schedule**                                                       | **Notes**                                    |
|-------------------------------|------------------------------------------------------------------------------|----------------------------------------------|
| Completed transaction records | Queue immediately after local commit; upload when connectivity is available. | Near-real-time when online; queued offline.  |
| Daily summary                 | At business-day closing.                                                     | Closing time is an owner input.              |
| Monthly report                | First day of the following month.                                            | Can also be generated on demand.             |
| Database backup               | Daily.                                                                       | Keep local backup even after cloud upload.   |
| Retry processing              | Background retry with increasing delay.                                      | Avoid repeated rapid retries during outages. |

## 15.4 Synchronization Requirements

- Persist every upload task in a local SyncQueue before attempting
  upload.

- Track Pending, In Progress, Succeeded and Failed states, attempt
  count, timestamps and error details.

- Use stable identifiers and checksums to prevent duplicate files or
  duplicate transaction records.

- Retry transient failures with exponential backoff and a maximum retry
  interval; provide a manual retry option.

- Do not delete local transactions or backups after upload.

- Provide a status screen showing last successful sync, pending item
  count and actionable errors.

- Use OAuth 2.0 and request the minimum Google Drive scopes required.

- Protect locally stored OAuth credentials using Windows DPAPI.

- Use a dedicated business-owned Google account and restricted Drive
  folder access.

- Define retention, naming and folder structure with the business owner.

## 15.5 Suggested Drive Folder Structure

- Beachtro POS/

- Transactions/YYYY/MM/

- Daily Reports/YYYY/MM/

- Monthly Reports/YYYY/

- Database Backups/YYYY/MM/

- Exports/ (on-demand exports)

Suggested file naming examples: transactions_2026-11-16.csv,
daily_sales_2026-11-16.pdf, monthly_sales_2026-11.pdf,
pos_backup_2026-11-16_230000.db. Final naming and retention policy
require owner approval.

# 16. Receipt and Printing Management

The initial release requires customer receipt printing. An 80mm thermal
receipt printer is recommended, subject to confirming the actual printer
model, driver and paper width. The application should support
Windows-installed printers and provide a test-print function.

A receipt should include, where applicable:

- Business name, address and contact information.

- Transaction number and date/time.

- Cashier and order type.

- Product names, quantities, unit prices and line totals.

- Subtotal, discounts, tax details and grand total.

- Payment method, amount received, change and GCash reference if
  captured.

- Required tax/business identifiers and invoice wording, after
  accountant/BIR review.

Receipt layout and required legal fields must be finalized after the
business confirms its registration and tax status.

# 17. Reporting and Analytics

| **Report**         | **Suggested contents**                                                                                  |
|--------------------|---------------------------------------------------------------------------------------------------------|
| Daily Sales        | Gross sales, net sales, transaction count, average transaction value, discounts, tax and refunds/voids. |
| Payment Summary    | Cash and GCash totals, transaction counts and recorded GCash references.                                |
| Product Sales      | Quantity and sales value by product/category.                                                           |
| Inventory Status   | Current on-hand quantity and products at/below reorder level.                                           |
| Cashier Shift      | Opening cash, cash sales, cash movements, expected cash, counted cash and variance.                     |
| Monthly Sales      | Daily totals, monthly totals, payment split and comparison where prior data exists.                     |
| Transaction Detail | Filtered transaction list with item and payment detail.                                                 |

Report definitions must be consistent. The business must decide whether
dashboard sales figures are gross or net of discounts, refunds and tax,
and the application should label each figure clearly.

# 18. Security Requirements

- Require unique user accounts; avoid shared cashier credentials.

- Store password hashes using a modern, salted password-hashing
  algorithm.

- Enforce role-based authorization in application services, not only by
  hiding UI controls.

- Require authorization and a reason for voids, refunds, stock
  adjustments and sensitive settings changes.

- Record audit events for login failures, account changes, price
  changes, inventory adjustments, refunds, voids, tax changes and cloud
  configuration changes.

- Protect Google OAuth tokens and other secrets using Windows DPAPI.

- Use TLS for Google Drive API communication.

- Lock or sign out after an owner-approved period of inactivity.

- Restrict access to local database, backup and log files through
  Windows file permissions.

- Do not include passwords, OAuth tokens or sensitive payment
  credentials in logs.

# 19. Backup and Disaster Recovery

The application should create a daily local backup using a safe SQLite
backup method. A database file must not be copied while a write
operation is in progress unless a supported consistent backup mechanism
is used.

- Keep a configurable number of local backup generations.

- Upload backups to the designated Google Drive folder when connectivity
  is available.

- Record backup creation time, file size and checksum.

- Provide a documented restore procedure and a restore test before
  production.

- Keep local backups even if cloud upload succeeds.

- After restore, verify database integrity and confirm the most recent
  completed transaction.

- Define who may perform a restore and how new sales are handled during
  recovery.

Recommended initial target: daily backup with a minimum of 7 local
generations, subject to available disk space and owner approval. A more
formal recovery point objective (RPO) and recovery time objective (RTO)
should be agreed before go-live.

# 20. Philippine Business and Tax Considerations

Tax and receipt requirements are explicitly pending confirmation. The
software should be configurable to support the tax treatment approved
for the business, but this document does not assert that the proposed
POS is BIR-certified, accredited or compliant by default.

- Confirm whether the business is VAT-registered or non-VAT and the
  applicable tax treatment with the owner/accountant.

- Confirm whether the business must issue a BIR-compliant invoice and
  the exact required fields and terminology.

- Confirm registration details, TIN, registered business name/address
  and any required permit or accreditation details for receipts.

- Confirm treatment of discounts, senior citizen/PWD discounts and other
  statutory discounts if applicable.

- Confirm numbering, record retention, audit trail and report
  requirements with the accountant or relevant authority.

- Obtain professional review of current BIR rules and any
  accreditation/certification obligations before production use.

Tax configuration must be versioned with effective dates so historical
transactions retain the tax treatment applied at the time of sale.

# 21. Hardware and Software Requirements

| **Component**    | **Baseline / recommendation**                                                            |
|------------------|------------------------------------------------------------------------------------------|
| Operating system | Windows 11 64-bit, supported and updated.                                                |
| Terminal         | Business-grade desktop or laptop; SSD recommended.                                       |
| Memory           | 8 GB RAM minimum recommended; 16 GB preferred for longer useful life.                    |
| Storage          | SSD with sufficient free space for application, database, logs and retained backups.     |
| Printer          | Windows-compatible 80mm thermal receipt printer; exact model to be confirmed.            |
| Network          | Internet connection for Google Drive sync; local POS functions remain available offline. |
| Runtime          | Bundled or installed .NET desktop runtime matching the deployed application.             |
| Google account   | Business-controlled Google account with access to the designated Drive folder.           |

# 22. Implementation Roadmap

| **Phase**                      | **Activities**                                                                                                    | **Exit criteria**                                        |
|--------------------------------|-------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------|
| 1\. Discovery and confirmation | Confirm catalog, pricing, tax, receipt content, printer, users, opening stock and business-day closing procedure. | Owner-approved requirements and configuration decisions. |
| 2\. Detailed design            | Finalize workflows, wireframes, schema, permissions, tax model and Drive folder structure.                        | Design review approval.                                  |
| 3\. Core POS development       | Implement login, catalog, cart, order lifecycle, cash/GCash recording and receipts.                               | Core workflows pass functional tests.                    |
| 4\. Inventory and reporting    | Implement finished-product stock, movements, dashboard and reports.                                               | Inventory and reports reconciled against test cases.     |
| 5\. Cloud and resilience       | Implement Drive OAuth, queue, retry, exports, backups and restore process.                                        | Offline/online sync and restore tests pass.              |
| 6\. UAT and deployment         | Train users, test real hardware, seed catalog/stock, install and conduct go-live checks.                          | Signed UAT and deployment approval.                      |
| 7\. Stabilization              | Monitor errors, sync queue, printer behavior and user feedback.                                                   | Critical defects resolved and handover complete.         |

# 23. Testing and Quality Assurance

- Unit tests for totals, discounts, tax calculations, change, stock
  movements and transaction state transitions.

- Integration tests for SQLite transactions, repository behavior,
  printer adapter and Google Drive API.

- Functional tests for dine-in, takeout, cash, GCash and receipt
  printing.

- Permission tests for cashier, manager, owner and administrator roles.

- Offline tests: disconnect internet, complete sales, reconnect and
  verify queue processing.

- Duplicate/retry tests: simulate failed uploads and confirm idempotent
  recovery.

- Backup/restore tests using a copy of a representative database.

- Data integrity tests for interrupted checkout, application crash and
  database constraints.

- User acceptance testing using the actual product catalog, printer and
  business workflows.

- Regression testing after changes to checkout, tax, payment or
  inventory logic.

# 24. Acceptance Criteria

- An authorized cashier can sign in and create dine-in and takeout
  orders.

- The system correctly calculates item totals, order total and cash
  change for approved test cases.

- Cash and GCash payments can be recorded, with GCash manual
  verification captured.

- Completed transactions receive unique transaction numbers and appear
  in transaction history.

- Stock for finished products is adjusted correctly after completed
  sales and authorized stock adjustments.

- Voids/refunds and sensitive changes are permission-controlled and
  auditable.

- A receipt prints successfully on the approved printer with
  owner-approved content.

- Daily and date-range sales reports reconcile with the underlying
  completed transactions.

- The POS can complete sales without internet access.

- Transactions created offline are queued and synchronize to the
  approved Google Drive folder after connectivity returns.

- Failed uploads can be retried without creating duplicate transaction
  records or uncontrolled duplicate files.

- A local backup can be created and successfully restored using the
  documented procedure.

- Owner-approved tax settings and receipt requirements have been
  reviewed before production.

# 25. Risks and Mitigation

| **Risk**                      | **Impact**                                  | **Mitigation**                                                                       |
|-------------------------------|---------------------------------------------|--------------------------------------------------------------------------------------|
| Internet or Drive outage      | Cloud copies delayed.                       | Offline-first local database; durable queue; retries; local backups.                 |
| Power loss or crash           | Potential data loss or incomplete checkout. | Atomic DB transactions, UPS recommendation, recovery tests and backups.              |
| Incorrect tax configuration   | Incorrect totals or receipt treatment.      | Owner/accountant approval; effective-dated settings; test cases.                     |
| Printer incompatibility       | Receipts cannot be printed.                 | Confirm exact model early; use Windows driver; test before go-live.                  |
| Unauthorized refunds or edits | Financial discrepancies.                    | RBAC, approval rules, mandatory reason and audit log.                                |
| Local device failure          | POS downtime and local data loss.           | Daily local and Drive backups; documented restore and spare-device plan.             |
| Duplicate sync uploads        | Confusing cloud records.                    | Stable IDs, checksums, idempotency and sync history.                                 |
| Future multi-terminal demand  | Local SQLite architecture becomes limiting. | Plan migration to central API and PostgreSQL; do not share SQLite live over network. |

# 26. Assumptions and Dependencies

- The first release runs on one Windows 11 terminal.

- The business will provide the final product list, categories, prices
  and starting stock.

- The business will provide official business details and receipt
  branding.

- The business will provide or approve the Google account and Drive
  folder used for synchronization.

- The business will confirm the printer model and provide access for
  testing.

- The business owner/accountant will confirm tax registration,
  invoice/receipt obligations and discount rules.

- GCash transactions will be verified manually in the first release.

- The business will define operating hours, business-day closing time
  and cashier shift procedure.

- Reliable local storage and appropriate Windows account permissions
  will be available.

# 27. Future Enhancements

- Multi-terminal operation through a centralized API and PostgreSQL
  database.

- Kitchen display and preparation ticket workflow.

- Ingredient-level inventory, recipe management and food costing.

- Supplier, purchase order and receiving modules.

- Integrated GCash or other payment provider integration, subject to
  provider availability and approval.

- Customer loyalty, discounts and membership management.

- Online ordering and delivery integration.

- Advanced analytics, product performance and sales forecasting.

- Cloud-based owner dashboard and controlled remote access.

# 28. Project Deliverables

- Windows 11 POS application installer and deployed application.

- Application source code and build/deployment instructions.

- Database schema, migrations and sample/seed configuration.

- Product, inventory, order, payment and reporting modules.

- Receipt printing integration and approved receipt template.

- Google Drive OAuth integration, synchronization queue and status
  screen.

- CSV/PDF exports and scheduled database backup mechanism.

- User guide and administrator guide.

- Technical architecture and database documentation.

- Test plan, test cases, UAT results and known-issues list.

- Backup and restore procedure.

- Deployment and handover checklist.

# 29. Outstanding Business Decisions

| **Decision / input required**                           | **Owner / responsible party** | **Status**         |
|---------------------------------------------------------|-------------------------------|--------------------|
| Registered business name, address and contact details   | Business owner                | Pending            |
| Product catalog, categories, selling prices and SKUs    | Business owner / manager      | Pending            |
| Opening stock quantities and reorder levels             | Business owner / manager      | Pending            |
| Exact receipt printer make/model and paper width        | Business owner / developer    | Pending            |
| VAT or non-VAT registration and tax treatment           | Owner / accountant            | Pending – critical |
| Invoice/receipt wording and applicable BIR requirements | Owner / accountant            | Pending – critical |
| Discount policy and statutory discount handling         | Owner / accountant            | Pending            |
| Refund/void approval rules                              | Business owner                | Pending            |
| Opening cash and shift closing procedure                | Business owner / manager      | Pending            |
| Cashier and manager accounts                            | Business owner                | Pending            |
| Business-owned Google account and Drive folder          | Business owner                | Pending            |
| Business-day closing time and report schedule           | Business owner                | Pending            |
| Receipt logo/branding and footer text                   | Business owner                | Pending            |
| Backup retention and recovery targets                   | Business owner / developer    | Pending            |

# 30. Approval and Sign-Off

Approval of this document confirms that the scope, requirements,
assumptions and outstanding decisions are understood. Items marked
pending remain subject to confirmation and may affect implementation
scope, schedule or acceptance.

| **Approver**               | **Name / role** | **Signature** | **Date** |
|----------------------------|-----------------|---------------|----------|
| Business Owner             | Ma'an           |               |          |
| Business Owner             | Miggy           |               |          |
| Developer / Technical Lead |                 |               |          |
| Business Representative    |                 |               |          |

# Appendix A – Suggested Initial Configuration

| **Setting**            | **Suggested starting value**                                                  | **Confirmation**      |
|------------------------|-------------------------------------------------------------------------------|-----------------------|
| Currency               | PHP (Philippine Peso)                                                         | Confirm               |
| Time zone              | Asia/Manila                                                                   | Confirm               |
| Order types            | Dine-in; Takeout                                                              | Confirmed             |
| Payment methods        | Cash; GCash                                                                   | Confirmed             |
| Initial terminal count | 1                                                                             | Confirmed             |
| Receipt paper          | 80mm thermal (recommended)                                                    | Printer model pending |
| Drive sync interval    | Queue immediately; background upload when online; status refresh periodically | Confirm               |
| Daily backup           | Once per day                                                                  | Confirm retention     |
| Tax mode               | Not activated until owner/accountant confirms                                 | Pending               |

# Appendix B – Key Technical Decisions

- Use SQLite only for the initial single-terminal deployment.

- Do not use a shared network folder as a concurrent SQLite database.

- Use a repository/service boundary so the data layer can later move to
  a central API.

- Use database transactions for checkout and inventory updates.

- Use decimal types for money and explicit rounding rules.

- Keep cloud synchronization asynchronous and durable through a local
  queue.

- Use stable record identifiers and checksums for idempotent uploads.

- Keep local records and backups after successful cloud upload.
