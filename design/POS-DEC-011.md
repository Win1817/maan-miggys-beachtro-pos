# POS-DEC-011 — Authorize milestone 12 (installer part): installer, updates, rollback, data protection, maintenance (IB-27)

Date: November 17, 2026 · Decided by: **Surely Win Dilag**, product owner and sole engineer
Follows [POS-DEC-010](POS-DEC-010.md). Tracked as GitHub milestone "M12 Installer and real-device integration". Authorized by "Proceed", in reply to the offer to build the installer part now.

## Decision

1. **IB-27 authorized; IB-28 deferred.** IB-28 (real printer, Google Drive, a physical second PC, performance and user acceptance with measured evidence) needs real hardware and accounts, which POS-DEC-002 does not allow yet. M12 stays open for it.

2. **IB-27 scope and choices**:

   | Area | Choice |
   |---|---|
   | Package | Self-contained .NET 10 win-x64 publish (no .NET install needed on the shop PC), in a folder rather than single-file because WPF ships native libraries. Built by `build/package.ps1` into `dist/`: the MSI, its SHA-256 and a `release.json` listing version, checksum and the newest database migration. |
   | Installer | **Windows Installer (MSI) built with WiX Toolset 5.0.2**, licensed MS-RL; later WiX versions add a separate maintenance-fee agreement, so the version is pinned (to be reviewed before production). Per-machine install to `Program Files\Beachtro POS` with a Start-menu shortcut; offline; no auto-updater and no Windows service. A newer MSI replaces an older one (major upgrade). Installing an older MSI over a newer one is refused, so a binary rollback is a deliberate uninstall-then-install. **The MSI never touches the data folder**, so uninstalling keeps all sales, backups and journals. Unsigned while Test-only; signing is decided before production (IB-32). |
   | Update at startup | Before any migration, the app inspects the database. If the new version needs to change it, it first writes a **verified pre-update backup package** (`pos_preupdate_…zip`, same format as other backups), then migrates, then records the backup and an audit entry with the old and new schema and the app version. **If the migration fails**, the failed database is kept in `recovery\update_failed_…`, the pre-update database is put back, and the app stops and says which version to reinstall. |
   | Newer data | An older app never opens data written by a newer one. It shows **Roll back an update**: reinstall the newer version (recommended), or restore a backup made before the update. That second path needs an Owner or Administrator sign-in **from that backup**, keeps the newer database as evidence, uses the normal restore checks (same installation, number journal reconcile) and leaves the PC in recovery mode, so the owner reviews and reopens it on the Recovery page. |
   | Missing database | If `pos.db` is missing but the journals or backups show the PC was in use, the app does **not** offer to create a new owner (that would start an empty ledger). It offers only **Restore from a backup**, with the live journals still in place to prove numbering. |
   | Data folder protection | At every start the data folder's permissions are set to private: inheritance is broken, and only the signed-in Windows user, SYSTEM and Administrators have access. The result is logged, and a folder that cannot be made private is shown on Settings → Backup and sync. |
   | Data retention | Nothing financial is ever deleted automatically. Logs are kept for 30 days and backups by the retention rules from POS-DEC-007. Recovery evidence (`recovery\`) stays until the owner removes it by hand. The install guide documents this. |
   | Maintenance tool | `BeachtroPOS.exe --maintenance` opens a separate maintenance window. It requires the app to be closed (same single-instance lock) and Windows administrator rights ("Run as administrator"). It shows the data folder, schema and mode, and has one action: **Reset an Owner password** for a lost last-owner password. That action first makes a verified backup (proof of backup), then shows a temporary password once and forces a change at the next sign-in. It also ends all sessions and audits the reset as the System actor with the Windows account name. It is not a hidden bypass: it needs administrator access to the PC itself. |
   | Version | Builds from this milestone are 0.12.0. |

3. **Not in scope:** code signing, automatic updates, a custom MSI user interface (the standard Windows Installer progress dialog is used), and anything in IB-28.

4. POS-DEC-002 constraints continue: Test profile and synthetic data only; no real printer or Google account; production activation disabled. POS-DEC-009 (no taxation) applies.

Work stops after this milestone part for a report.
