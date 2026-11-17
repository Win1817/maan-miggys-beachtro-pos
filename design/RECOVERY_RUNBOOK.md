# Recovery runbook (operator steps)

Version 1.0 · November 17, 2026 · IB-24, [POS-DEC-010](POS-DEC-010.md). This is the hands-on version of the [restore runbook in the sync and recovery design](SYNC_RECOVERY_DESIGN.md#restore-runbook-and-hold-conditions). The rule throughout is: **when the PC cannot prove something, it holds and asks you. It never guesses.**

## When to use it

| Situation | Where to start |
|---|---|
| The shop PC is lost, stolen or dead, and you are setting up a new one | New PC → first-run screen → **Replacing a PC? Restore from a backup** |
| The database on the same PC is damaged or was wrongly changed | Sign in as Owner or Administrator → **Recovery** → **Restore a backup** |
| The app says a number series is on hold, or uploads are on hold | **Recovery** page |

You need: the newest backup package (`.zip`), an Owner or Administrator username and password **from that backup**, and evidence of what happened after the backup (the last printed receipts, uploaded sale files, GCash statements).

## Steps

1. **Stop selling** on any other device. If the business must keep trading, write sales on paper with a separate number range. Never invent POS records later.
2. **Find the newest backup package.** Look in `backups\` on the old disk if it can be read, then a copy kept elsewhere, then the destination folder. Keep the old disk untouched.
3. **Restore.**
   - New PC: install the app. On the first-run screen choose the package, enter the backup's Owner/Administrator sign-in and a reason, then **Restore from a backup**. This works only before any owner is created on the new PC.
   - Same PC: **Recovery** → **Browse…** → **Check backup** (it reports when the backup was made and its last sale) → reason → **Restore this backup**.
   - Either way, the current database and journals are first copied to `recovery\restore_…` as evidence, and selling pauses.
4. **Sign in.** An Owner or Administrator lands on the **Recovery** page. Confirm your password once; it covers five minutes of actions on that page.
5. **Number series on hold.** For each business date listed, enter the **highest receipt number known to be used** on that date and say how you know (for example "last printed receipt MB-20261118-000014"). Enter 0 for a date with no sales after the backup. When unsure, choose a higher number: a skipped number is harmless, a reused one is not.
6. **Unfinished payments.** Each checkout or refund that was started but not finished in the backup may have been finished on the lost PC. Look for its receipt or the money, then write what really happened and **Close** it. It is never completed here.
7. **Uploads on hold.** Turn the destination on (Settings → Backup and sync), write what you checked, then **Check destination and release**. Identical copies already on the destination are kept and not uploaded again. A different file under the same name stays on hold for you to examine. Files not found are uploaded normally. Held uploads never block reopening.
8. **Reopen.** Record the lost period: sales between the backup and the failure, with the receipt numbers you found. Then **Reopen and back up**. Selling resumes, and a fresh verified backup of the recovered data is saved at once.
9. **Afterwards.** Re-enter any paper sales only through a separately reviewed procedure. Keep the `recovery\` evidence folder. On a new PC, connect the business Google account again once Drive is available (milestone 9).

## What the app does for you

- **Numbers:** a series that cannot be proven is blocked, along with every business date from the backup to today, because the lost PC may have sold on any of them. A sale is refused until you resolve it, and a number is never reused.
- **Installation identity:** the backup's identity is kept on the new PC. The business continues; it is not a new shop.
- **Upload identities:** reused when known. Unknown ones are held until the destination is searched, so a lost PC's uploads are never duplicated.
- **Old backups:** a backup from a newer app version, a damaged package, or one with unexpected files is refused before anything changes.

## Drill results (November 17, 2026)

| Drill | How | Result |
|---|---|---|
| Automated: old backup onto a new PC | `RecoveryDrillTests` (real SQLite, fake destination). The lost PC sells 5, backs up after sale 3 with one checkout prepared, then after the backup uploads sale 3 with a lost acknowledgement, completes the prepared checkout and sells once more. The new PC restores two hours later, past midnight. | Wrong password refused. The lost day and the new day were both blocked. Selling was refused until reopen. Sale 3 was adopted from the destination, not uploaded again. The prepared checkout was closed, not completed. The lost day reopened above 5. The first new sale was `MB-20251117-000001`. The destination file count grew only by the new sale. The lost-period sales stayed out of the ledger. |
| Automated: conflicting remote copy | Another file sits under the same logical key | It stays on hold with RemoteConflict. Nothing is overwritten. |
| Automated: account checks | A Cashier from the backup, and an unknown user | Both refused with the same message. The new PC stays untouched. |
| In the app (Test profile, two data folders) | PC A: sample data, 1 sale, backup, 1 more sale. PC B: first-run restore, sign in, try to sell, resolve the series above 3, reopen | Selling was refused during recovery. Reopening made a fresh backup. The first sale on PC B was `MB-20261118-000004`. Time from restore to first sale was about 1 minute (scripted). |

Not drilled yet: a real Google Drive account (M9), a real printer, and a physical second PC (IB-28).
