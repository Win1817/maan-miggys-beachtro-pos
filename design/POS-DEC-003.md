# POS-DEC-003 — Authorize milestone 4 (IB-05, IB-06)

Date: November 16, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-002](POS-DEC-002.md). Milestones 0–3 were completed and pushed to the project repository.

## Decision

1. **Milestone 4 authorized**:

   | Backlog item | Scope |
   |---|---|
   | IB-05 | Local owner bootstrap, sign-in, password hashing/rehash, failed-attempt cooldown, sessions and lock, capability templates and service-level authorization, password reset/role/activation administration with last-owner protection, audit writing |
   | IB-06 | Versioned calculation/calendar/business profiles with Draft/Test/Approved states and non-overlapping effective intervals, effective-profile resolution, canonical profile serializers, operational settings kept separate, production activation boundary |

   Work stops after milestone 4 for a report. Milestone 5 onward needs a further decision.

2. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer, Google account, tax/registration data, catalog or user accounts; production activation stays disabled in this build.

3. **Commit attribution:** commits carry no AI co-author trailer (owner instruction, November 16, 2026).
