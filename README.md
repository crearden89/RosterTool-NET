# Roster — .NET edition

The roster planning tool rebuilt in C# on .NET 10, with SQL Server and a Blazor web app.
It follows the rules of the TypeScript version exactly; the parity tests prove it.

- [ASSUMPTIONS.md](ASSUMPTIONS.md): the technology choices and where each can change.
- [CONVENTIONS.md](CONVENTIONS.md): how the code is organised, who owns which folder, and the
  JavaScript-to-C# rules that keep the numbers identical.
- [KNOWN-DIFFERENCES.md](KNOWN-DIFFERENCES.md): where this edition deliberately differs from the
  TypeScript version, and [CURRENT-APP-FINDINGS.md](CURRENT-APP-FINDINGS.md): faults found in it.

This guide is for the people who run and deploy the app. Settings are written as they appear in
`appsettings.json` (`Auth:Mode`). As an environment variable, each colon becomes two underscores
(`Auth__Mode`). Secrets (the connection string, the cron secret) go in environment variables,
never in a file that is committed.

## 1. Run it locally

**You need** the .NET 10 SDK (`global.json` asks for 10.0.100 or a later 10.0 SDK) and SQL Server
2022 (2019 also works). The free Developer edition is fine on a workstation.

**The connection string** is the setting `ConnectionStrings:Roster`. No committed file holds one,
and none should. Without it the app starts, but nothing can be read or saved. Keep it in user
secrets or in an environment variable.

User secrets are stored outside the repository, separately for each project. The web app reads
its own in the Development environment, and `dotnet ef` reads those of `src/Roster.Data`. Set it
for both:

```bash
dotnet user-secrets set "ConnectionStrings:Roster" "<connection string>" --project src/Roster.Web
dotnet user-secrets set "ConnectionStrings:Roster" "<connection string>" --project src/Roster.Data
```

An environment variable serves both, and takes precedence over user secrets:

```bash
export ConnectionStrings__Roster="Server=localhost;Database=Roster;Integrated Security=True;TrustServerCertificate=True"
```

```powershell
$env:ConnectionStrings__Roster = "Server=localhost;Database=Roster;Integrated Security=True;TrustServerCertificate=True"
```

For a SQL Server login, put `User Id=<login>;Password=<password>` in place of
`Integrated Security=True`. `TrustServerCertificate=True` accepts a local server's self-signed
certificate. On a real server, give SQL Server a certificate the app trusts and leave it out.
With no connection string set, `dotnet ef` uses
`Server=localhost;Database=Roster;Integrated Security=true;TrustServerCertificate=true`.
Commands time out after `Roster:Data:CommandTimeoutSeconds` (default 120).

**The database.** Build it with the migrations, as a login that may create databases:

```bash
dotnet tool restore        # dotnet-ef, from dotnet-tools.json
dotnet ef database update --project src/Roster.Data --startup-project src/Roster.Data
```

Or a DBA runs `src/Roster.Data/Migrations/Sql/roster-schema.sql` on an empty database. It holds
the same migrations as one T-SQL script: every table; the reference rows the app expects
(positions, load types, non-op categories, admin and reset tasks, and the default rules and
settings); and the migrations history (`__ef_migrations_history`), so a later
`dotnet ef database update` applies only newer migrations. For an upgrade, this writes the script
for a DBA:

```bash
dotnet ef migrations script <last applied migration> --idempotent -o upgrade.sql --project src/Roster.Data --startup-project src/Roster.Data
```

**Seed it with invented data and start it:**

```bash
dotnet run --project src/Roster.Web -- --seed
```

The flag is for the app, so with `dotnet run` it goes after the `--`. With a built or published
app it follows the program: `dotnet Roster.Web.dll --seed`. The seeding finishes before the app
starts serving.

- `--seed` applies the migrations (creating the database if it does not exist), then seeds it.
  If the database already holds stores, it says so and changes nothing.
- `--reseed` drops the database, rebuilds it from the migrations and seeds it. Everything in it is
  lost.
- Both run only in the Development environment. `dotnet run` uses Development, through
  `src/Roster.Web/Properties/launchSettings.json`. In other environments they are skipped with a
  warning, unless `Roster:Seed:AllowOutsideDevelopment` is `true`. Never set that where a real
  database can be reached.
- The data is built around today in Sydney: the previous, current and planning fortnights.
  `Roster:Seed:Today` (yyyy-MM-dd) builds it around another day. `Roster:Seed:OnStartup` = `true`
  seeds without the flag.

Everything in the seed is invented. It has six stores in four states (G001, G102, G215, G218, G327
and G340), with their teams and enough plans, rosters and uploads for every page to show
something. It also has seven users: `superuser` (Super User), `store1` and `store2` (Store), `pa1`
(PA), `director1` (DSO), and `emso1` and `emso2` (EMSO). The console lists these users and each
store's kiosk links. The users are the people behind the seeded reviews, approvals and uploads, and
nobody signs in as them. If `Roster:Users:FirstSuperUser` is set (section 2), the seed adds that
person too.

`dotnet run` serves http://localhost:5066 (the `http` profile).

**Signing in locally.** In Development, `appsettings.Development.json` sets `Auth:Mode` to
`Development`, so signing in goes to `/dev/sign-in`. Type a name, pick a tier, and pick a store.
A store is required for Store and EMSO sign-ins, and an EMSO gets that store's area. For the other
tiers, the store is just the one that opens first. Each name, tier and store makes its own user,
so the audit trail names who did what. To try single sign-on locally, set `Auth:Mode` to `EntraId`
or `Windows` (section 2).

## 2. Sign-in modes

`Auth:Mode` chooses how people sign in. In every mode, the app then matches the person once to
their login and keeps them signed in with its cookie (`.Roster.Session`). They stay signed in until
`Auth:SessionLifetime` (default `08:00:00`) passes without use. Logins are made on **Super User →
Users**. The Username is the person's sign-in name: `jsmith@example.com` for Entra ID, or
`DOMAIN\jsmith` for Windows. Anyone without a login is refused.

### EntraId (the default)

Single sign-on with Entra ID (OpenID Connect). The settings are under `Auth:EntraId`:

| Setting | Value |
|---|---|
| `Instance` | `https://login.microsoftonline.com/` |
| `TenantId` | the directory (tenant) id; set it on the server (`Auth__EntraId__TenantId`) |
| `ClientId` | the app registration's application (client) id (`Auth__EntraId__ClientId`) |
| `CallbackPath` | `/signin-oidc` |
| `SignedOutCallbackPath` | `/signout-callback-oidc` |

The app registration needs:

- Platform **Web**, with two redirect URIs: `https://<site>/signin-oidc`, and
  `https://<site>/signout-callback-oidc`. Signing out of the app also signs out of Entra ID and
  comes back to the second one.
- **ID tokens** turned on ("ID tokens (used for implicit and hybrid flows)"). The app only signs
  people in: it reads the ID token posted back to `/signin-oidc` and calls no other API. It needs
  no client secret or certificate.
- The tiers as app roles, with the values listed in `Auth:TierGroups` (below), assigned to people
  or groups. The defaults are `Roster.SuperUser`, `Roster.EMSO`, `Roster.DSO`, `Roster.PA` and
  `Roster.Store`. Group object ids work too, if the token is set to carry the groups claim.

A person is matched by the token's object id, then by their user principal name. The first sign-in
records the object id on their login.

### Windows

Set `Auth:Mode` to `Windows`. This uses Windows Authentication (Kerberos or NTLM, through
Negotiate) against on-premises Active Directory, at `/account/windows-sign-in`.

Under IIS, turn on Windows Authentication for the site and leave Anonymous Authentication on. The
app asks for the Windows sign-in itself where it is needed. The kiosk pages and the job and data
feed endpoints must stay open to requests that carry no Windows sign-in.

A person is matched by `DOMAIN\name`, then by the name alone.

### Development

The sign-in page from section 1, with no password. The app refuses to start in this mode unless
`ASPNETCORE_ENVIRONMENT` is `Development`.

### Tiers from groups: `Auth:TierGroups`

Each tier (`SuperUser`, `Emso`, `Director` for DSO, `Pa` and `Store`) takes a list of groups. An
entry can be an Entra ID app role value or group object id, or a Windows group
(`CORP\Roster Super Users`, or its SID). When any tier lists a group, the groups decide everyone's
tier:

- Someone in none of the groups is refused.
- Someone in several gets the first of Super User, EMSO, DSO, PA and Store, in that order.
- They still need a login on Super User → Users, which holds a Store user's store and an EMSO's
  area. The first Super User is the exception (below).

`appsettings.json` lists a group for every tier, so groups decide by default. To let the tier saved
on each login decide instead, delete the lists from `appsettings.json` itself. An empty value set in
another settings file or in an environment variable does not remove an entry; it only stops that
entry matching anyone.

### The first Super User

The first Super User sets everyone else up. Name them under `Roster:Users:FirstSuperUser`, with any
of `ObjectId` (Entra ID), `UserPrincipalName` or `WindowsAccount` (`DOMAIN\name`), and optionally a
`DisplayName`. For example:

```bash
Roster__Users__FirstSuperUser__UserPrincipalName=jsmith@example.com
Roster__Users__FirstSuperUser__DisplayName=Jordan Smith
```

Their login is created at their first sign-in, as a Super User. A login that already exists for
them is made a Super User. While groups decide tiers (the default), they must also be in the Super
User role or group. In no group they are refused; in a lower one they get that tier.

## 3. Kiosk pages

The Floor Display (`/floor/<token>`) and the Load Planner (`/load/<token>`) are pages for a tablet
on the wall, with no sign-in. Each store has its own random token, so a link opens one store only.
The data behind the Floor Display (`/api/floor/<token>`) follows the same rule.

**Super User → Kiosk Links** lists every store's links, to open, copy or rotate. Rotating makes a
new token, and the links pinned on that store's tablet stop working at once. The tablet must then
be given the new links.

If the links alone are not enough, set `Kiosk:RequireDeviceSignIn` to `true`. The tablet then signs
in first, through the same sign-in as everyone else, and the link still picks the store.
`Kiosk:DeviceGroups` limits this to accounts in any of the groups listed, written as in
`Auth:TierGroups`. When the list is empty, any account that can sign in counts. A tablet's account
needs no login on Super User → Users.

## 4. Scheduled jobs

Two jobs run inside the app, on cron schedules (minute hour day month weekday) in UTC:

| Job | Default schedule | What it does |
|---|---|---|
| EMSO check | `0 8 * * 3`: 08:00 UTC each Wednesday, the Wednesday evening in Sydney | Tells each EMSO, in the app, about stores in their area that have no approved roster for the planning period |
| Keep-alive | `0 17 * * *`: 17:00 UTC every day | Reads the stores and records that it ran, so a database that pauses when idle stays awake. Harmless on one that never pauses |

The settings are under `Roster:Jobs`:

- `EmsoCheckSchedule` and `KeepAliveSchedule` change the schedules.
- `RunScheduler` (default `true`, read at start-up) runs the jobs in the app. **On more than one
  server, leave it on for one server only**, or each job runs once per server. Or turn it off
  everywhere and have a scheduler call the endpoints.
- `CronSecret` protects the endpoints, `GET /api/cron/emso-check` and `GET /api/cron/keep-alive`.
  A scheduler calls them with the header `Authorization: Bearer <secret>`. With no secret set,
  both endpoints refuse every request. Set it as an environment variable,
  `Roster__Jobs__CronSecret` (`CRON_SECRET` is read too).

```bash
curl -H "Authorization: Bearer $CRON_SECRET" https://<site>/api/cron/emso-check
```

The in-app scheduler runs only while the app is running. A run missed while the app is stopped, or
unloaded because it was idle, is not made up afterwards. Keep the app running (section 5), or let a
scheduler call the endpoints; a request also starts an app that has been unloaded. A Super User can
also run the EMSO check by hand, on **Super User → EMSO Managers** ("Run check now").

## 5. Deploying to IIS or Azure App Service

**Publish** the web project:

```bash
dotnet publish src/Roster.Web -c Release -o publish
```

The server needs the ASP.NET Core 10 runtime. For IIS, install the .NET 10 Hosting Bundle.

**Settings on the server.** When `ASPNETCORE_ENVIRONMENT` is not set, the environment is
`Production`. Give the connection string and the cron secret as environment variables: on IIS,
for the application pool or the machine; on App Service, in the app's configuration. A connection
string named `Roster` in App Service's configuration also reaches the app as
`ConnectionStrings:Roster`. Set `Auth:Mode` and its settings for the sign-in you use, and
`Roster:Users:FirstSuperUser`.

**The database.** Run `roster-schema.sql` on an empty database, or run
`dotnet ef database update` against the server (section 1). The app never changes the schema
itself; only the migrations and the development seeder do.

**IIS:**

- Use an application pool with **.NET CLR version: No Managed Code**.
- Set **Load User Profile: True**, so the keys that protect the sign-in cookie survive a recycle of
  the application pool. Without it, everyone is signed out at each recycle.
- Set **Start Mode: AlwaysRunning** and **Idle Time-out: 0** on the pool, and **Preload Enabled:
  True** on the site (this needs the Application Initialization feature). The app, and its
  scheduler, then keep running while nobody is using it.
- Turn on the **WebSocket Protocol** feature. The screens are Blazor Server pages that keep a live
  connection to the server, and that connection should run over WebSockets.
- Add an HTTPS binding with a certificate. The app redirects HTTP to HTTPS and, outside
  Development, sends HSTS.
- For Windows sign-in, see section 2.

**Azure App Service:**

- Turn on **Web sockets**, **Always On** (for the scheduler) and **HTTPS Only**.
- On Linux, set the app setting `ASPNETCORE_FORWARDEDHEADERS_ENABLED` to `true`. HTTPS ends in
  front of the app, and the app must still see `https` to build the sign-in's return address.

**More than one server.** The live connections need session affinity: ARR affinity on App
Service, or sticky sessions on the load balancer. On an IIS farm, each server keeps its own
sign-in keys, because the app sets up no shared key store. Someone moved to another server has to
sign in again. Run the scheduler on one server only (section 4).

**Time zones and formats.** The server's own time zone does not matter. The app works out today in
Sydney itself, and each store's local time from its state (`Australia/Sydney`, `Australia/Perth`
and so on). The pages show dates and numbers in en-AU format whatever the server's culture. The app
finds time zones by these names, and sorts text with ICU as the TypeScript version did. On Windows,
.NET does both through ICU, which Windows includes from Windows Server 2022 (and Windows 10
version 1903) on, so use Windows Server 2022 or later. Leave invariant globalization and NLS mode
off.

**The data feed.** Sync jobs send CSV files to `POST /api/ingest/<type>`, with
`Authorization: Bearer <feed token>`; `?mode=validate` checks a file without saving it. The tokens
are issued on **Super User → Data Feed**, which also describes each file.

## 6. Tests

```bash
dotnet test Roster.sln --filter "Category!=Slow&Category!=SqlServer"
```

This is the everyday suite: the engine, the data layer on SQLite in memory, the pages, and the
parity tests on the committed corpus. A plain `dotnet test Roster.sln` runs the slow tests too.
The full-corpus parity tests among them fail until the full corpus has been generated (below).

**SQL Server tests** (`Category=SqlServer`) cover what only SQL Server shows: the real migrations,
the T-SQL that EF Core writes, and its constraints. They need a SQL Server (2019 or later) and a
login that may create databases. Without one they are skipped, not failed:

```bash
export ROSTER_TEST_SQLSERVER="Server=localhost,1433;User Id=<login>;Password=<password>;TrustServerCertificate=True"
export ROSTER_TEST_ID=Dev     # databases are named Roster_<ID>_<8 hex digits>
tests/Roster.Data.Tests/Infrastructure/run-sqlserver-tests.sh
```

The variable holds a connection string to the server, with no database. Each test class creates
and migrates a database of its own, and drops it after its last test. See
`tests/Roster.Data.Tests/Infrastructure/README.md`.

**Parity tests** run the C# engine on inputs recorded from the TypeScript engine and require the
same outputs. They cover eight entry points: pay, the shaper, the painter, the fortnight roster,
the till, Stage 1, sub-tasks and the load planner.

- **The committed corpus** is `tests/parity-corpus`: 2,445 representative calls, one gzipped JSON
  Lines file per entry point. Its `manifest.json` names the TypeScript commit they came from,
  `a004032`. It runs in the everyday suite.
- **The full corpus** holds every distinct call: 109,592 cases, too large to commit. Generate it
  from a checkout of the TypeScript version at `a004032` with its packages installed (`npm ci`).
  This takes about twelve minutes:

  ```bash
  cd <typescript checkout>
  npx tsx@4.20.3 <this repository>/tools/parity/dump.ts --full <folder> --no-select
  ```

  `--no-select` writes the full corpus only. Without it, the dumper also picks the committed corpus
  again. Without `--full`, the full corpus goes to `roster-parity-full` in the temp folder, which is
  also where the tests look by default. Then run the slow parity tests against it:

  ```bash
  PARITY_FULL_CORPUS=<folder> dotnet test tests/Roster.Engine.Tests --filter "FullyQualifiedName~Parity&Category=Slow"
  ```

  `PARITY_CASE=<id>,<id>` narrows a run to a few cases.

**The result:** all 109,592 cases are identical across the eight entry points under the strict
comparer: pay 11,205; shaper 1,992; painter 6,251; fortnight 67; till 86,780; Stage 1 1,981;
sub-tasks 988; load 328. The comparer compares the JSON exactly, with a missing field counted as
null. Its only leeway is at five listed fields, where the TypeScript leaves the field out and the
C# writes `false`. The TypeScript app reads each of those fields only as true or false, so the two
mean the same. `tests/Roster.Engine.Tests/Parity/ParityTolerances.cs` lists the five, with the
reason for each. To run that check again, set `PARITY_TOLERANCE=permissive` and
`PARITY_TOLERANCE_LOG=<folder>`.

## 7. Where the code is

| Folder | What is in it |
|---|---|
| `src/Roster.Engine` | The rules as plain C#: planning, rostering, breaks and pay, tills, the load planner, sub-tasks, overview and analysis figures, leave and variance. No database and no web. |
| `src/Roster.Data` | SQL Server through EF Core: the tables (`Entities/`, `Configurations/`), the store-scope rules (`Scope/`), the migrations and the DBA script (`Migrations/`), sign-in users (`Users/`), the services that load inputs, run the engine and save results (`Services/`), file feeds (`Feeds/`), the development seed (`Seed/`) and the jobs' database work (`Jobs/`). |
| `src/Roster.Web` | The Blazor web app: start-up (`Program.cs`, `ShellSetup.cs`), sign-in (`Auth/`), the shell and shared components (`Components/`), the kiosk link check (`Kiosk/`), and the pages and endpoints by area (`Features/<Area>/`). |
| `tests/` | xUnit tests for each project, and the committed parity corpus. |
| `tools/parity` | The dumper that records the TypeScript engine's calls. |
| `tools/tailwind` | The stylesheet compiler. The compiled `src/Roster.Web/wwwroot/css/app.css` is committed, so a build needs no compiler. After changing a page's classes, run `install.sh` (or `install.ps1`) once. Then build `src/Roster.Web`, which recompiles the file, and commit it. |

[CONVENTIONS.md](CONVENTIONS.md) has the full map, who owns which folder, and the rules every port
follows.
