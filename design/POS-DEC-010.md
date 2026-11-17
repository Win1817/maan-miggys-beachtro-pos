# POS-DEC-010 — Authorize milestone 11: recovery and fault regression (IB-24, IB-26)

Date: November 17, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-009](POS-DEC-009.md). Tracked as GitHub milestone "M11 Recovery and fault regression". Authorized by "proceed".

## Decision

1. **Milestone 11 authorized**:

   | Backlog item | Scope |
   |---|---|
   | IB-24 | Replacement-device recovery: restore a backup package on a new PC from the first-run screen; a Recovery page for the whole runbook (check and restore a backup, number series holds, unfinished payments, held uploads, reopen); release of held uploads by checking the destination for existing copies; a written runbook; and an automated drill that restores an old backup onto a "new device" after later sales and uploads on the lost one. |
   | IB-26 | Fault regression against real SQLite for faults not yet covered (number journal unwritable, database busy, session ended between prepare and complete, artifact folder unwritable, interrupted correction, report close during an upload outage), plus a fault matrix that maps every fault to its tests. |

2. **Implementation choices**:
   - **Restore on a new PC** is offered only while the installation is brand new: no owner created yet and no sales. The person must sign in with a username and password **from the backup** whose account can restore backups (Owner or Administrator). The backup's installation identity is kept, because the business continues on new hardware, not as a new shop. The empty database the new PC created is kept as evidence.
   - **Numbers after a lost device.** On a new PC the old number journal is gone, and the backup's copy can be stale. Every series in the backup is blocked, and so is a series for **every business date from the backup's date to today**, because the lost PC may have sold on those days. The owner reopens each one by entering the highest number known to have been used, with evidence such as the last printed receipt or the uploaded files. Entering 0 means "no sales that day after the backup". The same rule applies to any restore where the live journal is missing, damaged or belongs to another installation.
   - **Upload identities after a lost device.** The backup's upload journal is copied in, so identities known at backup time are reused. Any upload still waiting without a known identity is put on hold (`IdentityJournalStale`), because the lost PC may have uploaded it after the backup. **Check destination and release** (Recovery page) searches the destination for each held file by its logical key. A copy with the same checksum is adopted. A copy with different content stays held. With no copy found, the file is released for a normal upload. The remote-store port gains a `FindAsync` by logical key; the real Drive adapter (M9) must implement it with an appProperties query.
   - **Unfinished payments.** A checkout or refund that was prepared but not completed in the restored data may have been completed on the lost PC after the backup. Completing it again could record the same sale twice. Recovery therefore never completes one. The owner closes it with a reason recording what really happened (for example "receipt MB-…-000014 found, money received"). Reopening is refused while any remain.
   - **Reopen** still needs every number series resolved, and now also every unfinished payment closed. After reopening, the Recovery page immediately makes a fresh verified backup (runbook step 9).
   - **Signing in during recovery** takes an Owner or Administrator straight to the Recovery page. Selling stays blocked for everyone until reopen, as before.
   - **Not in scope:** importing lost sales back from uploaded files (the runbook keeps that a separate, reviewed repair); artifact-file holds such as "a different file is already at the path" (they stay visible on the status card); Drive re-authorization (M9).

3. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer or Google account; production activation disabled. POS-DEC-009 (no taxation) applies.

Work stops after milestone 11 for a report.
