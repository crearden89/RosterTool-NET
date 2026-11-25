// Port of src/app/(app)/plan/non-op/nonOpShared.test.ts, and (new) the page's reads and
// actions (page.tsx, actions.ts) on the data layer's model.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.NonOp;
using Roster.Web.Tests.Sales;

namespace Roster.Web.Tests.NonOp;

public class NonOpSharedTests
{
    private static readonly Guid Meet = Guid.NewGuid(), Train = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid(), One = Guid.NewGuid(), Induct = Guid.NewGuid();

    private static NonOpSub Sub(Guid id, Guid categoryId, int[] daysAllowed) =>
        new(id, categoryId, id.ToString(), 15, 120, 30, null, daysAllowed);

    private static readonly NonOpSub[] Subs =
    [
        Sub(Team, Meet, [0, 1, 2, 3, 4]),
        Sub(One, Meet, [0, 1, 2, 3, 4]),
        Sub(Induct, Train, [0, 1, 2, 3, 4, 5, 6]),
    ];

    [Fact]
    public void Offers_only_the_items_allowed_on_the_dates_weekday_Monday_0()
    {
        Assert.Equal([Team, One], NonOpShared.ItemsAllowed(Subs, Meet, new DateOnly(2026, 11, 27)).Select(s => s.Id).ToArray()); // Friday
        Assert.Empty(NonOpShared.ItemsAllowed(Subs, Meet, new DateOnly(2026, 11, 28))); // Saturday
        Assert.Equal([Induct], NonOpShared.ItemsAllowed(Subs, Train, new DateOnly(2026, 11, 29)).Select(s => s.Id).ToArray()); // Sunday
    }

    [Fact]
    public void Reads_the_weekday_from_the_date_itself_not_the_clock()
    {
        Assert.Equal("Mon", NonOpShared.WeekdayOf(new DateOnly(2026, 11, 23)));
        Assert.Equal("Sun", NonOpShared.WeekdayOf(new DateOnly(2026, 12, 6)));
        Assert.True(NonOpShared.IsWeekend(new DateOnly(2026, 11, 28)));
        Assert.False(NonOpShared.IsWeekend(new DateOnly(2026, 11, 27)));
    }

    [Fact]
    public void Adds_minutes_and_shows_them_as_hours_to_two_decimals()
    {
        NonOpEntryRow E(int minutes) => new(Guid.NewGuid(), new DateOnly(2026, 11, 23), null, null, minutes, "");
        Assert.Equal(75, NonOpShared.MinutesOf([E(30), E(45)]));
        Assert.Equal("1.25", NonOpShared.HoursText(75));
    }

    [Fact]
    public void Gives_each_category_its_own_colour_in_the_categories_order()
    {
        NonOpCategoryRow[] cats = [new(Guid.NewGuid(), "A"), new(Guid.NewGuid(), "B")];
        Assert.NotEqual(NonOpShared.CategoryColour(cats, cats[0].Id), NonOpShared.CategoryColour(cats, cats[1].Id));
        Assert.Equal(NonOpShared.CategoryColour(cats, cats[0].Id), NonOpShared.CategoryColour(cats, cats[0].Id));
        // An entry whose item is gone takes the first colour.
        Assert.Equal(NonOpShared.CategoryColour(cats, cats[0].Id), NonOpShared.CategoryColour(cats, null));
    }

    [Fact]
    public void Shows_the_month_of_a_date_as_its_short_name()
    {
        Assert.Equal("Nov", NonOpShared.MonthOf(new DateOnly(2026, 11, 23)));
        Assert.Equal(23, NonOpShared.DayNumber(new DateOnly(2026, 11, 23)));
    }
}

public class NonOpServiceTests : IDisposable
{
    private readonly PageDb _db = new();
    private readonly Guid _store, _other, _area, _sub, _emp;

    public NonOpServiceTests()
    {
        var (area, ids) = _db.SeedStores("G101", "G102");
        (_area, _store, _other) = (area, ids[0], ids[1]);
        var cat = new NonOpCategory { Id = Guid.NewGuid(), Name = "Meetings", Sort = 1 };
        var sub = new NonOpSubcategory { Id = Guid.NewGuid(), CategoryId = cat.Id, Name = "Team meeting", DefaultMinutes = 30 };
        var emp = new Employee { Id = Guid.NewGuid(), StoreId = _store, Name = "4-Rivers, Sam", ContractHours = 60 };
        _db.Add(cat, sub, emp);
        (_sub, _emp) = (sub.Id, emp.Id);
    }

    private NonOpService Service() => new(_db, _db.Scope, new StoreDirectory(_db), _db, TimeProvider.System);

    private static readonly DateOnly Monday = new(2026, 11, 23);

    [Fact]
    public async Task Adding_requires_a_description_and_a_team_member_then_writes_the_entry()
    {
        _db.User(Tier.Store, storeId: _store);
        var svc = Service();
        Assert.Equal("Description is required", (await svc.AddNonOpEntryAsync(new(_store, Monday, _sub, _emp, 30, "  "))).Error);
        Assert.Equal("A team member is required", (await svc.AddNonOpEntryAsync(new(_store, Monday, _sub, null, 30, "Weekly"))).Error);
        Assert.True((await svc.AddNonOpEntryAsync(new(_store, Monday, _sub, _emp, 30, "Weekly"))).Ok);

        var data = await svc.LoadAsync(null, Monday);
        Assert.Equal(_store, data.StoreId);
        Assert.False(data.ReadOnly);
        var e = Assert.Single(data.Entries);
        Assert.Equal((30, "Weekly", _emp), (e.Minutes, e.Description, e.EmployeeId!.Value));
        Assert.Equal(14, data.Dates.Count);
        Assert.Equal("Team meeting", Assert.Single(data.Subs).Name);
    }

    [Fact]
    public async Task Another_stores_page_is_open_to_read_but_not_to_change()
    {
        _db.Add(new NonOpEntry { Id = Guid.NewGuid(), StoreId = _store, Date = Monday, SubcategoryId = _sub, EmployeeId = _emp, Minutes = 45, Description = "Induction" });
        var other = new Employee { Id = Guid.NewGuid(), StoreId = _other, Name = "4-Lane, Kim", ContractHours = 40 };
        _db.Add(other);
        _db.User(Tier.Store, storeId: _other);
        var svc = Service();

        var data = await svc.LoadAsync(_store, Monday);
        Assert.True(data.ReadOnly);
        Assert.True(data.Peer);
        Assert.Single(data.Entries); // read past the store filter: the deliberate peer gate
        Assert.Equal("Store G101", data.StoreName);

        var entryId = data.Entries[0].Id;
        Assert.Equal(ScopeMessages.DeniedScope, (await svc.DeleteNonOpEntryAsync(entryId)).Error);
        Assert.Equal(ScopeMessages.DeniedScope, (await svc.ClearNonOpEntriesForDateAsync(_store, Monday)).Error);
        Assert.Equal(ScopeMessages.DeniedScope, (await svc.AddNonOpEntryAsync(new(_store, Monday, _sub, _emp, 30, "x"))).Error);
    }

    [Fact]
    public async Task The_area_EMSO_removes_one_entry_and_clears_a_day()
    {
        _db.Add(
            new NonOpEntry { Id = Guid.NewGuid(), StoreId = _store, Date = Monday, SubcategoryId = _sub, Minutes = 30, Description = "a" },
            new NonOpEntry { Id = Guid.NewGuid(), StoreId = _store, Date = Monday, SubcategoryId = _sub, Minutes = 30, Description = "b" },
            new NonOpEntry { Id = Guid.NewGuid(), StoreId = _store, Date = Monday.AddDays(1), SubcategoryId = _sub, Minutes = 15, Description = "c" });
        _db.User(Tier.Emso, areaId: _area);
        var svc = Service();
        var data = await svc.LoadAsync(_store, Monday);
        Assert.False(data.ReadOnly);

        Assert.True((await svc.DeleteNonOpEntryAsync(data.Entries.Single(e => e.Description == "c").Id)).Ok);
        Assert.True((await svc.ClearNonOpEntriesForDateAsync(_store, Monday)).Ok);
        await using var db = _db.System();
        Assert.Empty(await db.NonOpEntries.ToListAsync());
    }

    public void Dispose() => _db.Dispose();
}
