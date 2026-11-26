# Conventions

How the code is organised and who owns what. Every folder has one owner, so many
people (or agents) can work at the same time without editing the same files.

## Projects

| Project | What goes in it | References |
|---|---|---|
| `src/Roster.Engine` | The rules, as pure code: no database, no web, no clock or files unless they are passed in. | — |
| `src/Roster.Data` | SQL Server: entities, `RosterDbContext`, migrations, queries, store-scope permissions; services that load inputs, call the engine and save results; file feeds; seed data; scheduled jobs. | Engine |
| `src/Roster.Web` | Blazor pages and components, sign-in, the kiosk pages, HTTP endpoints. | Engine, Data |
| `tests/Roster.*.Tests` | xUnit tests for the matching project. | — |

No new projects; add folders inside these.

## Names

- Namespace = project + folder: `Roster.Engine.Pay`, `Roster.Data.Feeds`, `Roster.Web.Features.Plan`.
- No folder (so no namespace segment) may be called `Roster`: inside `Roster.Web.Features.*`, a namespace
  `Roster.Web.Features.Roster` would capture every `Roster.` name, and Razor `@using Roster....` lines break.
  Use `RosterPage`, `RosterOps` and so on. For the same reason a class never has the same name as its
  namespace's last part (`PlanShaper` in `...Shaper`, not `Shaper`).
- A file that ports a TypeScript file starts with `// Port of <path in the TS app>.`
  A file with no TypeScript counterpart starts with `// New: <why>.`
- C# style (PascalCase types and members). A ported function keeps its TypeScript name
  in PascalCase (`buildFortnight` → `BuildFortnight`), so the two read side by side.
- Database tables and columns keep the TypeScript app's snake_case names
  (`generated_shifts.start_min`), so the T-SQL reads like the original SQL.

## Who owns what

The TypeScript app is the source of truth for behaviour: `src/...` paths below are in
that app (version `1e4f39e`, which carries the owner's decisions of 26 November 2026).

### Engine — `src/Roster.Engine`, tests in the same folder names under `tests/Roster.Engine.Tests`

| Folder | Owner | Ports |
|---|---|---|
| `Core/` | E1 | `lib/grid`, `period`, `dates`, `storeTime`, `storeDay`, `constants`, `fortnight`, `fixedCycle`, `closeRules`, `weekendWindow`, `roster/display`, `stage2/rules`, the engine's shared types, JavaScript-semantics helpers |
| `Pay/` | E1 | `lib/payroll/*` |
| `Planning/` | E1 | `lib/stage1/*`, `lib/plan/placement`, `dayHours`, `benchmarkSplit`, `plannedHours`, `tradeCurve`, `loadRunning`, `dayCheck`, `lib/review/checks`, `lib/resetTasks`, `lib/adminTasks/*` (pure parts) |
| `SubTasks/Catalogue.cs` | E1 | `lib/subtasks/catalogue` |
| `Rostering/Painter/` | E5 | `lib/stage2/engine`, `coverage`, `order`, `priority` (pure parts) |
| `Rostering/Shaper/` | E6 | `lib/stage2/shaper`, `shiftCost`, `handoverCut`, `breakTime`, `planSlots` (pure parts) |
| `Rostering/Fortnight/` | E2 | `lib/stage2/fortnight`, `breakGap`, `heldPlan`, `compute` (pure parts), the G999 fixtures and audit |
| `Till/`, `Load/`, `RosterOps/`, `SubTasks/` (except `Catalogue.cs`) | E3 | `lib/till/*`, `lib/load/*`, `lib/roster/*` (except `display`), `lib/subtasks/*` (except `catalogue`) |
| `Overview/`, `Analysis/` | P4 | `lib/overview/*`, `lib/analysis/*` (pure parts) |
| `Leave/` | P6 | `lib/leave/*` (pure parts) |
| `Variance/` | P8 | `lib/variance/*` |
| `Walk/` | P9 | `lib/walk/*` |
| `tests/Roster.Engine.Tests/Parity/`, `tests/parity-corpus/`, `tools/parity/` | E4 | the parity harness: the TypeScript engine's outputs on shared inputs, compared exactly |

### Data — `src/Roster.Data`, tests under `tests/Roster.Data.Tests`

| Folder | Owner | Ports |
|---|---|---|
| root (`RosterDbContext*.cs`, `DataModules.cs` with `IDataModule` and `AddRosterData`), `Entities/`, `Configurations/`, `Migrations/`, `Scope/`, `Snapshots/`, `Users/` | D | `supabase/migrations/*` (tables, constraints, row-level security as store-scope rules), `lib/storeScope`, `authz`, `access`, `reportingScope`, `planSnapshots`, `auth` |
| `Services/` | D2 | the SQL functions (`approve_roster_day`, `overview_rollup`, `leave_period_ends`, `is_public_holiday`, `bump_cron_run`), the loaders around the engine (`stage1/compute`, `stage2/compute`, `planSlots`, `priority`, `refdata`, `requestCache`, `payroll/settings`, `payroll/dayType`), `lib/review/gate`, `notify`, `server`, `plan/approval`, `roster/approval`, `roster/drift`, `lib/notifications` (database parts) |
| `Feeds/` | D3 | `lib/csv/*`, `plan/curveSource`, `api/ingest`, `api/uploads`, `api/export/ukg`, `api/print/ukg-roster` |
| `Seed/`, `Jobs/`, `tests/Roster.Data.Tests/Infrastructure/` | D4 | invented seed data, the development seeder, `lib/cron`, `lib/emso` (scheduled checks), SQL Server test fixtures |

### Web — `src/Roster.Web`, tests under `tests/Roster.Web.Tests/<Area>`

| Folder | Owner | Ports |
|---|---|---|
| root (`Program.cs`, `appsettings*.json`), `Components/` (`App`, `Routes`, `Layout/`, `Ui/`), `Auth/`, `Kiosk/` (link checks), `Styles/`, `wwwroot/`, `Features/FeatureModules.cs`, `Features/Account/` | W | `app/layout`, `login`, `error`, `components/ui/*`, `components/nav/*`, `globals.css` |
| `Features/Plan/` | P1 | `app/(app)/plan/generate`, `components/plan/*`, `lib/plan/pageFigures`, `generateParts` |
| `Features/RosterPage/` (including the shared tile grid in `Features/RosterPage/Grid/`) | P2 | `app/(app)/plan/roster`, `components/roster/*`, `api/roster/held` |
| `Features/Review/`, `Features/Notifications/` | P3 | `app/(app)/plan/review`, `app/(app)/notifications` |
| `Features/Overview/`, `Features/Analysis/` | P4 | `app/(app)/overview`, `app/(app)/analysis`, `analysis/notes`, `app/page` |
| `Features/Team/`, `Features/Weekends/` | P5 | `app/(app)/setup/staff`, `setup/requests`, `setup/weekends` |
| `Features/Sales/`, `Features/NonOp/`, `Features/LeaveLiability/` | P6 | `app/(app)/plan/sales`, `plan/non-op`, `admin/leave-liability`, `components/nonop/*` |
| `Features/Rules/`, `Features/Process/` | P7 | `app/(app)/admin/rules/*`, `break-rules`, `labour-rules`, `rostering-rules`, `planning-order`, `roster-order`, `admin/process/*` |
| `Features/Admin/` | P8 | `app/(app)/admin/store-attributes`, `special-trading-hours`, `calendars`, `load-types`, `non-op-categories`, `pay-rates`, `kiosk-links`, `users`, `emso`, `views`, `data-feed`, `uploads`, `variance-report`, `till-preview` |
| `Features/Floor/`, `Features/LoadPlanner/` | P9 | `app/floor/[token]`, `app/load/[token]`, `api/floor/[token]` |
| `Features/Feeds/` | D3 | the HTTP endpoints for `api/uploads`, `api/ingest`, `api/export/ukg`, `api/print/ukg-roster` |
| `Features/Jobs/` | D4 | scheduled jobs as hosted services, `api/cron/*` |

### Shared files (one owner each)

| File | Owner |
|---|---|
| `Roster.sln`, `Directory.Build.props`, `global.json`, `.gitignore`, this file | O (orchestrator) |
| `src/Roster.Engine/Roster.Engine.csproj`, `tests/Roster.Engine.Tests/*.csproj` | E1 |
| `src/Roster.Data/Roster.Data.csproj`, `tests/Roster.Data.Tests/*.csproj`, `dotnet-tools.json` | D |
| `src/Roster.Web/Roster.Web.csproj`, `tests/Roster.Web.Tests/*.csproj` | W |

Need a change in a folder or file you don't own? Ask its owner; don't edit it and don't copy it.

## Behaviour must match the TypeScript app exactly

JavaScript and C# differ in ways that change numbers. Use the helpers in `Roster.Engine.Core`:

- Numbers are `double`, never `decimal`.
- `Math.round` in JavaScript rounds halves up (toward +∞); C#'s `Math.Round` rounds halves to even.
  Use the JavaScript-semantics helper, never `Math.Round`.
- `toFixed`, `parseInt`, `parseFloat`, `Number(...)` and string concatenation of numbers have
  JavaScript semantics; use the helpers.
- Sorting must be stable: `OrderBy`/`ThenBy`, never `List.Sort` or `Array.Sort` with a comparer.
- `Map` and `Set` iterate in insertion order; `Dictionary` does not promise that. Use the
  ordered collections in Core where order is observable.
- `localeCompare` is not ordinal comparison; use the helper.
- `%` with negative numbers, `Math.floor` versus truncation, and integer division differ.
- `NaN`, `undefined` and `null` each need an explicit decision that mirrors the TypeScript code.

## Tests

- Port the TypeScript tests for your code with the same cases and expected values.
- Mark tests that take more than a few seconds `[Trait("Category", "Slow")]` and tests that need
  a SQL Server `[Trait("Category", "SqlServer")]`. The landing check skips both.
- Parity tests (E4) run the same inputs through both engines and require identical results.

## Pages

- A page area is `Features/<Area>/`: its `.razor` pages (same routes as the TypeScript app, for
  example `@page "/plan/generate"`), components, view models, an `I<Area>Service` with its
  implementation, and an `<Area>Module : IFeatureModule` that registers them. Modules are found
  at start-up, so adding an area never touches `Program.cs`.
- Data areas register the same way: each of `Services/`, `Feeds/`, `Seed/` has an `IDataModule`, and
  `AddRosterData` (called once in `Program.cs`) registers the `DbContext` and every module.
- Server actions in the TypeScript app (`actions.ts`) become methods on the area's service, with
  the same permission checks in the same places.
- Styling uses the same Tailwind classes as the TypeScript app, so pages look the same.
