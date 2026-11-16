# Test and acceptance plan

Version 1.0 · November 16, 2026 · POS-DEC-001. **Plan only: no application tests have run.** Surely Win Dilag writes/executes automated tests, reviews results and accepts internally as sole engineer/owner. No separate QA or technical-lead sign-off. External business/accountant validation is attached only where applicable. Sources: [BRD](../Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) §§8–9,13–24; approved [traceability matrix](../REQUIREMENTS_TRACEABILITY_MATRIX.md). Existing T-01–19 and local AC-01–13 identifiers are retained.

## Test architecture and environments

Unit layer: pure money/quantity/state/authorization rules with fixed clock, fixed IDs and profile inputs; avoid UI/EF test doubles that merely repeat implementation. Property-based or generated fixtures verify arithmetic and cumulative-refund invariants over bounded valid domains. Initial .NET test runner recommendation xUnit with generated loops or maintained property library if justified; version/license pinned in authorized foundation task.

Integration layer: real on-disk SQLite using exact provider/version, real migrations/guards/constraints, connections and WAL settings; never substitute EF InMemory for transaction/money integrity. Separate test DB per scenario, synthetic data only. Crash tests spawn a disposable test process against disposable DB and kill at deterministic hooks before/after durable steps. Do not crash a production app/ledger.

Adapter layer: fake Drive/printer/clock/filesystem for deterministic transient/permanent/ambiguous outcomes, plus actual business-authorized Google sandbox/account and actual Windows printer only in integration/UAT. Offline is measured by removing network rather than simply returning false from a connectivity flag. Windows ACL/DPAPI tests require actual different OS user or replacement environment.

Acceptance layer: real approved catalog/stock, supported Windows 11 x64 hardware/printer, approved financial/legal profiles and witnessed/recorded actual receipts. Surely records internal acceptance; accountant/business confirmations provide external evidence as needed. Test transactions remain in an isolated data directory and obvious TEST profile; they never pollute production. No service sends messages to external persons automatically.

## Financial fixtures and invariants

F-01–08 in [financial design](FINANCIAL_PROCESSING_DESIGN.md) are arithmetic specifications, not approved tax treatment. Implement golden cases exactly, including expected integer amounts. Synthetic 12% only tests math.

| Case | Stimulus | Required assertion |
|---|---|---|
| F-01 / T-04,06 | 250.00 no-tax cart, 300.00 cash | Applied250.00, Tender300.00, Change50.00; report cash250.00, not300.00; inventory once. |
| F-02 / T-04 | 0.5×0.01 enabled fractional step | Round0.005 to0.01; no binary-float artifact. Whole-step product rejects0.5. |
| F-03–05 / T-04 | Inclusive/exclusive inputs and10% generic discount | Exact outputs from finance table; Net+Tax=Payable, GrossExTax=Net+DiscountExTax. Changing current tax afterward never changes prior receipt/report. |
| F-06 / T-04 | Three1.00 lines, fixed0.01 discount | Allocation0.01,0,0 by persistent LineNo tie-break; sum0.01 and total2.99. Reordered rendering does not change persisted allocation. |
| F-07 / T-10 | 3×35.00 inclusive synthetic12%, fixed5.00 discount; return one unit three times | Original Net89.29/Tax10.71/Total100.00; refund totals33.33,33.33,33.34; Net29.76,29.76,29.77; Tax3.57 each; DiscountExTax1.48,1.49,1.49; full reversal exact. |
| F-08 / T-04,07 | Invalid scale/qty/price/rate, excessive discount, currency mismatch, unsupported strategy | Typed rejection, no completed order/payment/stock/audit-of-success/artifact. Failed validation can be audited safely. |
| F-09 / T-10,12 | 112.00 synthetic inclusive sale day1, full return day2 | Day1 Net100/Tax12/Collected112; day2 Net revenue-100/Net tax-12/Net collected-112, original sale count0 and correction count1; range net0. Original day1 sale still visible. |
| F-10 / T-11 | Opening100, cash sale250 tender300/change50, cash-in20, cash-out10, cash refund40, GCash sale112 | Expected cash320, not370 or432; counted315 means variance-5; GCash separated. |
| F-11 / T-10 | Two correction requests contend for last remaining unit | At most one commits; second reloads committed balance and rejects over-refund; no duplicate refund/stock return. |
| F-12 / T-04 | Large values/rates/scale near upper bounds and integer-sum overflow | Checked rejection/error, no wrapping, no fallback to REAL/double, no partial commit. |

Generated invariants: discount allocated sum equals target; all allocations bounded/nonnegative; line/order sums exact; cumulative refund components monotone and <=original; final full return equals original every component; payment applied equals sale total; failed/abandoned checkout has no stock deduction; tracked balance equals signed movement sum; snapshot relational/JSON representations agree.

## Detailed verification scenarios

| ID / type | Cases and observable pass evidence |
|---|---|
| T-01 identity | Valid/invalid/inactive/offline login; username normalization uniqueness; hash format/salt/work factor/rehash; failed-count cooldown survives restart; no password logging; owner bootstrap once; owner loss invokes recovery not default credentials; reset/change invalidates sessions. |
| T-02 authorization/audit | Invoke each protected command/query directly with each capability template, no session and wrong scoped user ID. Cashier cannot fetch another order or refund via crafted ID. Administrator cannot grant Owner/reset Owner. Last owner cannot be removed. Audit contains actor/reason/target but no secrets; sensitive reauth expiry enforced. |
| T-03 catalog | Product/category create/edit/deactivate and stale Version conflict; duplicate normalized SKU rejected; deactivated product in cart fails final validation; historic SKU/name/price unchanged. TrackStock change requires explicit movement/reconciliation. |
| T-04 arithmetic/provider | Golden/generated cases, integer mapping/query aggregates, minor units round trip, effective profile intervals, supported/unsupported strategy and currency; no REAL financial column. Immutable tax/business/template snapshots survive current-config edits. |
| T-05 stock | Opening movement, authorized addition/deduction/reason, negative policy/override, movement+balance+audit rollback, multiple product lines and conditional stock check. Stock return only explicit Return disposition, source tracking snapshot respected. |
| T-06 cashier workflow | Both order types × both tenders; add/change/remove; insufficient/exact/excess cash; GCash missing verification rejected, reference optional accepted, verification actor/time persisted. Network unavailable GCash is still rejected unless real manual verification evidence available; cash remains functional. |
| T-07 checkout crash/idempotency | Fail before prepare/after prepare, during journal append/flush, before transaction, after order/items/payment/stock/audit/outbox writes but before commit, during commit, after commit/before UI acknowledgment. Atomic financial tuple appears entirely or not at all. Same RequestId returns same sale; changed fingerprint rejected. Never automatically charge again for OutcomeUnknown. |
| T-08 printing | Approved actual printer/paper/driver, long names/100-line output/margins/peso/logo/tax fields; printer missing/offline/paper-out/spool timeout/cutter issues where applicable. Sale remains committed. Unknown spool outcome prompts controlled marked reprint, no second stock/payment. Test print has no financial sale and obvious TEST label. |
| T-09 history/calendar | Transaction/date/status/cashier filters and scopes, cursor pagination without duplication, midnight/cutoff/clock rollback edges, historical business date unchanged after config edit, request-outcome recovery lookup. |
| T-10 corrections | Full/partial/next-day, same tender, over-refund/cumulative race, reauth/reason/self-approval, GCash external refund evidence, original unchanged, explicit discard/return, repeat request, crash during correction commit and lost acknowledgment. Refunded originals still counted in original sale history/report period. |
| T-11 shifts/cash | Optional/required-shift setting, one open shift per cashier, opening/movements/reason/count/variance, cross-midnight, refund paid in later shift, closed-shift immutability, authorized abandoned-shift closure. If Should feature deferred record acceptance scope, not silent pass. |
| T-12 reports/exports | All seven reports; original/correction reconciliation, daily/monthly/range, prior/current profile, no-data range, boundary times, close watermark/revision, pending missed-month catch-up. CSV escaped quotes/newline/formula prefix, invariant amounts/UTF-8; PDF totals agree. Artifact bytes/source snapshot stable. |
| T-13 offline/local faults | Initialize once then disconnect all network: sign in, sell, print/history/report/stock; no Drive timeout on checkout path. Reconnect later. Simulate disk full/access denied/journal failure/DB busy; safe typed error and no partial sale; no promise of operation on failed local storage. |
| T-14 materialization/durable queue | Crash after sale commit before file creation, mid temp write, after rename before DB Ready, missing file/serializer/template asset. Intent survives; identical regeneration accepted, mismatch held. Pending records survive restart; local transactions retained after upload. Backup payload cannot be forged from newer DB. |
| T-15 remote dedup/lease | Create success then timeout/lost local ack; same persisted remote ID used and one file exists. Crash after ID allocation/journal persistence; repeated manual retry; expired resumable session; stale lease worker; quota/rate-limit/token revocation; checksum/metadata conflict. Restore without mapping enters RecoveryHold, never fresh-ID auto-create. Wrong account/root stays held. |
| T-16 backup/restore | Live snapshot during sales, interrupted backup, corruption/foreign key error, missing assets, cleanup retains last good/protected copies, missed app-closed schedule catch-up. Old backup/replacement-device restore reconciles later number reservations/remote mappings/prepared payments; unknown upper bound blocks series. Integrity and financial/stock reconciliation pass before reopen; token reauth; record actual RPO/RTO. |
| T-17 configuration | Financial/calendar/business profiles immutable after use and effective nonoverlap, operational settings separate; Unknown tax blocks production but synthetic engine tests allowed; future version only affects new sales; activate production only after real readiness evidence. |
| T-18 usability/performance/deployment | Supported Windows x64 offline installer/runtime, clean install and upgrade/failed migration compatible rollback; actual printer, device profile, keyboard/touch/focus/lock/error walkthrough, numeric NFR measures. Credentials/operational data retained across update; uninstall does not silently delete ledger. |
| T-19 architecture/handover | Dependency checks: Domain no EF/WPF/Google; UI no DbContext. Single instance/data-dir lock. Source/build/version/license/template assets documented; stable IDs/service boundary support later API; no network DB share. Solo review evidence recorded, not fictional independent QA. |

## Fault-injection assertions

Use process-termination and exception hooks only in test builds. After each restart run integrity_check, foreign_key_check and ledger reconciliation; compare order count, request link, payment, line/component totals, stock movement, financial audit, artifact intent and print intent. Journal reservations may exceed committed sequences (safe gaps); no committed number exceeds durable reservation evidence in normal operation. Simulate storage write/flush failure and torn final journal record; ambiguity requires hold, not skipped validation.

Drive fake must support remote create having succeeded before throwing, not only pre-request failure. Remote listing with stale visibility must **not** authorize fresh create after restored identity loss. Physical printer tests distinguish adapter/spool success from actual paper; a fake PrintSuccess cannot satisfy receipt UAT. Backup tests restore in separate environment with no old DPAPI identity, not just open the copied DB on same process.

## Measurable target profile

Engineering-selected target profile D-15, to validate on actual hardware; not achieved results:

| Measure | Dataset/environment and acceptance target |
|---|---|
| Target device | Supported Windows 11 x64, 8GB RAM, SSD; exact CPU/printer/driver recorded. |
| Representative volume | 1000 active products, 100000 completed sales (average5 lines), representative corrections/audit; 100-line cart, backlog10000 artifacts. |
| Lookup/cart | Warm product search and local cart recalculation p95<=150ms per action over1000 samples; include50 slow initial/warm-up samples separately, no hidden exclusions. |
| Local checkout | p95<=1s from valid Confirm to durable local outcome across1000 synthetic transactions including normal stock/audit/outbox/journal work; excludes human verification and physical paper/network. Report p99/max/errors too. |
| Reports | Daily<=2s, month<=5s p95 on reference dataset; separate PDF render/export duration measured, no UI dispatcher blocking. |
| Background contention | Same checkout budgets with uploader/backup active; no DB lock held across HTTP; account for checksum/backup I/O. If unmet, prioritize checkout/rate-limit worker before adding infrastructure. |
| Offline | At least one complete business-day simulated selling/restart while network removed,10000 queued artifact backlog; all local sale functions pass; recovery cloud copies after reconnection within measured throughput, no invented fixed completion time. |
| Recovery targets | Proposed local RPO<=24 operating hours/RTO<=2h on representative replacement environment, conditional on available verified backup. Off-site RPO measured separately as last verified independent copy; outages may exceed target. |
| Usability | Complete representative dine-in/takeout both tender paths with keyboard and touch; visible total/method, focus/validation; primary targets>=44 logical pixels, shell usable1366×768; no reliance on color alone. |

Free-space capacity accepted before production: enough for live DB/WAL, journal, seven daily backup generations, at least two extra backup/temp workspaces, assets and approved offline artifact backlog with safety reserve. First implementation sizing test measures actual DB/archive size; no claim that a fixed GB estimate is sufficient. Health warning at max(2GB,2×measured typical backup size); blocking threshold based on required transaction/temp headroom, never intentionally allow unsafe write to proceed. Actual thresholds are configurable bounded engineering defaults reviewed with workload evidence.

## FR and acceptance trace

| FR | Required design evidence/test |
|---|---|
| FR-001 | Security authentication; T-01/13; AC-01 |
| FR-002 | User lifecycle/bootstrap/reset; T-01/02 |
| FR-003 | Capability matrix and service scopes; T-02; AC-01/06 |
| FR-004 | Catalog/product version/archive; T-03 |
| FR-005 | Category maintenance; T-03 |
| FR-006 | Price/currency exact mapping; T-03/04 |
| FR-007 | Tracked balances and snapshots; T-05/07; AC-05 |
| FR-008 | Reasoned movement transaction; T-02/05; AC-05 |
| FR-009 | Dine-in/takeout; T-06; AC-01 |
| FR-010 | Cart/edit/validation; T-03/06/18 |
| FR-011 | Finance algorithms/profiles; T-04/17; AC-02/13 |
| FR-012 | Atomic checkout/request/number protocol; T-06/07; AC-03/04 |
| FR-013 | Applied/tender/change; T-04/06; AC-02/03 |
| FR-014 | GCash optional reference; T-06; AC-03 |
| FR-015 | Manual verifier/evidence/unknown outcome; T-06/07; AC-03 |
| FR-016 | Snapshot/physical printing/reprint; T-08; AC-07/13 |
| FR-017 | Scoped/paged immutable history; T-09; AC-04 |
| FR-018 | Linked corrections/tender/stock; T-10; AC-06 |
| FR-019 | Shift conditional scope and closing; T-11 |
| FR-020 | Cash movements/expected equation; T-11 |
| FR-021 | Dashboard financial dictionary; T-12; AC-08 |
| FR-022 | Daily/monthly/range ledger queries; T-09/12; AC-08 |
| FR-023 | CSV/PDF and immutable export source; T-12/14 |
| FR-024 | Audit transaction and redaction; T-02/05/07/10/17; AC-06 |
| FR-025 | Offline local truth; T-13; AC-09 |
| FR-026 | Intent/durable payload/upload identity; T-14/15; AC-10/11 |
| FR-027 | Status/retry/error state; T-15; AC-11 |
| FR-028 | Scheduled verified backup/restore; T-16; AC-12 |
| FR-029 | Versioned settings/access/production gates; T-02/17; AC-13 |
| FR-030 | Stable-ID/port boundaries/future scope; T-19 |

All11 BRD §9 quality categories are covered: usability/performance/compatibility T-18; availability T-13; reliability/data integrity T-04/05/07/10/14/16; security T-01/02/17; maintainability/scalability T-19; recoverability T-16; localization T-04/08/09/12/17. Twelve §8 business-rule mappings and13 section-24 local AC definitions remain in approved traceability matrix; this plan adds actionable evidence without changing their meaning.

## Evidence and release disposition

Per run record build/commit, exact dependency/schema/config versions, OS/printer/profile/dataset, expected/actual results, duration, artifact hashes, failed assertions, defect IDs, reproduction steps and Surely's review/date. Attach accountant/business confirmations to relevant legal/money fixtures where applicable; no independent test staff signature needed.

Block release for any reproducible data loss/duplicate charge/number/stock effect, wrong approved money/tax, unauthorized action, unresolved required receipt/legal field failure, unsuccessful restore or unsupported mandatory sync/operating behavior. Lower-impact UI defects may be accepted by Surely with workaround, impact and follow-up recorded. All Must FRs require passing evidence; Should FR-019/020 require inclusion or explicit documented deferral, FR-030 readiness design still required. No 'pass' assigned merely because a future test is specified. Production checklist must be complete before real sales.
