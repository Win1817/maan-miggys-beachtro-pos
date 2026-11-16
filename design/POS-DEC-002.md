# POS-DEC-002 — Implementation-readiness disposition (IB-00)

Date: November 16, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Supersedes nothing; follows [POS-DEC-001](DECISION_REGISTER.md) and the [Phase 2 readiness report](IMPLEMENTATION_READINESS.md).

## Decision

1. **Bounded implementation authorized for milestones 0–3 only**:

   | Milestone | Backlog items | Scope |
   |---|---|---|
   | 0 Setup | IB-00, IB-01 | This record; source control; .NET 10 SDK; pinned, licence-reviewed dependencies for milestones 1–3 |
   | 1 Skeleton | IB-02 | Solution, four assemblies, test projects, composition root, local paths, single-instance lock, isolated synthetic (Test) profile |
   | 2 Money kernel | IB-03 | Pure money/quantity/rate types, rounding, discount allocation, tax components, cumulative refunds; fixtures F-01–F-08, F-10, F-12 |
   | 3 Database | IB-04 | Baseline EF Core/SQLite schema, checks, FKs, indexes, immutability triggers, pragma verification, real-SQLite tests |

   Work stops after milestone 3 for a progress report. Milestone 4 onward (IB-05+) needs a further decision.

2. **Policy bundles B-01 to B-05 accepted as recommended, provisionally.** They are used only with synthetic test data and the Test profile. They are not production-approved settings.

3. **Excluded from this authorization:** real printer, Google account or OAuth credentials, real tax rates/registration/invoice data, real catalog or opening stock, real user accounts, and production activation.

4. **Source control:** git repository in `POS/`, one commit per milestone.

## Consequences

- Readiness checklist item "Surely records final implementation-readiness authorization" is satisfied **for IB-00–IB-04 only**.
- Readiness checklist item "Owner policy bundles accepted/amended" is satisfied **provisionally for non-production use**; production activation still requires confirmation.
- Production-readiness checklist is unchanged; no item is closed by this decision.
- Dependencies for later milestones (OAuth, PDF, installer) are deferred to the milestone that needs them, with licence review at that time.
