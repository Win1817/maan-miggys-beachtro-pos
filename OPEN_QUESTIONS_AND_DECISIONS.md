# Open questions and decisions

Date: November 16, 2026. Source: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md), especially sections 26, 29–30. Status: all decisions below are **Pending**. Proposed responsible parties have not accepted assignments. Recommendations elsewhere are not recorded approvals.

Gate labels: **I** = resolve affected semantics/design before implementation approval; **F** = final input required before dependent feature configuration/UAT; **P** = production acceptance evidence. Some decisions span gates. Unresolved I decisions require explicit scope deferral or design containment approved by the product owner, not assumed defaults.

| ID / gate | Question requiring an explicit answer | Proposed authority | Required decision artifact | BRD / linked gaps |
|---|---|---|---|---|
| D-01 I/P | VAT or non-VAT? Which tax components, effective dates, exemption/zero-rating cases and statutory discounts apply? Which electronic-invoice, POS registration/accreditation and reporting obligations apply at intended go-live? | Owner and accountant; relevant authority as needed | Signed applicability memo, registration evidence and worked tax cases; review current issuances | Sections 4, 20, 29; FR-011,029; G-01–02 |
| D-02 I/F/P | What registered name/address/TIN/contact/permit details and legal invoice wording/fields are required? What logo/footer and paper layout are approved? Is internal sale number distinct from legal invoice series? | Owner/accountant; owner for branding | Approved specimen and field/numbering checklist | Sections 16,20,26,29; FR-016; G-01,15 |
| D-03 I | Currency/scale; tax-inclusive prices; per-line versus per-order rounding; discount order, stacking, caps, approvals and allocation to refunds? | Owner/accountant | Calculation rules and expected totals for boundary examples | Sections 8,14,20; Appendix A–B; FR-006,011; G-02 |
| D-04 I | What distinguishes cancel, post-sale void and refund? Partial item/quantity refunds? Limits, approvers/self-approval, allowed tender, external GCash refund confirmation, damaged-return stock and correction date? | Owner/manager with accountant | Signed correction workflow and financial/stock fixtures | Sections 8,14,17–18,29; FR-018; G-03,05–06 |
| D-05 I | Define gross/net/tax/discount/refund totals, averages, product/payment summaries, correction dates and report revision after close. Which reports export as CSV/PDF? | Owner/accountant | Report dictionary plus reconciliation examples | Sections 13,15,17,24; FR-021–023; G-05 |
| D-06 I/F | Confirm business timezone/currency and hours; midnight or other closing boundary? Business day versus calendar date, late closure, backdating, clock correction and monthly catch-up? | Owner/manager | Calendar/closing policy and schedule examples | Sections 8,15,17,26,29; Appendix A; FR-021–022,029; G-05,21 |
| D-07 I | Cash under/over-tender rules and zero-total orders? Single tender only or explicitly approved split payments? Who verifies GCash and how when connectivity is absent? Reference optional as BRD permits? Recovery when money received but sale outcome uncertain? | Owner/manager | Payment workflow, exceptions and verification evidence fields | Sections 5.2,8,13–14; FR-012–015; G-06 |
| D-08 I/F | Include shifts and cash movements in initial release? Required before selling? Opening/counting, cash-in/out approval, overlap, cross-midnight, refunds and abandoned-shift closure? | Owner/manager | Explicit FR-019/020 scope choice; signed expected-cash formula and procedure | Sections 5,11,17,29; FR-019–020; G-07 |
| D-09 I/F | Product list, categories/prices/SKUs, starting quantities/reorder levels; whole or fractional units; tracked/untracked policy, negative override authority, zero prices and cart repricing? | Owner/manager | Approved catalog template, initial stock reconciliation and inventory policy | Sections 7–8,11,26,29; FR-004–008,010; G-10 |
| D-10 I | Exact money/quantity/UTC mapping, snapshot fields, constraints, refund relations and aggregate strategy for chosen EF/SQLite versions? | Technical lead; accountant validates money outputs | Reviewed logical design and future provider-verification plan | Sections 8,10–11,20; Appendix B; FR-011–012,018; G-02–06 |
| D-11 I/F | Immutable per-sale exports or versioned batches? JSON/CSV contract, logical keys, artifact versioning, remote IDs, folder naming, retry rules and manual retry authority? | Technical lead and owner for retention/usability | Sync state machine and artifact contract, including ambiguous remote success | Sections 13–15; FR-023,026–027; G-04,08–09 |
| D-12 I/P | Confirm transaction format/date basis, sequence scope and exhaustion. How avoid reuse after old-backup restore? Who reconciles latest local/cloud/printed sales and approves resumption? | Owner/accountant and technical lead | Numbering and recovery protocol; failure test scenarios | Sections 8,14,19; FR-012,017,028; G-04,12 |
| D-13 I/F/P | RPO/RTO, retention counts/age, independent local media, Drive retention, encryption and recoverable keys? Who restores; halt or manual sales during recovery; how reconcile? App-closed/missed schedules and disk-full procedure? | Owner and technical lead | Recovery policy/runbook, measured replacement-device drill and retention rules | Sections 15,18–19,29; FR-025,028; G-09,11–12,16 |
| D-14 I/F | Capability matrix/multiple roles; cashier/manager accounts; financial rights for technical admin; bootstrap/reset authority, password/lockout/timeout, session handoff and revoke behavior? Windows user per operator or shared OS account? | Owner; technical lead for security design | Approved access matrix and account/session procedures | Sections 6,18,26,29; FR-001–003,018,024,029; G-13,16,21 |
| D-15 I/F/P | Approved hardware/workload, catalog/history/cart limits, latency and availability targets, outage duration, touch/keyboard paths and accessibility? | Owner/manager and technical lead | Measurable NFR profile and operator walkthroughs | Sections 9,12,21,23–24; FR-010,012,017,022,025; G-14,19,21 |
| D-16 F/P | Exact printer/driver/interface/paper width? Reprint permissions/duplicate marker, paper-out/offline/cutter issues and fallback procedure? | Owner with technical lead | Hardware approval and witnessed physical print matrix | Sections 16,21,26,29; FR-016; G-15 |
| D-17 I/F | Business-owned account, cloud project and OAuth client owners, Drive folder/access scope, quota plan, consent deployment, reconnect/revocation and off-site data access? | Owner; technical lead for integration | Cloud ownership/access design and authorization checklist; no account connection in this assignment | Sections 15,18,21,26,29; FR-026–027,029; G-16–17 |
| D-18 F/P | Installer type/runtime deployment, signing responsibility, update channel, schema compatibility/rollback, diagnostics, support owner and third-party licenses? | Owner/technical lead | Packaging/update decision, dependency/license inventory and support handover | Sections 10,21,28; FR-025,029; G-18 |
| D-19 I/P | Who approves design, financial fixtures and UAT? Which defect severities block release? Who signs a deferred Should feature or any baseline deviation? | Owners/appointed representative | Signed analysis disposition, acceptance plan, deviation register and later separate implementation authorization | Sections 22–24,28,30; all FRs; G-19 |
| D-20 I | Is readiness accepted through stable identities and service boundaries now, with central API/server/offline conflict policy explicitly future scope? | Owner/technical lead | Architecture disposition and future-phase boundary | Sections 4,5.2,10.2,27; FR-030; G-20 |

## Coverage of the baseline's 14 outstanding inputs

| Section 29 input | Decision |
|---|---|
| Registered business details | D-02 |
| Catalog, categories, prices, SKUs | D-09 |
| Opening stock and reorder levels | D-09 |
| Printer make/model and width | D-16 |
| VAT/non-VAT and tax | D-01 |
| Invoice wording and BIR requirements | D-01, D-02 |
| Discounts/statutory discounts | D-01, D-03 |
| Refund/void approval | D-04 |
| Opening cash/shift close | D-08 |
| Cashier/manager accounts | D-14 |
| Google account/folder | D-17 |
| Business-day closing/report schedule | D-06 |
| Logo/branding/footer | D-02 |
| Backup retention/recovery targets | D-13 |

## Decision recording and change control

For each resolution, record date, approver, selected answer, alternatives rejected, rationale, affected FRs, fixture/evidence links and remaining caveats. No dates or approvals are invented here. Keep pending entries open until actual evidence exists. Product-owner approval is needed for scope/behavior changes; technical-lead approval addresses technical choices without overriding business policy. Accountant review supplies tax/invoice evidence rather than software certification.

The complete BRD has blank sign-off cells. Its designation as authoritative baseline preserves traceability; it does not resolve its pending items. The owner should disposition this package, approve decision records or explicit deferrals, then separately authorize engineering implementation. No permission for development, integration or configuration is inferred from receipt of this analysis.
