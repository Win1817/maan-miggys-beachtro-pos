# Install, update and maintenance guide

Version 1.0 · November 17, 2026 · IB-27, [POS-DEC-011](POS-DEC-011.md). Builds are Test-profile only until production activation is approved.

## What you get

| File (in `dist\`) | Purpose |
|---|---|
| `BeachtroPOS-<version>-win-x64.msi` | The installer. Includes everything; the shop PC needs no .NET or internet. |
| `BeachtroPOS-<version>-win-x64.msi.sha256` | Its SHA-256 checksum. |
| `release.json` | Version, checksum, newest database change and source commit. |

Build it on the development PC with `powershell -ExecutionPolicy Bypass -File build\package.ps1`. It needs the .NET SDK and, the first time only, about 100 MB of NuGet downloads (runtime packs and WiX Toolset 5.0.2).

## Install (first time)

1. **Check the file.** Copy the MSI and its `.sha256` to the shop PC and compare checksums:
   `Get-FileHash .\BeachtroPOS-0.12.0-win-x64.msi -Algorithm SHA256`
2. **Install.** Double-click the MSI. It installs for all users into `C:\Program Files\Beachtro POS` and adds two Start-menu entries: **Beachtro POS** and **Beachtro POS Maintenance**. Windows asks for administrator approval once.
   - The installer is not code-signed yet, so Windows SmartScreen may warn. Signing is decided before production (IB-32).
3. **First start.** Sign in to Windows with the account the shop will use every day; a standard account is recommended. Start **Beachtro POS**. Data is kept per Windows account in `%LocalAppData%\BeachtroPOS-Test` (Test builds), and the folder is made private to that account at every start.
4. **Set up.** Create the owner account, or choose **Replacing a PC? Restore from a backup** (see the [recovery runbook](RECOVERY_RUNBOOK.md)).

## Update to a newer version

1. Close Beachtro POS on the shop PC and check the new MSI's checksum.
2. Install the new MSI over the old one. It replaces the program files and **never touches the data folder**.
3. Start Beachtro POS. If the new version changes the database, it first writes a verified backup named `backups\pos_preupdate_<time>_<id>.zip`, then updates the data. It records both in the audit log, as "Backup.PreUpdateCreated" and "System.Updated" with the old and new schema.
4. **If the update fails**, the app puts the database back exactly as it was and keeps the failed copy in `recovery\update_failed_…`. It then stops and asks you to reinstall the previous version. Nothing is lost.

## Go back to an older version (rollback)

An older version **never opens data written by a newer one**.

1. Uninstall the newer version (Settings → Apps). Your data is kept.
2. Install the older MSI and start it. It shows **This data is from a newer version**.
3. Prefer reinstalling the newer version. If that version is unusable, use **Roll back to this backup**. The newest `pos_preupdate_…zip` is chosen for you. Sign in with an Owner or Administrator account from that backup.
   - Everything recorded after that backup is left out.
   - The newer database is kept in `recovery\restore_…` as evidence.
   - The app restarts in recovery mode. Finish on the **Recovery** page, where the number journal still prevents reusing receipt numbers.

## Uninstall and data retention

- **Uninstalling removes only the program files.** Sales, backups, journals, logs and recovery evidence stay in the data folder.
- To remove data deliberately, first copy the newest backup somewhere safe, then delete the data folder by hand.
- **Kept automatically:**
  - logs for 30 days;
  - at least 7 backups, never the 2 newest verified ones, and never one still waiting to upload;
  - recovery evidence until you delete it.
- No sale, refund or audit record is ever deleted by the app.
- Legal retention periods are to be confirmed with the business's accountant before production.

## Maintenance tool (lost owner password)

Use it only when no Owner can sign in.

1. Close Beachtro POS.
2. Right-click **Beachtro POS Maintenance** and choose **Run as administrator**. Without administrator rights it shows status only.
3. The window shows the data folder, database version, mode, active owners and latest backup.
4. Enter the Owner's username, tick the confirmation and choose **Back up, then reset the password**.
   - It first writes a verified backup (`pos_premaintenance_…zip`).
   - It then shows a temporary password **once**, signs everyone out, and records the reset as the System actor with your Windows account and PC name.
5. Start Beachtro POS and sign in with the temporary password. You must choose a new one immediately.

## Data folder permissions

At every start the data folder is limited to the Windows account running the POS, SYSTEM and Administrators, with nothing inherited from a shared parent folder. Settings → **Backup and sync** shows "Data folder: private", or what else has access if it could not be fixed. Keep the PC's disk encrypted (BitLocker or device encryption) where available.

## Verified for 0.12.0 (November 17, 2026)

| Check | Result |
|---|---|
| Package build | `BeachtroPOS-0.12.0-win-x64.msi`, 57 MB, self-contained (296 files including the .NET and WPF runtimes); Windows Installer validation passes (ICE60 suppressed, see the installer project) |
| MSI contents | Product version 0.12.0, all-users install, fixed UpgradeCode, Start-menu shortcuts for the app and maintenance |
| Administrative extract (`msiexec /a`) | The extracted app starts on its own without .NET installed in its folder path |
| Update from the previous database version | Pre-update backup written, 1 database change applied, update audited, normal sign-in |
| Newer data on this version | Rollback screen shown; data untouched until rollback; rollback restored the pre-update backup, kept evidence, set recovery mode, restarted to sign-in |
| Database file missing on a used PC | Restore-only screen; no new owner offered |
| Maintenance window, not elevated | Status shown; reset disabled with instructions |
| Automated | 6 tests in `UpdateAndMaintenanceTests` (update, failed-update revert, rollback, missing database, folder permissions, maintenance reset) |

**Not yet done:** a full install and uninstall through the Windows Installer service on a clean PC, since it changes system folders and needs administrator approval. That belongs with IB-28, together with signing and the real printer and Drive tests.
