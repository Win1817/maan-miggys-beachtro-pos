# Requirements traceability matrix

Date: November 16, 2026. Governing source: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md). Status: analysis only; **all implementation and test evidence is Not started**. Test IDs are proposed verification scenarios, not completed tests or new baseline requirements. G and D references link conceptually to [gaps](REQUIREMENTS_GAP_ANALYSIS.md) and [decisions](OPEN_QUESTIONS_AND_DECISIONS.md).

## Functional coverage

All 30 section-7 FRs are represented once below; priorities are unchanged. Related source sections clarify behavior. 'Conditional' means policy/design detail needs resolution, not that the baseline obligation is optional.

| FR / original priority | Obligation and other BRD references | Module | Readiness / finding | Proposed evidence |
|---|---|---|---|---|
| FR-001 Must | Sign in before protected access; sections 6,12,18 | Identity | Conditional; G-13 | T-01 authorized/invalid/inactive login; offline login; no protected pre-login access |
| FR-002 Must | Create/edit/deactivate/reset accounts; sections 6,11,18 | Identity | Conditional; D-14 | T-01 lifecycle, uniqueness, reset authority, last-owner protection and active-session revocation |
| FR-003 Must | Permissions across protected functions; sections 6,18 | Authorization | Conditional; G-13 | T-02 capability matrix including direct service calls and technical-admin restrictions |
| FR-004 Must | Product create/edit/activate/deactivate; sections 11–12 | Catalog | Catalog input pending; G-10 | T-03 invalid fields, deactivate in open cart, historical sale unaffected |
| FR-005 Must | Organize products in categories; sections 11–12 | Catalog | Detailed policy pending | T-03 create/edit/deactivate grouping without historical loss |
| FR-006 Must | Selling price/currency; sections 8,11; Appendix A | Catalog/money | Conditional; G-02,10 | T-04 exact prices/scale/bounds; currency and price-history examples |
| FR-007 Must | Stock-tracked on-hand quantity; sections 8,11 | Inventory | Conditional; D-09 | T-05 opening movements, tracked/untracked products, balance-to-ledger reconciliation |
| FR-008 Must | Reasoned audited stock changes; sections 8,13 UC-02,18 | Inventory | Conditional; G-10 | T-05 permission/reason, atomic movement/balance/audit, negative override |
| FR-009 Must | Identify dine-in/takeout; sections 4,13 UC-01 | Sales | Types confirmed | T-06 both types persist to history, reports and receipt |
| FR-010 Must | Add/change/remove cart items before pay; sections 12–14 | Sales | Bounds/draft handling pending; G-10,21 | T-06 quantities, removal, invalid/deactivated item, abandoned cart and lock |
| FR-011 Must | Subtotal/discount/tax/total; sections 8,14,20 | Money | Policy blocked; G-01–02 | T-04 signed rounding/discount/tax fixtures and effective-date snapshot checks |
| FR-012 Must | Finalize after payment requirements; sections 8,13–14 | Checkout | Critical refinements; G-04,06 | T-07 crash/rollback/duplicate request; unique number and one atomic local result |
| FR-013 Must | Cash tender/change; sections 11,13 | Payment | Semantics pending; G-06 | T-06 insufficient/exact/excess tender, applied amount versus tender and change |
| FR-014 Must | GCash method and optional reference/note; sections 5.2,13 | Payment | Recovery pending; D-07 | T-06 optional reference, persisted method/note and no gateway assumption |
| FR-015 Must | Confirm manual GCash receipt; sections 8,13 | Payment | Procedure pending; G-06 | T-06 reject unverified completion, record verifier/time; unavailable-verification and crash cases |
| FR-016 Must | Generate/print completed-sale receipt; sections 16,20–21 | Receipts | Hardware/legal details blocked; G-01,15 | T-08 physical approved sample, paper-out/retry/reprint without new sale |
| FR-017 Must | Search/view completed transactions; sections 11–12 | History | Filters/time limits pending | T-09 transaction/date/status/cashier lookup, permission and archived product snapshots |
| FR-018 Must | Traceable reasoned void/refund; sections 8,14,18 | Corrections | Policy blocked; G-03 | T-10 unauthorized/cumulative/partial/full correction, tender and return-stock reconciliation |
| FR-019 Should | Open/close/count shift; sections 5,11,17,29 | Shifts | Explicit initial-scope decision D-08 | T-11 opening/close/variance/cross-midnight and abandoned-shift procedure if retained |
| FR-020 Should | Cash in/out with reason/authorization; sections 11,17 | Cash | Explicit initial-scope decision D-08 | T-11 positive/invalid movements, permissions and expected cash including refunds |
| FR-021 Must | Daily indicators; sections 12,17 | Reports | Definitions pending; G-05 | T-12 same ledger totals as detail/payment report; clear gross/net labels |
| FR-022 Must | Daily/monthly/date-range reports; sections 13,17 | Reports | Definitions pending; G-05 | T-12 all periods, correction dates, business-day boundary and reconciliation |
| FR-023 Must | Applicable CSV/PDF exports; sections 13,15,17 | Exports | Scope/format pending; D-05,11 | T-12 exported totals/encoding/escaping/PDF layout, saved artifacts and sync intent |
| FR-024 Must | Significant financial/admin audit; sections 8,18 | Audit | Event policy pending; G-13,16 | T-02 event completeness, safe before/after, mandatory reason and secret redaction |
| FR-025 Must | Offline core operations; sections 1,8–10 | All core modules | Confirmed; local fault boundaries needed | T-13 disconnected initialized terminal sells/prints/reports without cloud dependency |
| FR-026 Must | Persist unavailable-cloud work; sections 11,15 | Sync | Critical refinements; G-04,08–09 | T-14 intent survives crash/offline/restart; missing payload recoverable; no lost sale export |
| FR-027 Must | Pending/success/failure status; sections 12,15.4 | Sync UI | Status/retry semantics pending | T-15 visible counts/time/error/manual retry; transient/permanent faults and no duplicate upload |
| FR-028 Must | Scheduled local backup; sections 15,19 | Backup | Recovery policy pending; G-11–12 | T-16 due/missed schedule, live consistent backup, disk-full and replacement-device restore |
| FR-029 Must | Authorized business/receipt/tax/sync settings; sections 18,20 | Settings | Decisions D-01,02,14,17 | T-17 permission checks, effective dates, audit and history unaffected by edits |
| FR-030 Should | Preserve later central-backend migration; sections 4,10.2,27; Appendix B | Architecture | Future readiness confirmed; D-20 | T-19 dependency review, stable IDs and documented cutover limitations; no current multi-terminal claim |

## Business rules (section 8)

Local labels below identify bullets in source order; they do not rename the baseline.

| Source bullet | Rule | Related FR / proposed evidence |
|---|---|---|
| 1 | Unique transaction number; format suggested | FR-012,017; T-07,16; resolve section-14 format/date and restore ambiguity in D-12 |
| 2 | Payment requirements before completed state | FR-012–015; T-06,07 |
| 3 | Stock deducted only on successful completed sale | FR-007,012; T-05,07 |
| 4 | Prevent negative stock by default; logged authorized override | FR-003,007–008,024; T-02,05 |
| 5 | Preserve completed original; correct through traceable workflow | FR-018,024; T-10 |
| 6 | GCash method is not proof of settlement | FR-014–015; T-06 |
| 7 | Decimal-safe money, no binary float | FR-006,011,013; T-04; data-mapping review D-10 |
| 8 | Consistent stored timestamps/business local display | FR-017,021–022,029; T-09,12,17 |
| 9 | Local records remain after sync | FR-026; T-14,15 |
| 10 | Cloud failure never prevents completed sale | FR-012,025–026; T-07,13–15 |
| 11 | Approved discount/tax/refund authority | FR-003,011,018,029; T-02,04,10,17 |
| 12 | Movement includes product/delta/reason/user/time | FR-007–008,024; T-05 |

## Non-functional coverage (section 9)

| Category | Related FRs | Proposed acceptance refinement / evidence | Pending |
|---|---|---|---|
| Usability | FR-009–016 | T-18 timed representative touch/keyboard sale, readable total/method and actionable errors | D-15: paths, interaction limits, accessibility |
| Performance | FR-010,012,017,022 | T-18 p95 lookup/cart/local commit/report duration measured on agreed dataset/hardware, including worker load | D-15: numeric budgets; no latency promise inferred |
| Availability | FR-025 | T-13 no network or Drive authorization dependency for core initialized operation | D-07,13,15: GCash exception, local faults, outage duration |
| Reliability | FR-012,018,026 | T-07 interruption atomicity and repeat request; T-14 durable intent | D-10–12 |
| Data integrity | FR-007–008,011–018 | T-04,05,07,10 constraints and line/order/payment/stock reconciliation | D-03–05,09–10 |
| Security | FR-001–003,024,029 | T-01,02,17 direct-service denial, reset/lock, ACL/redaction/token design review | D-14,17 |
| Maintainability | All | T-19 module dependency/build/configuration/migration guide review and appropriate automated tests | D-18,19 |
| Recoverability | FR-028 | T-16 restore drill including latest sale, numbering, payloads, tokens and measured downtime/data loss | D-12–13: targets |
| Compatibility | FR-016,025 | T-08,18 installation and physical printer checks on supported Windows 11 x64 target | D-15–16,18 |
| Scalability | FR-030 | T-19 reviewed service boundary and future API migration notes, no shared SQLite | D-20 |
| Localization | FR-006,011,016,022,029 | T-04,08,12 currency/rounding/date/time/printing including business-day edges | D-03,06 |

Numeric NFR values must be agreed before tests can establish acceptance. No unsupported default is promoted to a baseline requirement.

## Section-24 acceptance bullets

`AC-01`–`AC-13` are local labels assigned in the original bullet order. They are not identifiers present in the BRD.

| Local label | Baseline acceptance | FR coverage / proposed tests |
|---|---|---|
| AC-01 | Authorized sign-in, dine-in/takeout | FR-001,003,009–010; T-01,02,06 |
| AC-02 | Approved totals/change examples | FR-011,013; T-04,06 |
| AC-03 | Cash/GCash with manual verification | FR-012–015; T-06,07 |
| AC-04 | Unique number and history | FR-012,017; T-07,09,16 |
| AC-05 | Correct sale/authorized-adjustment stock | FR-007–008,012; T-05,07 |
| AC-06 | Authorized auditable corrections/settings | FR-003,018,024,029; T-02,10,17 |
| AC-07 | Approved content on actual approved printer | FR-016; T-08 |
| AC-08 | Reconciled daily/date-range reports | FR-021–022; T-12 |
| AC-09 | Sales without internet | FR-025; T-13 |
| AC-10 | Offline transactions later reach approved Drive folder | FR-026; T-14 |
| AC-11 | Retry without duplicate records/uncontrolled files | FR-026–027; T-14,15 |
| AC-12 | Local backup creation and documented restore | FR-028; T-16 |
| AC-13 | Tax/receipt review before production | FR-011,016,029; T-04,08,17 and D-01–02 approval artifacts |

## Proposed test/evidence catalog

| ID | Level / principal evidence | Review authority proposed |
|---|---|---|
| T-01 | Identity unit/integration and offline account lifecycle scenarios | Technical lead; owner approves reset policy |
| T-02 | Service authorization negative tests, audit-event matrix and redaction review | Technical lead; owner approves capabilities |
| T-03 | Catalog/category persistence and historical snapshot scenarios | Manager and technical lead |
| T-04 | Exact money/rate round trips, rounding and effective-tax golden cases | Accountant/owner approves outputs; technical lead verifies storage |
| T-05 | Inventory movement/balance reconciliation, permissions and rollback | Manager and technical lead |
| T-06 | Operator dine-in/takeout cash/GCash happy/negative paths | Cashier/manager and owner |
| T-07 | SQLite crash-point and duplicate-request fault injection; number/stock/payment/outbox invariants | Technical lead |
| T-08 | Actual printer signed receipt samples, spool/physical failure and controlled reprint | Owner/accountant for content; operator for hardware |
| T-09 | Search/filter/time-boundary/history snapshot integration | Manager and technical lead |
| T-10 | Same/next-day partial/full refunds, cumulative limits, tender and stock disposition | Owner/accountant and technical lead |
| T-11 | Shift/cash reconciliation fixtures, only if initial scope retained | Owner/manager |
| T-12 | Daily/monthly/range/payment/product reconciliation and CSV/PDF comparisons | Owner/accountant |
| T-13 | Network removal, application restart, local availability and disk-space exception scenarios | Technical lead and operator |
| T-14 | Durable queue/payload/restart/offline-backlog and local-record-retention integration | Technical lead |
| T-15 | Drive ambiguous success, timeout, duplicate identity, quota/revocation, lease and manual retry integration | Technical lead; owner reviews error UX |
| T-16 | Live backup, corruption detection, old-backup/replacement-device restore, numbering and queue reconciliation, measured RPO/RTO | Owner-authorized recovery operator and technical lead |
| T-17 | Authorized/versioned settings and immutable historical tax/receipt data review | Owner/accountant and technical lead |
| T-18 | Supported device install/update, representative performance/usability and real printer UAT | Owner/operators and technical lead |
| T-19 | Architecture dependency, future migration, source/build/license/guide/handover review | Technical lead and owner |

Section 23 supplies test categories, not completed evidence. The roadmap adds proposed release gates. Monthly reporting, exports, account management, sync status and measurable NFRs need explicit acceptance coverage beyond the section-24 bullets (G-19). Final evidence records should include approved inputs, environment/versions, expected and actual results, defects and approver; none exists yet.
