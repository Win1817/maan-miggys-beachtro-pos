# Risk and dependency register

Date: November 16, 2026. Source: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md), sections 15–26 and 29. Status: all risks/dependencies **Open**; owners below are proposed accountable roles. No mitigation is claimed implemented. G/D references resolve to the companion gap/decision registers.

Likelihood: L/M/H = low/medium/high qualitative planning estimates, not measured probabilities. Impact: C/H/M = critical/high/medium. Priority: P0 = resolve before dependent financial/recovery design is implemented; P1 = release-critical; P2 = planned design/handover control. Reassess after decisions and test evidence.

| ID / likelihood / impact / priority | Risk and trigger | Consequence | Proposed treatment / accountable role | Closure evidence and trace |
|---|---|---|---|---|
| R-01 M/C/P0 | Unapproved or inapplicable tax/invoice treatment; go-live without accountant review | Incorrect invoices/totals or legally unusable deployment | Obtain current-rule applicability memo and worked cases; owner/accountant | D-01–03, T-04/08/17; G-01–02; sections 20,24,29; FR-011,016,029 |
| R-02 M/C/P0 | Decimal mapping/rounding drift or changed historic configuration | Ledger, receipt and report disagreement | Exact storage, approved rounding and immutable snapshots; technical lead/accountant | D-03,10; T-04/12; sections 8,11,20; FR-011 |
| R-03 M/C/P0 | Power loss or repeated confirm at commit boundary | Lost/duplicate sale, payment or stock deduction | Atomic local write, stable request ID and crash recovery; technical lead | T-07; G-04; sections 8,14,25; FR-012,026 |
| R-04 M/C/P0 | Unspecified refund value/date/stock policy | Over-refund, altered history, false reports | Linked correction ledger and approved authority/limits; owner/accountant | D-04–05; T-10/12; G-03,05; FR-018,022 |
| R-05 H/H/P1 | Internet/Drive outage, quota exhaustion or revoked token | Off-site lag, growing backlog and eventual disk pressure | Durable local queue, bounded retry, visible age/errors and local backup; technical lead/owner | T-13–15, capacity policy; sections 15,25; FR-025–027 |
| R-06 M/H/P0 | Upload succeeds but acknowledgment lost; worker crashes | Duplicate or missing cloud artifact | Persist remote identity, reconcile same artifact and reclaim stale lease; technical lead | D-11, T-14/15; G-08–09; section 15; FR-026–027 |
| R-07 M/C/P0 | Restore older DB rewinds numbers/queue and loses payload files | Reused invoice number, conflicting ledger or repeated uploads | Recovery maintenance mode, high-water reconciliation, recoverable artifacts; technical lead with owner approval | D-12–13, T-16; G-12; sections 8,14–15,19 |
| R-08 M/C/P1 | Disk/device failure or same-device-only copies | Downtime and loss since last usable independent backup | Safe backup, independent off-site copy, spare-device recovery plan; owner/technical lead | Measured T-16, agreed RPO/RTO; sections 19,25; FR-028 |
| R-09 M/H/P1 | App closed at schedule, missing permissions or disk full | Daily backup/report silently omitted | Catch-up, visible job state, space alerts and approved scheduling procedure; technical lead/manager | D-13, T-16/18; G-09,11; FR-028 |
| R-10 M/H/P1 | Printer driver/paper/font incompatible or paper-out | Receipt cannot be issued or cashier repeats sale | Approve actual model early; separate print status/reprint; owner/technical lead | D-16, T-08; sections 16,21,25; FR-016 |
| R-11 M/H/P0 | UI-only authorization, shared accounts or permissive admin | Unauthorized refunds/stock edits, weak attribution | Service capability enforcement, unique POS users and approved reset/role matrix; owner/technical lead | D-14, T-01/02/10; sections 6,18,25; FR-001–003,018,024 |
| R-12 M/H/P1 | Stolen DB/backups/logs or broad Drive sharing | Business/user data exposure; credentials compromised | ACLs, redaction, minimum scope, data minimization and recovery-compatible encryption decision; owner/technical lead | D-13–14,17; access/redaction review; G-16–17; sections 15,18–19 |
| R-13 M/H/P0 | GCash received externally but local outcome uncertain; no verification connectivity | Double recording/charge or unverified sale | Manual verifier evidence, history lookup and approved exception workflow; manager | D-07, T-06/07; G-06; sections 8,13; FR-014–015 |
| R-14 M/H/P1 | Undefined midnight/close/correction date or clock changes | Daily totals and sequence/date inconsistent | Approved timezone/business-day and close policy; owner/accountant | D-05–06,12; T-09/12; sections 8,15,17 |
| R-15 M/H/P1 | Growing catalog/history/backlog without sizing; long DB locks | Slow checkout or failed local writes | Agreed workload/headroom, short transactions, representative test; technical lead | D-15, T-18; G-14; sections 9,21 |
| R-16 M/H/P1 | Failed app/schema update or unsupported dependency | App cannot start or data inaccessible | Version pinning, pre-update backup, tested upgrade/restore and support ownership; technical lead | D-18, T-16/18/19; G-18; sections 10,21,28 |
| R-17 M/H/P2 | Later terminals share SQLite file or assume Drive replication | Concurrent corruption/conflicts and stock divergence | Enforce local DB boundary; later central API/server and explicit offline design; owner/technical lead | D-20, T-19; sections 10.2,25,27; FR-030 |
| R-18 H/H/P1 | Pending inputs or ambiguous acceptance discovered late | Rework, blocked UAT, schedule overrun | Decision gates, traceable fixtures, explicit deferrals and owner review; owner/technical lead | D-19 and closure log; sections 22–24,26,29–30 |

Residual exposure remains after proposed controls: manual GCash verification can be mistaken; local administrators can alter files; outage duration can exceed the last uploaded backup; physical printing cannot be made atomic with a sale. Owner-approved procedures and measured evidence are needed to accept these limits, not promises of zero risk.

## Dependency register

| ID | Dependency / proposed supplier | Needed by / consequence of absence | Acceptance or resolution artifact | Trace |
|---|---|---|---|---|
| DEP-01 | Tax status, registration, current-rule applicability / owner-accountant | Financial/invoice design; cannot finalize lawful production behavior | D-01–03 signed memo, approved calculations and invoice fields | Sections 20,26,29; R-01 |
| DEP-02 | Product/category/price/SKU list and stock/reorder units / owner-manager | Catalog fixtures, seeding and inventory UAT; no representative reconciliation otherwise | D-09 validated input and opening-stock sign-off | Sections 26,29; FR-004–008 |
| DEP-03 | Actual Windows terminal and printer/driver/paper / owner | Hardware/performance/receipt UAT; simulator output insufficient | D-15–16 device profile and physical test access | Sections 16,21,26; R-10,15 |
| DEP-04 | Business Google account, cloud project/client, designated folder and API authorization / owner | Later integration and recovery; no business cloud setup authorized now | D-17 ownership, scope/consent and reconnect plan | Sections 15,21,26,29; R-05,12 |
| DEP-05 | Users/capabilities, refund/discount and shift policies / owner-manager-accountant | Service authorization and correction/shift semantics | D-03–04,07–08,14 signed workflows/matrix | Sections 6,8,18,29; R-04,11,13 |
| DEP-06 | Hours, timezone, business-day close and report definitions / owner-accountant | Number/date, scheduling and report reconciliation | D-05–06,12 calendar examples and formulas | Sections 15,17,26,29; R-14 |
| DEP-07 | Recovery authority, media/storage, retention, RPO/RTO and spare-device access / owner-technical lead | Backup/restore design and release drill | D-12–13 runbook and target approval | Sections 19,25,29; R-07–09 |
| DEP-08 | Supported compatible .NET/EF/SQLite/Google client/printing/PDF/logging packages and licenses / technical lead | Build/update design after authorization | D-10,18 version and license inventory, reproducible build plan | Sections 10,21,28; R-02,16 |
| DEP-09 | Windows file permissions, deployment/signing/update channel and support owner / owner-technical lead | Secure install/upgrade and ongoing recovery | D-14,18 deployment/support checklist | Sections 18,21,28; R-12,16 |
| DEP-10 | Approved fixtures, UAT operators and decision authority / owners-appointed representative | Acceptance and separate implementation/go-live approval | D-19 disposition records and future signed UAT | Sections 22–24,30; R-18 |

Each risk review should record actual owner, review date, mitigation status, evidence, residual severity and accepted-by/date. No arbitrary due dates or completion percentages are assigned. Unavailable external input blocks the dependent activity, not all independent documentation work.
