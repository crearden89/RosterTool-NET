# SQL Server test fixtures

Most data tests run on SQLite in memory (`Support/TestDatabase.cs`): fast, and part of every
build. Some things only SQL Server can show: the real migrations, the T-SQL that EF Core writes
for a query, foreign keys and check constraints as SQL Server enforces them. Tests of those
use the fixtures in this folder.

## Running them

They need a SQL Server (2019 or later) and a login that may create databases (`dbcreator`).
Nothing here holds a password: the connection string comes from the environment.

| Variable | What |
|---|---|
| `ROSTER_TEST_SQLSERVER` | A connection string to the **server** (no database name; one given is ignored), e.g. `Server=localhost,1433;User Id=...;Password=...;TrustServerCertificate=True` |
| `ROSTER_TEST_ID` | Who is running them: letters and digits, e.g. `D4`. Databases are named `Roster_<ID>_<8 hex digits>`. Default `Dev`. |

```bash
export ROSTER_TEST_SQLSERVER="Server=localhost,1433;User Id=<login>;Password=<password>;TrustServerCertificate=True"
export ROSTER_TEST_ID=D4
tests/Roster.Data.Tests/Infrastructure/run-sqlserver-tests.sh                     # every SqlServer test
tests/Roster.Data.Tests/Infrastructure/run-sqlserver-tests.sh "FullyQualifiedName~Jobs"   # some of them
```

The script runs `dotnet test --filter Category=SqlServer` on the data and web test projects
(extra arguments go to `dotnet test`). On the team machine, put it through the build queue:
`$S/master/heavy.sh tests/Roster.Data.Tests/Infrastructure/run-sqlserver-tests.sh`.

Without `ROSTER_TEST_SQLSERVER` the tests are **skipped**, not failed, and the landing check
never runs them (it filters `Category=SqlServer` out).

**After a reboot** the local server has to be started again before these tests can run
(on the team machine: `/opt/roster-sqlserver/start.sh`; check with
`pgrep -f /opt/mssql/bin/sqlservr`). A stopped server shows up as connection errors
(error 40 / "server was not found"), not as skipped tests.

## Writing one

```csharp
[Trait("Category", "SqlServer")]
public sealed class MyTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    // Every test starts from the database as the migrations left it.
    public Task InitializeAsync() => db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [SqlServerFact]
    public async Task Something_only_sql_server_shows()
    {
        await using var sys = db.System();                  // as the server
        await using var store = db.As(someScopedUser);      // as a signed-in user (filters apply)
        var factory = db.Factory();                         // IRosterDbContextFactory for services
        ...
    }
}
```

- **`[Trait("Category", "SqlServer")]`** on the class, and **`[SqlServerFact]`** /
  **`[SqlServerTheory]`** on the tests (they skip when no server is configured). A plain
  `[Fact]` in such a class would run without a server and fail: keep server-free tests in
  another class.
- **`SqlServerDatabaseFixture`** (one per test class, `IClassFixture`): creates
  `Roster_<ID>_<random>`, applies the EF Core migrations (exactly what `dotnet ef database
  update` does), and drops the database when the class is done. Contexts come from
  `System()`, `As(AccessContext)` / `As(ScopedUser)`, or `Factory()`. `Options` gives the
  `DbContextOptions` (configured as the app configures SQL Server, retry strategy included).
  Override `Clock` to give the contexts a fixed `TimeProvider`.
- **`ResetAsync()`**: deletes every row the tests wrote and puts back the rows the migrations
  inserted, in one transaction (a few tens of milliseconds). Right after migrating, the fixture
  copies every table that holds rows into the `reset_checkpoint` schema; a reset deletes from
  every table, children before parents (from `sys.foreign_keys`; foreign keys on a cycle are
  switched off for the batch and checked again at its end), then copies the checkpoint back.
  `ReferenceTables` lists the tables it restores.
- To share one database between several classes, put them in one xUnit collection with an
  `ICollectionFixture<SqlServerDatabaseFixture>`; reset in each test as above.
- For data every test of a class needs, subclass the fixture and override `SeedAsync()`
  (written once after the migrations). A reset goes back to the migrated state, before that
  seed: either re-seed after resetting, or don't reset in that class.

## Housekeeping

- Each class's database is dropped at the end, even when its tests fail. A test run that is
  killed leaves its databases behind; the next run (same `ROSTER_TEST_ID`) drops that ID's
  databases older than six hours before it starts.
- `SqlServerDatabaseFixture.DropStaleDatabasesAsync(TimeSpan)` drops them on demand.
- xUnit runs test classes in parallel, so several databases may exist at once during a run.

## Files

| File | What |
|---|---|
| `SqlServerTestSettings.cs` | The environment variables, database names, connection strings |
| `SqlServerDatabaseFixture.cs` | The per-class database: create, migrate, reset, drop |
| `DatabaseReset.cs` | The checkpoint and the reset batch |
| `SqlServerFactAttribute.cs` | `[SqlServerFact]` and `[SqlServerTheory]` |
| `SqlServerFixtureTests.cs` | Tests of the fixture itself |
| `run-sqlserver-tests.sh` | Runs every SqlServer test |
