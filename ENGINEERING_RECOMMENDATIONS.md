# Engineering recommendations

Date: November 16, 2026. Source: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md), sections 3–30 and Appendices A–B. Status: **proposals for review**, not new confirmed requirements, implementation authorization or completed controls. G and D references are defined in [gap analysis](REQUIREMENTS_GAP_ANALYSIS.md) and [decision register](OPEN_QUESTIONS_AND_DECISIONS.md).

## Priorities and disposition

P0 recommendations resolve financial/recovery semantics before dependent implementation. P1 recommendations define reliable release behavior. P2 recommendations support handover and future expansion. Proposed responsible roles require confirmation. A product-owner decision, including an explicit deferral with impact, is the closure mechanism for business policy. Technical approval alone cannot settle tax or refund policy.

| ID / priority | Recommendation and rationale | Baseline and findings | Proposed approval/evidence | Scope impact |
|---|---|---|---|---|
| REC-01 P0 | Obtain tax/invoice applicability review before locking the money/invoice model; distinguish legal invoice series from internal sale ID. Avoid redesign and unsupported compliance claims. | Sections 16,20,29; FR-011,016,029; G-01 | D-01–02; owner/accountant memo and approved specimen | Clarifies pending baseline. Any required electronic-invoice/reporting expansion needs a change request. |
| REC-02 P0 | Approve exact calculation tables, rounding/discount allocation, tax effective dates and immutable inputs; use decimal-safe calculation and reviewed exact persistence. | Sections 8,11,20; FR-006,011; G-02 | D-03,10; accountant golden examples and T-04 | Detailed design; do not hard-code PHP/tax defaults before confirmation. |
| REC-03 P0 | Define cancellation/void/refund semantics with linked immutable corrections, cumulative caps, reason/approval, tender and stock disposition. | Sections 8,14,17–18; FR-018,024; G-03 | D-04; workflow plus T-10/12 | Refines in-scope corrections; partial-refund policy remains pending. |
| REC-04 P0 | Commit sale, number, payment, stock, financial audit and durable sync intent atomically. Use stable checkout request ID so retry returns original outcome. | Sections 8,13–15; FR-012,024,026; G-04 | D-10–12; transaction design and T-07 | Resolves unsafe sequence wording; no cloud transaction inside checkout. |
| REC-05 P0 | Define applied amount/tender/change and GCash verifier/time; approve received-but-unrecorded payment recovery and absent-verification procedure. | Sections 5.2,8,13; FR-013–015; G-06 | D-07; T-06/07 | Retains manual GCash and optional reference; no gateway or split-tender expansion assumed. |
| REC-06 P0 | Approve report dictionary and correction-date examples, then share calculations across dashboard, reports and exports. | Sections 13,15,17; FR-021–023; G-05 | D-05–06; T-12 reconciled original/correction fixtures | Clarifies existing report scope; advanced analytics remain future. |
| REC-07 P0 | Design recovery before schema freeze: restore authority, maintenance mode, latest-sale/high-water reconciliation, payload recovery and token reconnect. | Sections 8,14–15,19; FR-012,026,028; G-11–12 | D-12–13; T-16 replacement-device drill | Required resilience refinement; no cloud-to-live-ledger merge added. |
| REC-08 P1 | Keep one modular WPF application with application/domain boundaries, local SQLite and short-lived persistence scopes. Preserve stable IDs and future API boundary. | Sections 10,27; Appendix B; FR-025,030; G-20 | D-10,20; architecture review/T-19 | Aligns with baseline; no new initial backend, broker or distributed deployment. |
| REC-09 P1 | Prefer immutable per-sale exports with separate versioned summaries, or explicitly approve immutable batches. Persist stable remote identity and reconcile ambiguous upload outcomes. | Section 15; FR-026–027; G-08–09 | D-11; state machine, artifact contract and T-14/15 | Proposed export granularity, not confirmed policy. Naming and retention require owner approval. |
| REC-10 P1 | Define worker claim/lease, capped backoff/jitter, stale-work recovery, safe paths and missing-payload regeneration. Display backlog age, failures and reconnect/manual retry. | Sections 11–12,15; FR-026–027; G-09 | D-11,13; T-14/15 | Refines required durable background sync; exact intervals not prescribed here. |
| REC-11 P1 | Define backup schedule/catch-up and independent retention policy; use supported consistent SQLite backup plus manifest/checksum and restore validation. Protect last good generation. | Sections 15,19; FR-028; G-11 | D-13; scheduled/missed/disk-full T-16 | Seven generations/daily timing remain proposed. An unattended service is a separate design choice. |
| REC-12 P1 | Approve least-privilege capabilities, bootstrap/reset/session rules and Windows identity; enforce in services and protect last-owner access. Log financial changes safely. | Sections 6,18; FR-001–003,024,029; G-13,16 | D-14; T-01/02/17 | Refines existing security; avoids automatic financial access for technical admins. |
| REC-13 P1 | Establish business ownership of OAuth project/client/account/folder, minimal scope and credential lifecycle. Specify DPAPI scope and replacement-device reauthorization. | Sections 15,18–19; FR-026–029; G-16–17 | D-13–14,17; access/threat review and future reconnect test | No connection or production configuration authorized by this document. |
| REC-14 P1 | Confirm printer early and approve post-commit failure/reprint behavior; retain historical receipt inputs/template context. | Sections 11,16,21; FR-016; G-15 | D-02,16; physical T-08 | Customer receipts retained; kitchen routing/cash drawer are not added. |
| REC-15 P1 | Confirm finished-product units, bounds, tracked/untracked policy, opening movements and override rules; balance/movement/audit together. | Sections 8,11,13; FR-004–008; G-10 | D-09; T-03/05 | No ingredient inventory, procurement or recipes. |
| REC-16 P1 | Explicitly retain/defer Should shift/cash features; if retained, link sales/refunds and approve expected-cash equation. | Sections 5,7,11,17; FR-019–020; G-07 | D-08; T-11 if included | Product-owner scope disposition needed; no silent omission. |
| REC-17 P1 | Agree numeric NFRs and target workloads; expand acceptance from the traceability matrix to include monthly/export/account/sync-status behavior and failure tests. | Sections 9,21,23–24; all FRs; G-14,19,21 | D-15,19; approved T-01–19 plan | Proposed measurable refinement; test targets require owner agreement. |
| REC-18 P2 | Select packaging, version/license policy, update/migration recovery and support ownership. Preserve source/build guides and operational runbooks. | Sections 10,21,28; G-18 | D-18; T-18/19 and handover checklist | Completes existing deployment/documentation deliverables; no automatic update service assumed. |

## Concrete acceptance examples to approve

These are proposed scenarios, not invented business policies:

- Cash sale: persist applied amount equal to approved total, separately retain tender and change; reconcile cash ledger using applied sale amount rather than all tender received (FR-013; D-07).
- Interrupted checkout: before commit there is no partial sale/stock reduction; after commit and lost UI response, retry retrieves the same sale and number (FR-012; D-10–12).
- Refund: an approved partial correction retains the original line, limits cumulative returned quantities/value, applies approved physical stock disposition and appears on the correct reporting day (FR-018,022; D-04–05).
- Cloud retry: remote success followed by lost acknowledgment reconciles the same artifact identity and content; it does not create a second transaction export (FR-026–027; D-11).
- Recovery: restoring yesterday's backup cannot reopen selling with a reused number or silently discard today's known external receipts/cloud artifacts (FR-012,028; D-12–13).
- Printing failure: sale remains completed; an authorized reprint renders approved historical content without charging or deducting stock again (FR-016; D-02,16).

Use accountant-approved sample amounts and operator-approved procedures when turning these into executable tests. No illustrative formula substitutes for pending legal/tax treatment.

## Approval route

Review the analysis and decision register first. Record accepted/rejected/deferred disposition for each recommendation and any changed requirement, affected acceptance criteria and schedule impact. Resolve financial and recovery contracts before schema/design approval; collect final catalog/hardware/cloud inputs before dependent UAT. Explicit product-owner implementation approval is a separate gate. Production release requires signed UAT, tax/invoice review, actual printing, restore evidence and accountable support.

No deviation from the confirmed Windows/single-terminal/future-readiness/order-type/payment/inventory/printing/offline/Drive constraints is recommended. Any later deviation should be documented with rationale, alternatives, risk, FR/acceptance impact and owner approval before adoption.
