# POS-DEC-009 — No taxation in this POS

Date: November 17, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-008](POS-DEC-008.md). Owner's words: "No tax rules for this POS" and "no taxation required for the system".

## Decision

1. **The POS charges no tax.** Prices are final, sales and refunds compute no tax, and receipts, the cart and new products show no tax lines or tax fields. This replaces the open tax-treatment questions (D-01 and IB-29); there are no tax rules to confirm.

2. **How it is built:**
   - A new treatment, **NotTaxed**, means "this business charges no tax". Unlike TestNoTax, it is valid in any profile, Approved included, and it is never treated as Exempt or ZeroRated, which are legal tax categories. It is added to the database CHECK rules of `TaxConfigurations`, `OrderItems` and `ItemTaxComponents`. SQLite changes a CHECK by rebuilding the table, so a second migration re-creates those tables' immutability triggers from the frozen baseline SQL. Checked on a copy of the owner's Test database: all 49 triggers present, integrity ok, no foreign-key violations.
   - **Switch to no tax** (`ApplyNoTaxPolicyAsync`, Settings → Calendar and tax, Owner plus password) starts a new pricing revision, effective now, in which every existing tax key is NotTaxed. Products keep their key and keep selling at the same price with no tax. Past sales keep the revision and the tax they were made under; nothing is rewritten. Running it again changes nothing.
   - **Sample data** creates a no-tax profile (one key, `NONE`), and running it again on older sample data switches that data to no tax.
   - **Products** no longer have a tax field. A new product gets the profile's no-tax key. Saving one is refused while a taxed profile is still in effect, with a pointer to the switch.
   - **Settings:** the tax editor and the draft tax preview from milestone 10 are removed. The tab is now "Calendar and tax": calendar revisions, a no-tax status line, the switch button when needed, the revision history and the production blockers.
   - **Production gate:** an Approved calculation profile is still required, but its meaning changes from "professionally confirmed tax rules" to "the owner's approved no-tax pricing profile". A profile that still computes tax is a blocker. Production stays disabled in this build (POS-DEC-002).
   - **Kept but dormant:** the money kernel still supports taxed lines, so the tested arithmetic, correction allocation and history from earlier milestones stay valid and old records stay readable. Reports keep their tax columns, which show zero.

3. **Roadmap:** milestone 13 no longer includes tax rules (IB-29). It keeps production catalog and configuration (IB-30) and confirming any receipt wording the business needs, such as a "not a VAT invoice" note, with the business's accountant. The app does not decide that wording.

4. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer or Google account; production activation disabled.

Work stops after this change for a report.
