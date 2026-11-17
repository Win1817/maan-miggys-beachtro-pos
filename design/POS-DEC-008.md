# POS-DEC-008 — Authorize milestone 10: settings and user management screens (IB-25)

Date: November 17, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-007](POS-DEC-007.md). Tracked as GitHub milestone "M10 Settings and user management UI".

## Decision

1. **Order changed: milestone 10 before milestone 9.** Milestone 9 (IB-21 Google Drive OAuth, IB-22 sync screens) needs a business Google account, which POS-DEC-002 still does not allow. Milestone 10 needs no external input, so it is built first on the instruction "proceed with the next task". M9 stays open.

2. **Milestone 10 authorized** (IB-25):

   | Area | Scope |
   |---|---|
   | Users | New **Users** page for holders of `users.manage`: list accounts with roles and status; create an account with chosen roles; reset a password (temporary password shown once); change roles; deactivate/reactivate. Every change needs a reason and a fresh password confirmation, as the service already requires. Role check boxes the signed-in user may not grant are shown disabled with the reason. |
   | General | Printer and test page, shift requirement and Test sample data (moved, unchanged). |
   | Business | Business profile editor (registered name, address, contact, tax identifier, approval state, evidence reference) with its revision history. Test revisions must contain "TEST" (existing rule). |
   | Tax and calendar | New calculation profile revision (one row per tax entry: key, treatment, rate, price mode), new calendar revision (time zone, business-day start), both with start time ("now" or a future time in the business time zone), approval state and evidence reference. A live **preview** shows how a sample price is split into net and tax under the draft. An **effective profile preview** shows which revisions apply at any chosen time, and the revision history lists past, current and scheduled revisions with their checksums. Production readiness blockers stay visible. |
   | Backup and sync | The milestone 8 status card, unchanged, on its own tab. |

3. **Implementation choices**:
   - **Administrator and Cashier accounts (open question from M4):** the existing strict rule stays. An Administrator may only grant, remove or administer roles whose permissions it holds itself; it does not hold `pos.sell`, so it cannot create or reset Cashier or Manager accounts. Otherwise an Administrator could create a selling account, learn its temporary password and sell. Owners manage selling accounts. The rule moves to one shared function (`RoleDelegation`) used by both the service and the screen, so the screen never offers what the service refuses.
   - **Approved state not offered in the screens.** The service still supports Approved revisions with evidence (and its tests stay), but while this build is Test-only the screens offer only Test and Draft. The evidence field is present for both. Approved needs professionally confirmed tax rules (IB-29, milestone 13).
   - **Read-only history query:** a new `GetProfileHistoryAsync` returns every calculation and calendar revision (including Draft) and every business profile revision. Viewing needs `settings.operational`; saving still needs `settings.financial` plus fresh reauthentication.
   - **Preview is pure:** the draft preview runs the same `OrderCalculator` on one sample line per tax entry, without saving anything. Treatments the calculator rejects (for example Unknown) show the reason instead of a number.
   - **Not in scope:** paper width and touch mode stay fixed (receipt template v1 is 80 mm/42 columns); the full sync screens and Drive connection (M9); restore and recovery screens (M11).

4. **Fixed along the way:** white text on coloured buttons (Primary, Danger) was drawn in dark ink because the app-wide text style overrode it; button text now takes the button's own colour.

5. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer, Google account or tax data; production activation disabled.

Work stops after milestone 10 for a report.
