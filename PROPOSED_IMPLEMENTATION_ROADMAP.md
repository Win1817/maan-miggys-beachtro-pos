# Proposed implementation roadmap

Date: November 16, 2026. Governing source: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md), especially sections 22–24, 28–30. Status: proposed future work, **not authorization to implement**. Only analysis/documentation has been authorized and performed. Durations and delivery dates are deliberately uncommitted pending scope, inputs and team capacity.

## Readiness recommendation

**CONDITIONAL GO** — the baseline is feasible, but implementation may begin only after Gate A below is satisfied and the product owner explicitly authorizes it. Today the authorization gate is closed. This recommendation is not a production GO. Pending tax treatment must not be assumed to mean zero tax or legally valid receipt output.

## Gates and decision sequencing

| Gate | Required evidence | Proposed approver | Current status |
|---|---|---|---|
| A: Analysis disposition and implementation entry | Review nine analysis documents; disposition G-01–21/REC-01–18; resolve I-gate decisions D-01–15,17,19–20 as applicable or explicitly approve bounded deferrals. Approve financial/payment/refund/report rules, number/restore protocol, access matrix, artifact contract, Should feature scope and NFR targets. Review logical architecture/data/test plan. Then record separate explicit implementation authorization. | Owners/appointed representative; accountant for financial/legal inputs; technical lead for design | Not satisfied; no approvals asserted |
| B: Core integrity demonstrated | T-01–07/10/13 relevant cases pass on local real SQLite; original/correction totals and stock reconcile; request/number uniqueness and sync intent survive crash tests; no UI-only authorization. | Technical lead; owner/accountant validate fixtures | Future |
| C: Feature and resilience acceptance | Reports/exports, actual printer, queue failure recovery, backup and replacement-device restore pass; final hardware/catalog/Google/installer inputs settled (D-09,13,15–18). | Technical lead plus owner/operators/accountant | Future |
| D: Production release | Tax/invoice applicability approved; all Must FRs and accepted Should scope evidenced; agreed NFRs measured; no open release-blocking defects; signed UAT, retention/recovery procedures, training, support and go-live checklist. | Product owner; relevant reviewers supply evidence | Future |
| E: Handover/stabilization closure | Critical incidents resolved, backup/sync monitored, support responsibility and guides accepted. | Owner and technical lead | Future |

If D-01/D-02 legal scope is unresolved, the owner can approve only expressly bounded non-production engineering after recording that limitation and its rework risk; that is a separate authorization, not a presumed exception. Final business identifiers/branding/catalog values may wait for configuration/UAT after their contracts are approved. Printer selection must precede hardware-dependent acceptance. A late mandatory compliance capability requires impact assessment and change approval.

## Proposed work packages

| Phase / dependency | Future activities and deliverables | Exit evidence / trace |
|---|---|---|
| 0. Review current package | Owner/accountant/technical review of findings and decisions; confirm scope and approval roles. Preserve original baseline and track approved addenda separately. | Gate A dispositions; sections 29–30; D-19 |
| 1. Detailed design, after authorization | Operator walkthroughs/wireframes; financial golden fixtures; logical/physical schema design, permission matrix, lifecycle, numbering, receipt and export contracts; backup/restore/update plans; version and license selections. Resolve any authorized design deferrals before dependent build. | Approved design and future test plan; sections 10–20,23; D-01–18,20 |
| 2. Local foundation and catalog | Authorized future project setup, composition root, local persistence/migrations, bootstrap/auth/session/capabilities/audit, products/categories/opening stock/settings. Record source/build and data-directory choices. | T-01–05,17; FR-001–008,024,029; sections 11,18,28 |
| 3. Atomic core checkout | Cart/order types, exact totals, cash/manual GCash, request identity and numbering, sale/payment/stock/audit/sync-intent commit. Receipt rendering/job boundary and restart recovery. | Gate B; T-04–08,13; FR-009–016,025–026; sections 13–14 |
| 4. Corrections and operating ledger | Immutable void/refund records, authorized stock-return treatment, history/search and shifts/cash movements if retained; close/count/variance procedures. | T-05,09–11; FR-017–020,024; sections 8,14,17–18 |
| 5. Reports and artifact materialization | Approved dashboard and daily/monthly/range/payment/product summaries; CSV/PDF output; consistent ledger definitions; business-day close, report revisions and missed-schedule catch-up. | T-12,17; FR-021–023; sections 15,17 |
| 6. Cloud and operational resilience | Only with approved account authorization: desktop OAuth/DPAPI, minimal-scope folder access, stable remote identity, leases/retries/status/manual retry; consistent backups, retention, replacement-device recovery and update recovery. | T-14–16,18; Gate C; FR-026–029; sections 15,18–19 |
| 7. Real-device UAT and release | Install on approved Windows/printer, validate real catalog/opening stock, disconnected sale and reconnect, accountant-approved invoice/tax cases, physical print failures, performance, recovery, guides/training and known-issues disposition. | Gate D, signed UAT and section-28 delivery checklist; T-01–19 as applicable |
| 8. Stabilization and handover | Monitor errors/backlog/backup age/print incidents, reconcile close reports, handle support, retest affected workflows after fixes. | Gate E; section 22 phase 7, section 28 |

This refines section 22 without expanding product scope: stock atomicity and durable sync intent belong in core checkout even if inventory screens/cloud transfer are built later. Recovery/numbering and tax snapshots must be designed before schema freeze rather than discovered in the resilience phase. Actual Drive calls, full reporting and recovery drills can still follow the core work. Do not ship an intermediate core build as production before the resilience and legal gates.

## Verification and release policy to approve

The [traceability matrix](REQUIREMENTS_TRACEABILITY_MATRIX.md) defines T-01–19 and links all 30 FRs, 12 section-8 rules, 11 section-9 categories and 13 section-24 bullets. Proposed evidence sequence:

1. Unit tests for approved exact calculations, state transitions and permission decisions.
2. Integration tests against actual SQLite provider/constraints/transaction behavior, including interruptions, retry identity, stock and financial reconciliation.
3. Adapter/integration tests for physical printer and Drive only after approved hardware/account access; fault injection for ambiguous remote success, missing payload and stale claims.
4. Representative-volume offline/performance tests and full restore on a replacement environment, including latest sale, numbering, artifacts, tokens and measured loss/downtime.
5. Operator UAT against real catalog, approved tax/invoice samples and signed expected reports; archive results/defects and approvers.

Propose blocking release for unresolved data loss/duplication, wrong tax/money, authorization bypass, unsupported required printer or failed restore; owner must approve the full severity policy (D-19). Any deferred Should feature requires documented approval and updated acceptance scope. No Must obligation is silently dropped. No fabricated test result or completion percentage should appear in status reporting.

## Delivery and operational checklist

Section 28 requires installer/deployed app; source/build instructions; schema/migrations/seed configuration; catalog/inventory/order/payment/report modules; approved receipts/printer integration; OAuth/queue/status; CSV/PDF and backups; user/admin guides; architecture/database documents; test/UAT/known-issues evidence; restore procedure; deployment/handover checklist. Track each artifact, reviewer and acceptance evidence during implementation. These future product deliverables are distinct from the nine current analysis documents.

Before go-live, identify who checks backup success and sync backlog, who handles paper-out and GCash uncertainty, who approves corrections, who may restore, and who provides technical support. Agree downtime/manual-sales handling and subsequent reconciliation; do not invent an emergency workflow in code (sections 18–19,26; D-04,07,13–14,18).

## Future multi-terminal phase

After a separate business approval, design central API/server database (PostgreSQL is the baseline example), terminal identity, concurrency/numbering, authentication, cutover/import and explicit disconnected-terminal conflict rules. Validate stock reservations/oversell and reconnection policy in that phase. Never deploy concurrent terminals over a shared SQLite file or use Drive artifacts as a replication protocol (sections 5.2,10.2,27; FR-030).

The immediate next step is review and written decision disposition. Implementation remains unstarted until explicit authorization is recorded.
