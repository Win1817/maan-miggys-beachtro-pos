# Beachtro POS

Native Windows 11 point-of-sale for Ma'an and Miggy's Beachtro.

- Requirements baseline: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) (never edited)
- Design: [design/](design/) — start with [IMPLEMENTATION_READINESS.md](design/IMPLEMENTATION_READINESS.md)
- Current authorization: [POS-DEC-007](design/POS-DEC-007.md) — milestone 8; earlier: POS-DEC-002 to POS-DEC-006. Test profile only
- Roadmap: [GitHub milestones](https://github.com/Win1817/maan-miggys-beachtro-pos/milestones) (M0–M14)

## Build

Requires the .NET SDK pinned in `global.json` (10.0.404, latest patch roll-forward).

```powershell
dotnet tool restore
dotnet build
dotnet test
```

Package versions are pinned centrally in `Directory.Packages.props`; restores are locked by `packages.lock.json`.

## Try it

1. `dotnet run --project src/Beachtro.Desktop` (Test profile; data in `%LocalAppData%\BeachtroPOS-Test`).
2. Create the owner account on first launch, then sign in.
3. Settings → **Load sample data** (needs your password) → Sell.
4. Optional: Settings → choose **Microsoft Print to PDF** as the receipt printer.
5. Shifts → open a shift with the drawer's cash; sell; record cash in/out; close with the counted cash.
6. Dashboard for today's figures; Reports → choose a report and dates → Generate → Export CSV or PDF.
7. Settings → **Backup and sync status** → **Turn on simulated destination** → **Run now**. Sale, refund, close and backup files appear under `artifacts\` and are copied to `simulated-drive\` (stands in for Google Drive; no account is used).

`--data-root <folder>` runs the Test profile against a separate folder (ignored in Production).

## Status

Milestones 0–8 complete: core (money, database, identity, profiles, catalog, numbering, checkout, corrections, backup/restore), the screens (owner setup, sign-in, lock, POS, orders with refunds, products, inventory, settings, receipt printing), shifts and cash movements, the dashboard, the seven reports with business-day close, CSV/PDF export, stored artifact files, the durable upload queue (simulated destination only) and zip backup packages with retention and a daily/monthly scheduler. Work stops here pending the next decision (POS-DEC-007).

| Project | Purpose |
|---|---|
| `src/Beachtro.Domain` | Exact money kernel (`Financial/`), persisted vocabularies (`Model/`) |
| `src/Beachtro.Application` | Ports, typed results, capability matrix/role templates, sessions, service contracts |
| `src/Beachtro.Infrastructure` | Data paths, lock, EF Core/SQLite schema, migrations, guard triggers, identity, profiles, catalog, numbering journal, checkout, corrections, backup/restore, shifts, reports, CSV/PDF rendering, artifact materializer, upload queue, scheduler |
| `src/Beachtro.Desktop` | WPF shell and composition root (Test profile only) |
| `tests/*` | Domain fixtures/invariants, real-SQLite schema/constraint tests, architecture rules |

Data lives in `%LocalAppData%\BeachtroPOS-Test\` (`data\numbers.journal` is the number reservation journal and `data\uploads.journal` the upload-identity journal: never delete or edit either). New migration: `dotnet ef migrations add <Name> --project src/Beachtro.Infrastructure --output-dir Persistence/Migrations --namespace Beachtro.Infrastructure.Persistence.Migrations`, then move `PosDbContextModelSnapshot.cs` back into `Persistence/Migrations/` if EF places it elsewhere.

**Not production software.** It runs only in the Test profile with synthetic data. No real tax, receipt, printer or Google Drive configuration exists.
