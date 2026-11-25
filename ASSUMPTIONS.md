# Roster planning tool — .NET edition: working assumptions

The owner asked for the roster planning tool to be rebuilt in the company's own
technologies (C#, .NET, Java, CSS), without waiting for IT, on assumptions.
These are the assumptions. Each one is a setting or a seam, so IT can change
it later without a rewrite.

| # | Assumption | Where it can change |
|---|---|---|
| 1 | **C# on .NET 10 (LTS)** for everything: the engine, the data layer and the web app. Java is not used; nothing here needs it. | — |
| 2 | **ASP.NET Core Blazor Web App** with interactive server rendering for the screens. Plain browser access; Citrix is optional. | `src/Roster.Web` |
| 3 | **SQL Server 2022** (2019 also works) through **Entity Framework Core 10**. The schema is created by EF Core migrations, which also produce plain T-SQL scripts for a DBA. | `src/Roster.Data`, connection string |
| 4 | **Single sign-on through Microsoft Entra ID / Active Directory** (OpenID Connect), with roles taken from AD groups: Super User, EMSO, DSO, PA, Store. Windows Authentication is a configuration switch for on-premises AD. A development sign-in exists for local testing only. | `appsettings.json` → `Auth` |
| 5 | **Hosting on IIS (Windows Server) or Azure App Service**, inside the company network. | deployment |
| 6 | **The same behaviour as the current tool** at version `a004032` of the TypeScript app: every rule, timing and order of operations in `docs/Roster-Rules.pdf`. Proved by porting its 1,010 tests and by **parity tests** that run both engines on the same inputs and require identical plans and rosters. | `tests/` |
| 7 | **Data comes in as today** — benchmark, sales, payroll leave extract, UKG contacts, POS statistics — through file uploads in the same formats, behind interfaces, so direct feeds from internal systems can replace the uploads later without touching the engine. The UKG roster export is kept. | `src/Roster.Data/Feeds` |
| 8 | **Floor Display and Load Planner** stay no-login pages reached by a per-store link, with a setting to require a device sign-in instead if security asks for it. | `appsettings.json` → `Kiosk` |
| 9 | **Australian rules stay as the defaults** (award breaks, junior rules, public holidays); they are the values the rules pages edit. | rules pages |
| 10 | The code lives in its own repository, separate from the TypeScript app, which carries on unchanged. | — |

## Layout

Who owns which folder, and the coding rules, are in [CONVENTIONS.md](CONVENTIONS.md).

```
RosterTool-NET/
  Roster.sln
  src/Roster.Engine      the rules engine: planning, rostering, breaks and pay, the load
                         planner, sub-tasks. Pure C#, no database, no web.
  src/Roster.Data        SQL Server: entities, DbContext, migrations, queries, store-scope
                         permissions, file feeds.
  src/Roster.Web         the Blazor web app: sign-in, every page, the kiosk pages, scheduled jobs.
  tests/Roster.Engine.Tests   ported engine tests and the parity tests.
  tests/Roster.Data.Tests     data-layer tests.
  tests/Roster.Web.Tests      page and endpoint tests.
  tools/parity           the script that runs the TypeScript engine on the shared inputs
                         and writes the expected outputs the parity tests compare against.
  tests/parity-corpus    the shared inputs and the TypeScript engine's outputs on them.
```

## Build and test

```
dotnet build Roster.sln
dotnet test Roster.sln
```
