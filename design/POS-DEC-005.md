# POS-DEC-005 — Authorize milestone 6: screens and receipts (IB-13 to IB-15)

Date: November 16, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-004](POS-DEC-004.md). IB-12 was delivered inside milestone 5, so this milestone covers the screen items.

## Decision

1. **Milestone 6 authorized**:

   | Backlog item | Scope |
   |---|---|
   | IB-13 | WPF first-run owner setup, sign-in, forced password change, shell with TEST banner/operator/business date/lock, inactivity and Ctrl+L lock, POS screen (categories, search, cart, order type, totals, cash/GCash payment, completion result) with safe handling of every typed result |
   | IB-14 | Category/product editors, stock adjustment and movement history, order history with details and refund/void flow |
   | IB-15 | Immutable receipt model rendered from the sale/correction snapshot, versioned 80 mm text template with TEST / NOT A TAX INVOICE and REPRINT markers, durable print-job lifecycle with operator confirmation, reprint with reason, test print, Windows printer adapter (virtual printer for development) |

2. **Supporting additions** (needed to make the screens usable, no new product scope):
   - Order query service for transaction history and refund selection (FR-017), scoped by `orders.view.own` / `orders.view.all`.
   - Test-profile-only sample data loader (synthetic Test profiles, TEST business profile and sample products). Refused outside the Test profile. Settings screens remain IB-25.

3. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer configuration is required (a Windows virtual printer is used for development); production activation disabled.

Work stops after milestone 6 for a report.
