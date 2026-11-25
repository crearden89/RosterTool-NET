// New: the Weekends service against the data layer (SQLite): the window on the period
// grid, the team's order and markers, and saveWeekendMarkers with its scope check.

using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Weekends;
using Roster.Web.Tests.Team;

namespace Roster.Web.Tests.Weekends;

public sealed class WeekendsServiceTests : IDisposable
{
    private readonly TeamTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateOnly PeriodStart = new(2026, 8, 3);

    [Fact]
    public async Task Shows_the_fortnights_two_weekends_after_the_two_before_them()
    {
        var sa = _db.AddEmployee(TeamTestDb.StoreA, "Brook, Al");
        var sm = _db.AddEmployee(TeamTestDb.StoreA, "Young, Sam", "SM", 90);
        _db.AddEmployee(TeamTestDb.StoreA, "Abara, Lina", "JSA", 0);
        var other = _db.AddEmployee(TeamTestDb.StoreB, "Sandoval, Ruby");
        _db.Seed(db => db.WeekendMarkers.AddRange(
            new WeekendMarker { EmployeeId = sa, WeekendStart = new DateOnly(2026, 8, 8), SatOff = true, SunOff = true },
            new WeekendMarker { EmployeeId = sm, WeekendStart = new DateOnly(2026, 7, 25), SatOff = true, SunOff = false },
            new WeekendMarker { EmployeeId = sm, WeekendStart = new DateOnly(2026, 8, 22), SatOff = true, SunOff = true },
            new WeekendMarker { EmployeeId = other, WeekendStart = new DateOnly(2026, 8, 8), SatOff = true, SunOff = true }));

        var page = await _db.Weekends.LoadAsync(TeamTestDb.StoreA, PeriodStart);
        Assert.Equal(PeriodStart, page.PeriodStart);
        Assert.Equal([new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 8), new DateOnly(2026, 8, 15)], page.Weekends);
        // Managers first, then by name.
        Assert.Equal(["Young, Sam", "Brook, Al", "Abara, Lina"], page.Employees.Select(e => e.Name));
        Assert.Equal(2, page.Markers.Count);
        Assert.DoesNotContain(page.Markers, m => m.EmployeeId == other || m.WeekendStart == new DateOnly(2026, 8, 22));

        // Not a period start: the fortnight the Plan screen opens on.
        var fallback = await _db.Weekends.LoadAsync(TeamTestDb.StoreA, new DateOnly(2026, 8, 4));
        Assert.Equal(Period.PlanningPeriodStart(Dates.TodayAu()), fallback.PeriodStart);
    }

    [Fact]
    public async Task Saves_every_edited_weekend_in_one_go_refused_whole_out_of_scope()
    {
        var a = _db.AddEmployee(TeamTestDb.StoreA, "Brook, Al");
        var b = _db.AddEmployee(TeamTestDb.StoreB, "Sandoval, Ruby");
        var sat = new DateOnly(2026, 8, 8);
        _db.Seed(db => db.WeekendMarkers.Add(new WeekendMarker { EmployeeId = a, WeekendStart = sat, SatOff = true, SunOff = true }));

        Assert.True((await _db.Weekends.SaveWeekendMarkersAsync([])).Ok);
        Assert.True((await _db.Weekends.SaveWeekendMarkersAsync(
            [new WeekendMarkerInput(a, sat, false, true), new WeekendMarkerInput(a, sat.AddDays(7), true, true)])).Ok);
        var rows = _db.Read(db => db.WeekendMarkers.Where(m => m.EmployeeId == a).OrderBy(m => m.WeekendStart)
            .Select(m => ValueTuple.Create(m.SatOff, m.SunOff)).ToList());
        Assert.Equal([(false, true), (true, true)], rows);

        _db.SignInAs(Tier.Store, TeamTestDb.StoreA);
        Assert.Equal(ScopeMessages.DeniedScope, (await _db.Weekends.SaveWeekendMarkersAsync(
            [new WeekendMarkerInput(a, sat, true, true), new WeekendMarkerInput(b, sat, true, true)])).Error);
        Assert.Equal((false, true), _db.Read(db => db.WeekendMarkers.Where(m => m.EmployeeId == a && m.WeekendStart == sat)
            .Select(m => ValueTuple.Create(m.SatOff, m.SunOff)).Single()));
        Assert.True((await _db.Weekends.SetWeekendMarkerAsync(a, sat, true, false)).Ok);

        _db.SignInAs(Tier.Director);
        Assert.True((await _db.Weekends.LoadAsync(null, PeriodStart)).ReadOnly);
        Assert.Equal(ScopeMessages.DeniedReadOnly, (await _db.Weekends.SetWeekendMarkerAsync(a, sat, true, true)).Error);
    }

    [Fact]
    public void Counts_full_weekends_off_and_keys_each_weekend()
    {
        var weekends = WeekendWindow.WindowSaturdays(PeriodStart);
        var id = Guid.Parse("7ea70000-0000-4000-8000-000000000001");
        Assert.Equal("7ea70000-0000-4000-8000-000000000001:2026-08-08", WeekendsRules.Key(id, weekends[2]));
        Assert.Equal(1, WeekendsRules.FullOffCount(weekends, wk => wk == weekends[0] ? (true, true) : (true, false)));
        Assert.Equal(2, WeekendsRules.EditableFrom(weekends.Count));
    }
}
