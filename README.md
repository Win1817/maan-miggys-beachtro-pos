# Beachtro POS

Native Windows 11 point-of-sale for Ma'an and Miggy's Beachtro.

- Requirements baseline: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) (never edited)
- Design: [design/](design/) — start with [IMPLEMENTATION_READINESS.md](design/IMPLEMENTATION_READINESS.md)
- Current authorization: [POS-DEC-002](design/POS-DEC-002.md) — milestones 0–3, Test profile only

## Build

Requires the .NET SDK pinned in `global.json` (10.0.404, latest patch roll-forward).

```powershell
dotnet tool restore
dotnet build
dotnet test
```

Package versions are pinned centrally in `Directory.Packages.props`; restores are locked by `packages.lock.json`.

## Status

Milestones 0–3 complete (setup, skeleton, money kernel, database). Work stops here pending the next decision (POS-DEC-002).

| Project | Purpose |
|---|---|
| `src/Beachtro.Domain` | Exact money kernel (`Financial/`), persisted vocabularies (`Model/`) |
| `src/Beachtro.Application` | Ports and runtime profile policy |
| `src/Beachtro.Infrastructure` | Data paths, single-instance lock, EF Core/SQLite schema, migrations, guard triggers |
| `src/Beachtro.Desktop` | WPF shell and composition root (Test profile only) |
| `tests/*` | Domain fixtures/invariants, real-SQLite schema/constraint tests, architecture rules |

Data lives in `%LocalAppData%\BeachtroPOS-Test\`. New migration: `dotnet ef migrations add <Name> --project src/Beachtro.Infrastructure --output-dir Persistence/Migrations --namespace Beachtro.Infrastructure.Persistence.Migrations`, then move `PosDbContextModelSnapshot.cs` back into `Persistence/Migrations/` if EF places it elsewhere.

**Not production software.** It runs only in the Test profile with synthetic data. No real tax, receipt, printer or Google Drive configuration exists.
