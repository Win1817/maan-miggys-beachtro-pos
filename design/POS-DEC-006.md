# POS-DEC-006 — Authorize milestone 7: shifts, dashboard, reports and exports (IB-16 to IB-18)

Date: November 16, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-005](POS-DEC-005.md).

## Decision

1. **Milestone 7 authorized**:

   | Backlog item | Scope |
   |---|---|
   | IB-16 | Shifts and cash movements under accepted bundle B-03: open, live expected cash, cash in/out with reason, counted close with variance and an immutable close snapshot, manager closure of an abandoned shift with reason. Optional "require an open shift to sell" setting, **off by default** (D-08 procedure still pending). Sales and refunds already link to the open shift. |
   | IB-17 | One ledger query for the reporting dictionary (finance design §Reporting dictionary); daily dashboard; the seven reports (Daily Sales, Payment Summary, Product Sales, Inventory Status, Cashier Shift, Monthly Sales, Transaction Detail) stored as immutable `ReportRuns` with a ledger watermark and revisions; business-day close metadata. |
   | IB-18 | CSV and PDF rendering from the stored report snapshot only: invariant amounts, UTF-8, CSV formula-injection protection, deterministic bytes, TEST marker; local export to a user-chosen file and an artifact intent per export. |

2. **Implementation choices**:
   - **Shift close snapshot:** stored in two new nullable columns, `CashierShifts.CloseSnapshotJson` and `CloseSnapshotSha256`. They are added with `ADD COLUMN`, which avoids a table rebuild that would drop the guard triggers. The existing "closed shift is immutable" trigger already protects them once a shift is closed.
   - **Business-day close:** recorded as a Daily Sales `ReportRun` whose filters are `{"purpose":"close"}`. The table is already append-only and carries the watermark and revision, so a re-close after later corrections becomes the next revision. Closing never locks or changes orders.
   - **Report revisions:** generating a report for the same type, range and filters reuses the latest run when the content and watermark are unchanged. Otherwise it records the next revision.
   - **PDF writer:** a small built-in PDF writer using the standard Courier and Helvetica fonts. This adds no dependency or download, and the output has no timestamps, so the same run always gives the same bytes. Non-Latin characters print as `?`.
   - **Times on shift and transaction reports** are shown in the calendar profile's time zone (Asia/Manila), not the PC's.

3. **Deferred:**
   - Artifact materialization, the monthly catch-up scheduler and Drive upload (IB-19 to IB-23).
   - Report filters beyond type and date range.
   - Historical (as-of past date) inventory.

4. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer, Google account or tax data; production activation disabled.

Work stops after milestone 7 for a report.
