// New: the Team Members page's pure rules (TeamRules: page.tsx's order and fixed-roster
// pay, StaffClient's lists, actions.ts's checks). The TypeScript app has no tests for
// these; the expected values are worked by hand from the TypeScript code.

using Roster.Engine.Core;
using Roster.Web.Features.Team;

namespace Roster.Web.Tests.Team;

public sealed class TeamRulesTests
{
    private static TeamMember Member(string name, string role) => new() { Id = Guid.NewGuid(), Name = name, Role = role };

    [Fact]
    public void Ranks_by_the_UKG_prefix_or_else_the_role()
    {
        Assert.Equal((4d, ""), TeamRules.RankOf("4-Nye, Harper", "SA"));
        Assert.Equal((5d, "L"), TeamRules.RankOf("5l-Castell, Remy", "SA"));
        Assert.Equal((3.5, ""), TeamRules.RankOf("Fenwick, Jo", "RSM"));
        Assert.Equal((9d, ""), TeamRules.RankOf("Someone", "XYZ"));
    }

    [Fact]
    public void Sorts_the_team_top_down_by_seniority_variants_after_their_base_rank()
    {
        var team = new[]
        {
            Member("6-Quinlan, Mia", "JSA"), Member("Fenwick, Jo", "RSM"), Member("1-Okoro, Dana", "SM"),
            Member("5L-Castell, Remy", "SA"), Member("5-Brook, Al", "SA"), Member("4-Nye, Harper", "SA"),
            Member("Whitlow, Ben", "SA"), Member("4-Abara, Lina", "SA"),
        };
        Assert.Equal(
            ["1-Okoro, Dana", "Fenwick, Jo", "4-Abara, Lina", "4-Nye, Harper", "Whitlow, Ben", "5-Brook, Al", "5L-Castell, Remy", "6-Quinlan, Mia"],
            TeamRules.SortTeam(team).Select(m => m.Name));
    }

    [Fact]
    public void Prices_fixed_rosters_per_fortnight_averaged_over_the_cycle()
    {
        var sa = Member("4-Nye, Harper", "SA");
        var dsm = Member("3-Moreau, Tess", "DSM");
        var jsa = Member("6-Quinlan, Mia", "JSA");
        FixedShift Shift(TeamMember m, int week, int day, int sh, int sm, int eh, int em) =>
            new(Guid.NewGuid(), m.Id, week, day, new TimeOnly(sh, sm), new TimeOnly(eh, em));
        var shifts = new[]
        {
            // 9h clock, a 30-minute unpaid break: 8.5 paid. Five over a 2-week cycle.
            Shift(sa, 0, 0, 8, 0, 17, 0), Shift(sa, 0, 2, 8, 0, 17, 0), Shift(sa, 0, 4, 8, 0, 17, 0),
            Shift(sa, 1, 1, 8, 0, 17, 0), Shift(sa, 1, 3, 8, 0, 17, 0),
            // 8.5h clock, the flat 30 unpaid: 8 paid. Weekly (no cycle row).
            Shift(dsm, 0, 0, 6, 0, 14, 30), Shift(dsm, 0, 1, 6, 0, 14, 30), Shift(dsm, 0, 3, 6, 0, 14, 30),
            // 4h: a paid 15. Weekly.
            Shift(jsa, 0, 0, 16, 0, 20, 0), Shift(jsa, 0, 1, 16, 0, 20, 0),
        };
        var paid = TeamRules.FixedPaidHours([sa, dsm, jsa], shifts, [new FixedRosterCycleRow(sa.Id, 2, new DateOnly(2026, 8, 3))]);
        Assert.Equal(42.5, paid[sa.Id]);
        Assert.Equal(48, paid[dsm.Id]);
        Assert.Equal(16, paid[jsa.Id]);
        Assert.True(TeamRules.PaysOverContract("SA", 40, paid[sa.Id]));
        Assert.False(TeamRules.PaysOverContract("SA", 42.5, paid[sa.Id]));
        Assert.False(TeamRules.PaysOverContract("JSA", 0, paid[jsa.Id]));
    }

    private static readonly IReadOnlyList<TradingDayRow> Week =
    [
        .. Enumerable.Range(0, 5).Select(d => new TradingDayRow(d, "08:00:00", "21:00:00", false)),
        new TradingDayRow(5, "08:00:00", "18:00:00", false),
        new TradingDayRow(6, null, null, true),
    ];

    private static BreakRule Rule(double max, string breakType) => new() { MinShiftHours = 3, MaxShiftHours = max, BreakType = breakType };

    private static FixedDay Day(int week, int day, string start, string end) => new(week, day, TimeOnly.Parse(start), TimeOnly.Parse(end));

    [Fact]
    public void Refuses_a_fixed_shift_that_breaks_a_shift_rule_and_says_why()
    {
        var sa = Rule(9, "tiered");
        var none = new Dictionary<string, double>();
        Assert.Equal("Week 1 Mon 08:00–10:30 is under the 3-hour minimum.",
            TeamRules.FixedShiftRefusal("SA", [Day(0, 0, "08:00", "10:30")], sa, Week, none));
        // 9 paid hours are a 9.5-hour clock shift (the unpaid 30 sits outside pay).
        Assert.Equal("Week 1 Mon 07:00–17:00 is longer than the 9.50-hour maximum for a SA.",
            TeamRules.FixedShiftRefusal("SA", [Day(0, 0, "07:00", "17:00")], sa, Week, none));
        Assert.Null(TeamRules.FixedShiftRefusal("SA", [Day(0, 0, "07:30", "17:00")], sa, Week, none));
        Assert.Equal("Week 1 Tue 05:30–09:30 is outside a junior's 06:00–21:00.",
            TeamRules.FixedShiftRefusal("JSA", [Day(0, 1, "05:30", "09:30")], Rule(6, "tiered"), Week, none));
        Assert.Equal("Week 2 Sun 09:00–15:00 falls on a day the store is closed.",
            TeamRules.FixedShiftRefusal("SA", [Day(0, 2, "09:00", "15:00"), Day(1, 6, "09:00", "15:00")], sa, Week, none));
        Assert.Equal("Week 1 Sat 12:00–18:45 runs past the store's close (18:00) and its 30-minute close-down.",
            TeamRules.FixedShiftRefusal("SA", [Day(0, 5, "12:00", "18:45")], sa, Week, none));
        Assert.Null(TeamRules.FixedShiftRefusal("SA", [Day(0, 5, "12:00", "18:45")], sa, Week, new Dictionary<string, double> { ["closeDownMin"] = 60 }));
        // No break rule for the role: no maximum.
        Assert.Null(TeamRules.FixedShiftRefusal("SA", [Day(0, 0, "06:00", "20:00")], null, Week, none));
    }

    [Fact]
    public void Checks_the_actions_arguments_as_the_TypeScript_actions_do()
    {
        var monday = new DateOnly(2026, 8, 3);
        Assert.Equal("Cycle must be 1, 2 or 4 weeks", TeamRules.CycleRefusal(3, monday, []));
        Assert.Equal("A cycle start date is required", TeamRules.CycleRefusal(2, null, []));
        Assert.Equal("Week 3 is outside the 2-week cycle", TeamRules.CycleRefusal(2, monday, [Day(2, 0, "08:00", "12:00")]));
        Assert.Equal("Start must be before end for day 4", TeamRules.CycleRefusal(1, monday, [Day(0, 4, "12:00", "12:00")]));
        Assert.Null(TeamRules.CycleRefusal(4, monday, [Day(3, 6, "08:00", "12:00")]));

        Assert.Equal("Contract hours must be between 0 and 76", TeamRules.ContractRefusal(80, null));
        Assert.Equal("Contract hours must be between 0 and 76", TeamRules.ContractRefusal(double.NaN, null));
        Assert.Equal("Notional hours must be between 0 and 24", TeamRules.ContractRefusal(60, 25));
        Assert.Null(TeamRules.ContractRefusal(76, 0));

        Assert.Equal("Unknown leave type XYZ", TeamRules.LeaveRefusal("XYZ", null));
        Assert.Equal("Hours per day must be between 0 and 24", TeamRules.LeaveRefusal("ANN", 0));
        Assert.Null(TeamRules.LeaveRefusal("UNP", 24));
    }

    [Fact]
    public void Keeps_the_weekend_marker_in_step_with_a_weekend_day_off()
    {
        // 05/09/2026 is a Saturday.
        Assert.Equal(new DateOnly(2026, 9, 5), TeamRules.WeekendStartOf(new DateOnly(2026, 9, 5)));
        Assert.Equal(new DateOnly(2026, 9, 5), TeamRules.WeekendStartOf(new DateOnly(2026, 9, 6)));
        Assert.Null(TeamRules.WeekendStartOf(new DateOnly(2026, 9, 4)));
        Assert.Equal((true, false), TeamRules.MergeWeekendMarker(new DateOnly(2026, 9, 5), true, null, null));
        // Un-granting Sunday clears Sunday only; Saturday's own flag stays.
        Assert.Equal((true, false), TeamRules.MergeWeekendMarker(new DateOnly(2026, 9, 6), false, true, true));
    }

    [Fact]
    public void Reads_a_week_of_availability_and_groups_the_positions()
    {
        var id = Guid.NewGuid();
        var days = TeamRules.AvailabilityDays(
        [
            new AvailabilityRow(id, 0, false, null, null),
            new AvailabilityRow(id, 1, true, new TimeOnly(15, 30), new TimeOnly(21, 0)),
            new AvailabilityRow(id, 2, true, new TimeOnly(15, 30), null),
        ]);
        Assert.Equal(["Mon not available", "Tue 15:30–21:00 only", "Wed all day", "Thu all day"], days.Take(4).Select(d => d.Text));
        Assert.Equal([AvailabilityMode.Off, AvailabilityMode.Window, AvailabilityMode.All], days.Take(3).Select(d => d.Mode));

        var groups = TeamRules.GroupPositions(Positions.All.Select(p => new PositionOption(p.Code, p.Label, p.BaseRole, p.WeeklyHours, p.YearBand)));
        Assert.Equal(["SM", "ASM", "DSM", "RSM", "SA", "JSA"], groups.Select(g => g.Role));
        Assert.Equal("Junior Store Assistant", groups[^1].Label);
        Assert.Equal(["JSA-16", "JSA-17"], groups[^1].Items.Select(p => p.Code));
        Assert.Equal("Every week", TeamRules.CycleLabel(1));
        Assert.Equal("4-week cycle", TeamRules.CycleLabel(4));
    }
}
