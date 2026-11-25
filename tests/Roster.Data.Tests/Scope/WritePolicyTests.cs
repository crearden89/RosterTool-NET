// New: the write side of the policies (WITH CHECK, 0006/0044, and authz.ts's guards),
// checked on every SaveChanges, tier by tier.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Scope;

public sealed class WritePolicyTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TestOrg _org;

    public WritePolicyTests() => _org = TestOrg.Create(_db);

    public void Dispose() => _db.Dispose();

    private static readonly DateOnly Day = new(2026, 8, 4);

    /// <summary>Insert a sales override for a store as someone; null when saved, else the refusal.</summary>
    private string? WriteStoreRow(AccessContext who, Guid storeId)
    {
        using var ctx = _db.As(who);
        ctx.SalesOverrides.Add(new SalesOverride { StoreId = storeId, Date = Day, Closed = true });
        try { ctx.SaveChanges(); return null; }
        catch (ScopeViolationException e) { return e.Message; }
    }

    [Fact]
    public void Store_user_writes_their_own_store_and_no_other()
    {
        var me = AccessContext.ForUser(_org.StoreS1);
        Assert.Null(WriteStoreRow(me, _org.S1));
        Assert.StartsWith(ScopeMessages.DeniedScope, WriteStoreRow(me, _org.S2));
        Assert.StartsWith(ScopeMessages.DeniedScope, WriteStoreRow(me, _org.S3));
    }

    [Fact]
    public void Emso_writes_every_store_in_their_area_and_nothing_outside_it()
    {
        var me = AccessContext.ForUser(_org.EmsoA1);
        Assert.Null(WriteStoreRow(me, _org.S1));
        Assert.Null(WriteStoreRow(me, _org.S2));
        Assert.StartsWith(ScopeMessages.DeniedScope, WriteStoreRow(me, _org.S3));
        // A store with no area never falls into an area scope.
        Assert.StartsWith(ScopeMessages.DeniedScope, WriteStoreRow(me, _org.S4));
    }

    [Theory]
    [InlineData(Tier.Pa)]
    [InlineData(Tier.Director)]
    public void Pa_and_dso_see_everything_and_change_nothing(Tier tier)
    {
        var me = AccessContext.ForUser(tier == Tier.Pa ? _org.PaR1 : _org.DirectorD1);
        foreach (var store in _org.AllStores)
            Assert.StartsWith(ScopeMessages.DeniedReadOnly, WriteStoreRow(me, store));
    }

    [Fact]
    public void Super_user_writes_every_store()
    {
        foreach (var store in _org.AllStores)
            Assert.Null(WriteStoreRow(AccessContext.ForUser(_org.Super), store));
    }

    [Fact]
    public void Nobody_signed_in_writes_nothing() =>
        Assert.StartsWith(ScopeMessages.DeniedAuth, WriteStoreRow(AccessContext.Anonymous, _org.S1));

    [Fact]
    public void Moving_a_row_out_of_scope_is_refused_on_both_sides()
    {
        // The store user's own row, re-pointed at another store: the new value is checked too.
        Assert.Null(WriteStoreRow(AccessContext.ForUser(_org.StoreS1), _org.S1));
        using var ctx = _db.As(AccessContext.ForUser(_org.StoreS1));
        var row = ctx.DayPlans.Single();
        ctx.Remove(row);
        ctx.SaveChanges();
        ctx.DayPlans.Add(new DayPlan { StoreId = _org.S2, Date = row.Date });
        Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void Employee_owned_rows_check_the_employees_store()
    {
        using (var mine = _db.As(AccessContext.ForUser(_org.StoreS1)))
        {
            mine.WeekendMarkers.Add(new WeekendMarker { EmployeeId = _org.EmployeeOf[_org.S1], WeekendStart = new DateOnly(2026, 8, 8), SatOff = true });
            mine.SaveChanges();
        }
        using var other = _db.As(AccessContext.ForUser(_org.StoreS1));
        other.WeekendMarkers.Add(new WeekendMarker { EmployeeId = _org.EmployeeOf[_org.S2], WeekendStart = new DateOnly(2026, 8, 8), SatOff = true });
        var e = Assert.Throws<ScopeViolationException>(() => other.SaveChanges());
        Assert.StartsWith(ScopeMessages.DeniedScope, e.Message);
    }

    [Fact]
    public void A_new_team_member_and_their_availability_save_together()
    {
        using var ctx = _db.As(AccessContext.ForUser(_org.EmsoA1));
        var e = new Employee { StoreId = _org.S2, Name = "4-New, Starter", Role = Roles.Sa };
        ctx.Employees.Add(e);
        ctx.EmployeeAvailability.AddRange(Enumerable.Range(0, 7).Select(d => new EmployeeAvailability { EmployeeId = e.Id, DayOfWeek = d }));
        ctx.SaveChanges();
        Assert.Equal(7, ctx.EmployeeAvailability.Count(a => a.EmployeeId == e.Id));
    }

    [Fact]
    public void Shift_tiles_check_their_shifts_store()
    {
        using var ctx = _db.As(AccessContext.ForUser(_org.StoreS1));
        ctx.ShiftTaskBlocks.Add(new ShiftTaskBlock { ShiftId = _org.ShiftOf[_org.S1], BlockTime = new TimeOnly(6, 15), LabourGroup = "reset" });
        ctx.SaveChanges();
        ctx.ShiftTaskBlocks.Add(new ShiftTaskBlock { ShiftId = _org.ShiftOf[_org.S3], BlockTime = new TimeOnly(6, 15), LabourGroup = "reset" });
        Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
    }

    [Theory]
    [InlineData("store")]
    [InlineData("emso")]
    [InlineData("pa")]
    [InlineData("kiosk")]
    public void Configuration_is_written_by_a_super_user_only(string who)
    {
        var access = who switch
        {
            "store" => AccessContext.ForUser(_org.StoreS1),
            "emso" => AccessContext.ForUser(_org.EmsoA1),
            "pa" => AccessContext.ForUser(_org.PaR1),
            _ => AccessContext.ForKiosk(_org.S1),
        };
        using (var ctx = _db.As(access))
        {
            ctx.RosteringRules.Add(new RosteringRule { Key = "restGapMin", Value = 600 });
            Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        }
        using (var super = _db.As(AccessContext.ForUser(_org.Super)))
        {
            super.RosteringRules.Add(new RosteringRule { Key = "restGapMin", Value = 600 });
            super.SaveChanges();
        }
    }

    [Fact]
    public void A_kiosk_writes_its_own_stores_kiosk_tables_and_nothing_else()
    {
        var kiosk = AccessContext.ForKiosk(_org.S1);
        using (var ctx = _db.As(kiosk))
        {
            ctx.StoreWalkNotes.Add(new StoreWalkNote { StoreId = _org.S1, Note = "Broken trolley", RaisedDate = Day });
            ctx.AdminTaskChecks.Add(new AdminTaskCheck { StoreId = _org.S1, Date = Day, TaskName = "End of Day" });
            ctx.DailyLoads.Add(new DailyLoad { StoreId = _org.S1, Date = Day, LoadTypeId = ctx.LoadTypes.Single().Id, Qty = 12 });
            ctx.SaveChanges();
        }
        using (var ctx = _db.As(kiosk))
        {
            ctx.StoreWalkNotes.Add(new StoreWalkNote { StoreId = _org.S2, Note = "Not my store", RaisedDate = Day });
            Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        }
        using (var ctx = _db.As(kiosk))
        {
            ctx.SalesOverrides.Add(new SalesOverride { StoreId = _org.S1, Date = Day });
            Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        }
    }

    [Fact]
    public void Signed_in_users_do_not_write_the_kiosk_tables()
    {
        using var ctx = _db.As(AccessContext.ForUser(_org.Super));
        ctx.AdminTaskChecks.Add(new AdminTaskCheck { StoreId = _org.S1, Date = Day, TaskName = "End of Day" });
        Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void A_feed_writes_the_fed_tables_only()
    {
        var feedToken = _db.System().FeedTokens.Single().Id;
        var feed = AccessContext.ForFeed(feedToken);
        using (var ctx = _db.As(feed))
        {
            var batch = new UploadBatch { Type = UploadTypes.Actuals, Filename = "api:sync" };
            ctx.UploadBatches.Add(batch);
            ctx.ActualsDays.Add(new ActualsDay { StoreId = _org.S3, Date = Day, ActualSales = 44200, BatchId = batch.Id });
            ctx.FeedTokens.Single().LastUsedAt = DateTimeOffset.UtcNow;
            ctx.SaveChanges();
        }
        using (var ctx = _db.As(feed))
        {
            ctx.Employees.Add(new Employee { StoreId = _org.S3, Name = "4-Fed, Person", Role = Roles.Sa });
            Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        }
    }

    [Fact]
    public void Notifications_are_created_by_a_super_user_and_read_by_their_owner()
    {
        using (var ctx = _db.As(AccessContext.ForUser(_org.StoreS1)))
        {
            ctx.Notifications.Add(new Notification { UserId = _org.StoreS1.Id, Kind = "x", Body = "y" });
            Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        }
        using (var ctx = _db.As(AccessContext.ForUser(_org.StoreS1)))
        {
            var mine = ctx.Notifications.Single();
            mine.ReadAt = DateTimeOffset.UtcNow;
            ctx.SaveChanges();
        }
    }

    [Fact]
    public void Server_only_tables_refuse_every_user()
    {
        using var ctx = _db.As(AccessContext.ForUser(_org.Super));
        ctx.CronRuns.Add(new CronRun { Job = "keep-alive" });
        Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void A_refused_save_changes_nothing()
    {
        using var ctx = _db.As(AccessContext.ForUser(_org.StoreS1));
        ctx.SalesOverrides.Add(new SalesOverride { StoreId = _org.S1, Date = Day });
        ctx.SalesOverrides.Add(new SalesOverride { StoreId = _org.S2, Date = Day });
        Assert.Throws<ScopeViolationException>(() => ctx.SaveChanges());
        Assert.Equal(0, _db.System().SalesOverrides.Count());
    }

    [Fact]
    public void Timestamps_default_to_now_as_the_postgres_defaults_did()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 3, 21, 15, 0, TimeSpan.Zero));
        using var db = new TestDatabase(clock);
        var org = TestOrg.Create(db);
        using var ctx = db.As(AccessContext.ForKiosk(org.S1));
        var note = new StoreWalkNote { StoreId = org.S1, Note = "Lights out in freezer aisle", RaisedDate = Day };
        ctx.StoreWalkNotes.Add(note);
        ctx.SaveChanges();
        Assert.Equal(clock.GetUtcNow(), db.System().StoreWalkNotes.Single(n => n.Id == note.Id).CreatedAt);
    }
}

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
