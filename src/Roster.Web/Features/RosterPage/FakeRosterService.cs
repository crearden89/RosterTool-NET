// New: an invented fortnight for the Roster page, for its tests and for looking at the page
// without a database (not registered by RosterModule). No real store, person or figure: every name and number is made up.
// The rows go through the real display builder (Display.BuildRosterRows), so breaks and
// tiles are decorated exactly as the real page decorates them.

using System.Globalization;
using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Review;
using Roster.Web.Features.RosterPage.Grid;

namespace Roster.Web.Features.RosterPage;

public sealed class FakeRosterService : IRosterService
{
    private static readonly StoreOption[] Stores =
    [
        new(Guid.Parse("7d3c2f0a-1111-4a6e-9a51-0c1f00000101"), "G101", "Riverside"),
        new(Guid.Parse("7d3c2f0a-1111-4a6e-9a51-0c1f00000102"), "G102", "Hillcrest"),
    ];

    private sealed record Person(string Id, string Name, string Role, double Rate);

    private static readonly Person[] Team =
    [
        new("a0000000-0000-4000-8000-000000000001", "Morgan Avery", Roles.SM, 42.5),
        new("a0000000-0000-4000-8000-000000000002", "Priya Nair", Roles.ASM, 38),
        new("a0000000-0000-4000-8000-000000000003", "Liam Chen", Roles.SA, 31.2),
        new("a0000000-0000-4000-8000-000000000004", "Ruby Okafor", Roles.SA, 31.2),
        new("a0000000-0000-4000-8000-000000000005", "Noah Fischer", Roles.SA, 31.2),
        new("a0000000-0000-4000-8000-000000000006", "Isla Moreau", Roles.SA, 31.2),
        new("a0000000-0000-4000-8000-000000000007", "Kai Tanaka", Roles.SA, 31.2),
        new("a0000000-0000-4000-8000-000000000008", "Ella Brooks", Roles.JSA, 17.4),
        new("a0000000-0000-4000-8000-000000000009", "Finn Walsh", Roles.JSA, 17.4),
        new("a0000000-0000-4000-8000-000000000010", "Grace Holloway", Roles.SA, 31.2),
    ];

    // Who works when on an invented day: (team index, start, end); -1 is an open shift.
    private static readonly (int Who, double Start, double End)[] DayShape =
    [
        (0, 6 * 60, 15 * 60), (1, 12 * 60 + 30, 21 * 60 + 30), (2, 6 * 60, 14 * 60), (3, 7 * 60, 15 * 60 + 30),
        (4, 9 * 60, 17 * 60), (5, 13 * 60, 21 * 60 + 30), (6, 15 * 60, 21 * 60 + 30), (7, 16 * 60, 20 * 60),
        (8, 16 * 60 + 30, 20 * 60 + 30),
    ];

    private const int OpenShiftDay = 3; // the fortnight's day with an open shift (a red chip)

    public Task<RosterPageModel> LoadPageAsync(RosterPageQuery query, CancellationToken cancellationToken = default)
    {
        var store = Guid.TryParse(query.Store, out var s) && Stores.Any(x => x.Id == s) ? s : Stores[0].Id;
        var requested = ParseDate(query.Period) ?? ParseDate(query.Date);
        var periodStart = requested is { } r ? Period.PeriodStartFor(r) : Period.CurrentPeriodStart();
        var dates = Period.PeriodDatesFrom(periodStart);
        var selected = ParseDate(query.Date) ?? periodStart;
        var dayIndex = dates.IndexOf(selected);
        var day = dayIndex >= 0 ? BuildDay(dayIndex) : null;
        var generatedAt = new DateTimeOffset(periodStart.ToDateTime(new TimeOnly(9, 12)), TimeSpan.Zero).AddDays(-6);

        var chips = dates.Select((d, i) =>
        {
            var ok = i != OpenShiftDay;
            var n = DayShape.Length + (i == OpenShiftDay ? 1 : 0);
            return new DayChip
            {
                Date = d,
                Href = $"plan/roster?store={store}&date={RosterPageModel.Iso(d)}",
                Tone = ok ? DayTone.Ok : DayTone.Error,
                Note = n.ToString(CultureInfo.InvariantCulture),
                NoteWide = $"{n} shifts",
                Status = $"{(ok ? "OK" : "has an error")}, {n} shifts",
            };
        }).ToList();

        var checks = new List<RosterCheck>();
        if (day is { HasData: true })
        {
            if (dayIndex == OpenShiftDay)
            {
                checks.Add(new("e0", CheckTone.Error, "1 open shift: SA 17:00-21:00 needs cover"));
                checks.Add(new("e1", CheckTone.Note, "Why: everyone who could take it is at their weekly cap"));
            }
            checks.Add(new("w0", CheckTone.Warning, "Noah Fischer works 6 days in a row"));
        }

        var coverDays = new List<DateOnly> { dates[OpenShiftDay] };
        return Task.FromResult(new RosterPageModel
        {
            Stores = Stores,
            SelectedStore = store,
            PeriodStart = periodStart,
            SelectedDate = selected,
            PlanDates = dates,
            IsPastPeriod = periodStart < Period.CurrentPeriodStart(),
            HasPlan = true,
            HasSnapshot = true,
            GeneratedAt = generatedAt,
            ReviewState = PlanReviewState.Approved,
            Gate = new RosterGate { Unlocked = true, PlanApproved = true, ReviewStatus = "approved" },
            CoverNoteDays = coverDays,
            CoverNoteReason = $"1 day has an error (open shifts or cover gaps): {coverDays[0].Day:00}/{coverDays[0].Month:00}",
            FortnightCost = new RosterFortnightCost(48210.55, 49875.2, 49875.2 - 48210.55),
            Contracts = new RosterContracts
            {
                People =
                [
                    new(Team[3].Id, Team[3].Name, Team[3].Role, false, 3.5),
                    new(Team[6].Id, Team[6].Name, Team[6].Role, true, 1.25),
                ],
                Under = 1,
                Over = 1,
                UnderHours = 3.5,
                OverHours = 1.25,
            },
            ContractFlags = ["Noah Fischer works 6 days in a row in the fortnight"],
            DayChips = chips,
            Day = day,
            DayChecks = checks,
            ActiveEmployees = Js.Sorted(Team, (a, b) => Js.LocaleCompare(a.Name, b.Name))
                .Select(p => new RosterEmployee(Guid.Parse(p.Id), p.Name, p.Role)).ToList(),
        });
    }

    public Task<DateTimeOffset?> HeldRosterGeneratedAtAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTimeOffset?>(new DateTimeOffset(periodStart.ToDateTime(new TimeOnly(9, 12)), TimeSpan.Zero).AddDays(-6));

    private static RosterDayView BuildDay(int dayIndex)
    {
        var shape = DayShape.ToList();
        if (dayIndex == OpenShiftDay) shape.Add((-1, 17 * 60, 21 * 60));
        var raw = shape.Select((x, i) => x.Who < 0
            ? new RawShift("open-1", "Open shift", Roles.SA, x.Start, x.End, 0, Paint(Roles.SA, x.Start, x.End, i))
            : new RawShift(Team[x.Who].Id, Team[x.Who].Name, Team[x.Who].Role, x.Start + (dayIndex % 2) * 30, x.End + (dayIndex % 2) * 30, 0,
                Paint(Team[x.Who].Role, x.Start + (dayIndex % 2) * 30, x.End + (dayIndex % 2) * 30, i))).ToList();
        var rows = Display.BuildRosterRows(raw);
        var costs = new Dictionary<string, double>();
        foreach (var row in rows)
            if (Team.FirstOrDefault(p => p.Id == row.Key) is { } p)
                costs[row.Key] = Js.Round((row.End - row.Start - row.UnpaidBreakMinutes) / 60 * p.Rate * 100) / 100;
        return new RosterDayView
        {
            HasData = true,
            Rows = rows.Select(RosterGridRow.From).ToList(),
            CostByKey = costs,
            DayTotalCost = costs.Values.Sum(),
            IdealCost = 3380.4,
            Flags = [],
            LockRows = raw.Where(r => r.Key != "open-1")
                .Select(r => new RosterLockRow(Guid.Parse(r.Key), r.Name, r.Role, r.Start, r.End, r.Key == Team[2].Id)).ToList(),
        };
    }

    // An invented day's tiles: the load first thing, a manager's admin hour, reset and
    // specials through the day, cleaning at night, checkout in between.
    private static List<RawBlock> Paint(string role, double start, double end, int seed)
    {
        string[] loadSubs = ["produce", "long_life", "freezer", "ambient", "bread"];
        var blocks = new List<RawBlock>();
        for (var t = start; t < end; t += 15)
        {
            var q = (int)(t / 15);
            blocks.Add(
                t < 9 * 60 ? new RawBlock(t, "load_running", loadSubs[(q + seed) % loadSubs.Length])
                : Roles.IsManager(role) && t >= 10 * 60 && t < 11 * 60 ? new RawBlock(t, "admin")
                : t >= 19 * 60 ? new RawBlock(t, "cleaning", q % 2 == 0 ? "front_end" : "shop_floor")
                : (q + seed) % 7 == 0 ? new RawBlock(t, "reset", "re_runs")
                : role != Roles.JSA && t >= 13 * 60 && t < 14 * 60 ? new RawBlock(t, "specials", "tidy")
                : role == Roles.JSA ? new RawBlock(t, "reset", "decarding")
                : new RawBlock(t, "checkout"));
        }
        return blocks;
    }

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
