// New: the migrations and the DBA script against a real SQL Server 2022, and the store-scope
// rules and code cascades where only SQL Server can prove them (real foreign keys, real
// T-SQL translation of the query filters).
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;
using Roster.Data.Users;

namespace Roster.Data.Tests.Migrations;

[Trait("Category", "SqlServer")]
public sealed class SqlServerSchemaTests
{
    private const string TableCount =
        "SELECT COUNT(*) FROM sys.tables WHERE name <> '__ef_migrations_history'";

    [SqlServerFact]
    public void The_migrations_build_every_table()
    {
        using var db = new SqlServerDatabase();
        Assert.Equal(70, db.Scalar<int>(TableCount));
        Assert.Equal(8, db.Scalar<int>("SELECT COUNT(*) FROM sys.computed_columns WHERE is_persisted = 1"));
        Assert.Equal(160, db.Scalar<byte>("SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()"));
    }

    [SqlServerFact]
    public void The_dba_script_builds_the_same_database()
    {
        using var db = new SqlServerDatabase(migrate: false);
        var batches = System.Text.RegularExpressions.Regex.Split(
            File.ReadAllText(ScriptTests.ScriptPath()), @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
            db.Exec(db.Name, batch);
        Assert.Equal(70, db.Scalar<int>(TableCount));
        Assert.Equal(2, db.Scalar<int>("SELECT COUNT(*) FROM [__ef_migrations_history]")); // InitialCreate, ReferenceData
    }

    [SqlServerFact]
    public void Filters_and_write_checks_hold_on_sql_server()
    {
        using var db = new SqlServerDatabase();
        var org = TestOrg.Create(db.System);

        using (var emso = db.As(AccessContext.ForUser(org.EmsoA1)))
        {
            Assert.Equal(2, emso.Stores.Count());
            Assert.Equal(2, emso.EmployeeLeave.Count());
            Assert.Equal(2, emso.ShiftTaskBlocks.Count());
            Assert.Equal(1, emso.AppUsers.Count());
            emso.SalesOverrides.Add(new SalesOverride { StoreId = org.S3, Date = new DateOnly(2026, 8, 4) });
            Assert.Throws<ScopeViolationException>(() => emso.SaveChanges());
        }
        using (var pa = db.As(AccessContext.ForUser(org.PaR1)))
        {
            Assert.Equal(4, pa.Employees.Count());
            Assert.Equal(0, pa.UploadBatches.Count());
        }
        using (var kiosk = db.As(AccessContext.ForKiosk(org.S3)))
        {
            Assert.Single(kiosk.LoadAssignments);
            kiosk.AdminTaskChecks.Add(new AdminTaskCheck { StoreId = org.S3, Date = new DateOnly(2026, 8, 4), TaskName = "End of Day" });
            kiosk.SaveChanges();
        }
    }

    [SqlServerFact]
    public async Task Deleting_a_team_member_does_what_postgres_did()
    {
        using var db = new SqlServerDatabase();
        var org = TestOrg.Create(db.System);
        var day = new DateOnly(2026, 8, 5);
        Guid newcomer;
        using (var sys = db.System())
        {
            var e = new Employee { StoreId = org.S1, Name = "4-Short, Stay", Role = Roles.Sa };
            sys.Employees.Add(e);
            sys.SaveChanges();
            newcomer = e.Id;
            sys.WeekendMarkers.Add(new WeekendMarker { EmployeeId = e.Id, WeekendStart = new DateOnly(2026, 8, 8), SatOff = true });
            sys.StoreWalkNotes.Add(new StoreWalkNote { StoreId = org.S1, Note = "Check the bins", RaisedDate = day, AssignedEmployeeId = e.Id, AssignedName = e.Name });
            sys.SaveChanges();
        }

        using (var me = db.As(AccessContext.ForUser(org.StoreS1)))
        {
            // On a published roster: refused, as the foreign key refused it.
            Assert.NotNull(await me.DeleteEmployeeAsync(org.EmployeeOf[org.S1]));
            Assert.Null(await me.DeleteEmployeeAsync(newcomer));
        }
        using var check = db.System();
        Assert.False(check.Employees.Any(e => e.Id == newcomer));
        Assert.False(check.WeekendMarkers.Any(w => w.EmployeeId == newcomer));            // cascade
        var note = check.StoreWalkNotes.Single(n => n.Note == "Check the bins");
        Assert.Null(note.AssignedEmployeeId);                                              // set null
        Assert.Equal("4-Short, Stay", note.AssignedName);
    }

    [SqlServerFact]
    public async Task Deleting_a_store_and_a_login_on_sql_server()
    {
        using var db = new SqlServerDatabase();
        var org = TestOrg.Create(db.System);
        using (var sys = db.System())
        {
            sys.StoreAttributes.Add(new StoreAttributes { StoreId = org.S1, PairStoreId = org.S4 });
            sys.SaveChanges();
        }
        using (var super = db.As(AccessContext.ForUser(org.Super)))
        {
            Assert.NotNull(await super.DeleteStoreAsync(org.S1)); // has a Store login
            Assert.Null(await super.DeleteStoreAsync(org.S4));
        }
        using (var check = db.System())
        {
            Assert.False(check.Stores.Any(s => s.Id == org.S4));
            Assert.False(check.Employees.Any(e => e.StoreId == org.S4));
            Assert.Null(check.StoreAttributes.Single(a => a.StoreId == org.S1).PairStoreId);
        }

        var contexts = new TestContexts2(db, AccessContext.ForUser(org.Super));
        var admin = new UserAdmin(contexts, new StoreScope(contexts, contexts));
        using (var sys = db.System())
        {
            sys.RosterReviews.Add(new RosterReview { StoreId = org.S2, PeriodStart = new DateOnly(2026, 8, 3), SubmittedBy = org.EmsoA1.Id, DecidedBy = org.EmsoA1.Id });
            sys.SaveChanges();
        }
        Assert.True((await admin.DeleteAsync(org.EmsoA1.Id)).Ok);
        using var after = db.System();
        Assert.Null(after.RosterReviews.Single().DecidedBy);
        Assert.False(after.Notifications.Any(n => n.UserId == org.EmsoA1.Id));
    }

    private sealed class TestContexts2(SqlServerDatabase db, AccessContext current) : IRosterDbContextFactory, IAccessContextAccessor
    {
        public AccessContext Current => current;
        public RosterDbContext Create() => db.As(current);
        public RosterDbContext Create(AccessContext access) => db.As(access);
        public RosterDbContext CreateSystem(string reason) => db.As(AccessContext.System(reason));
    }
}
