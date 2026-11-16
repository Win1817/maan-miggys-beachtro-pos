# Beachtro POS

Native Windows 11 point-of-sale for Ma'an and Miggy's Beachtro.

- Requirements baseline: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) (never edited)
- Design: [design/](design/) — start with [IMPLEMENTATION_READINESS.md](design/IMPLEMENTATION_READINESS.md)
- Current authorization: [POS-DEC-005](design/POS-DEC-005.md) — milestone 6; earlier: POS-DEC-002 to POS-DEC-004. Test profile only

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

`--data-root <folder>` runs the Test profile against a separate folder (ignored in Production).

## Status

Milestones 0–6 complete: core (money, database, identity, profiles, catalog, numbering, checkout, corrections, backup/restore) plus the screens: owner setup, sign-in, lock, POS, orders with refunds, products, inventory, settings, and receipt printing. Work stops here pending the next decision (POS-DEC-005).

| Project | Purpose |
|---|---|
| `src/Beachtro.Domain` | Exact money kernel (`Financial/`), persisted vocabularies (`Model/`) |
| `src/Beachtro.Application` | Ports, typed results, capability matrix/role templates, sessions, service contracts |
| `src/Beachtro.Infrastructure` | Data paths, lock, EF Core/SQLite schema, migrations, guard triggers, identity, profiles, catalog, numbering journal, checkout, corrections, backup/restore |
| `src/Beachtro.Desktop` | WPF shell and composition root (Test profile only) |
| `tests/*` | Domain fixtures/invariants, real-SQLite schema/constraint tests, architecture rules |

Data lives in `%LocalAppData%\BeachtroPOS-Test\` (`data\numbers.journal` is the number reservation journal: never delete or edit it). New migration: `dotnet ef migrations add <Name> --project src/Beachtro.Infrastructure --output-dir Persistence/Migrations --namespace Beachtro.Infrastructure.Persistence.Migrations`, then move `PosDbContextModelSnapshot.cs` back into `Persistence/Migrations/` if EF places it elsewhere.

**Not production software.** It runs only in the Test profile with synthetic data. No real tax, receipt, printer or Google Drive configuration exists.
