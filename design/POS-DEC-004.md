# POS-DEC-004 — Authorize milestone 5 (IB-07 to IB-12)

Date: November 16, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-003](POS-DEC-003.md). Milestone 4 was completed and pushed.

## Decision

1. **Milestone 5 authorized** (service layer and tests; no screens):

   | Backlog item | Scope |
   |---|---|
   | IB-07 | Categories and products (SKU, price, step, tax key, active, optimistic versions) and audited opening/adjustment stock movements with negative-stock control |
   | IB-08 | External, hash-chained number reservation journal; startup reconciliation; torn-write repair; safe holds and owner hold resolution |
   | IB-09 | Quote, durable prepared checkout request with fingerprint, payment validation (one tender), outcome lookup and abandonment |
   | IB-10 | One atomic completion: order, lines, tax, payment, stock, audit, sale snapshot, artifact and print intents |
   | IB-11 | Refund/void preparation and completion with cumulative allocation, same-tender evidence and stock disposition |
   | IB-12 | Consistent SQLite backup with manifest and verification, candidate validation, restore with evidence preservation, maintenance mode, numbering reconciliation and explicit reopen |

   Work stops after milestone 5 for a report.

2. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer, Google account, tax, catalog or user data; production activation disabled.

3. **Schema addition:** `Products.TaxKey` links each product to a tax entry in the effective calculation profile. The database design omitted it, but the finance design requires each line's treatment to be resolved.

4. Scope notes: the receipt model and printing (IB-15), Drive queue rows (IB-20, need an account binding) and zip packaging with retention (IB-23) remain later items. This milestone writes print and artifact *intents* and an unpackaged backup folder.
