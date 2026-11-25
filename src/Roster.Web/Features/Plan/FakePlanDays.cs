// New: invented plan days for FakePlanService - the engine's own shapes (Stage 1's result
// and the plan's shifts), filled with made-up but plausible numbers, so the page can be
// looked at before the real loaders can solve a day.

using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.SubTasks;

namespace Roster.Web.Features.Plan;

/// <summary>FAKE: invents a held plan day for a store and date (the same every time).</summary>
public static class FakePlanDays
{
    private static readonly (string Role, double Start, double End)[] Weekday =
    [
        (RoleBadges.Mgr, 6 * 60, 14 * 60 + 30),
        (RoleBadges.Mgr, 12 * 60, 20 * 60 + 30),
        (RoleBadges.Sa, 6 * 60, 12 * 60),
        (RoleBadges.Sa, 7 * 60, 15 * 60),
        (RoleBadges.Sa, 8 * 60, 16 * 60 + 30),
        (RoleBadges.Sa, 9 * 60, 13 * 60),
        (RoleBadges.Sa, 10 * 60, 18 * 60),
        (RoleBadges.Sa, 12 * 60, 20 * 60 + 30),
        (RoleBadges.Sa, 14 * 60, 20 * 60 + 30),
        (RoleBadges.Jsa, 16 * 60, 20 * 60),
    ];

    private static readonly (string Role, double Start, double End)[] WeekendExtra =
    [
        (RoleBadges.Sa, 8 * 60 + 30, 14 * 60 + 30),
        (RoleBadges.Jsa, 9 * 60, 13 * 60),
    ];

    public static readonly CoverRules Cover = DayHours.DefaultCover;

    public static PlanSnapshotDay Invent(Guid storeId, DateOnly date, bool phReallocated)
    {
        if (date is { Month: 12, Day: 25 })
        {
            return new PlanSnapshotDay
            {
                Date = date,
                Ok = true,
                Result = new Stage1Computation { HasData = false, Closed = true },
                Plan = null,
            };
        }
        var rng = new Random(HashCode.Combine(storeId.GetHashCode(), date.DayNumber) & 0x7fffffff);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        const double tradingOpen = 8 * 60 + 30, tradingClose = 20 * 60, staffOpen = 6 * 60, staffClose = 20 * 60 + 30;

        // The tills: two to four on checkout, busiest around midday and late afternoon.
        var checkout = new List<PlannedBlock>();
        for (var t = tradingOpen; t < tradingClose; t += 15)
        {
            var h = t / 60;
            var n = (h is >= 11 and < 14) || (h is >= 16 and < 18.5) ? 3 : 2;
            if (weekend && h is >= 10 and < 15) n++;
            checkout.Add(new PlannedBlock("checkout", t, n));
        }

        var slots = weekend ? [.. Weekday, .. WeekendExtra] : Weekday;
        var shifts = slots.Select((s, i) => PlanEdit.ReshapePlanShift(null, $"slot-{i + 1}", s.Role, s.Start, s.End)).ToList();
        shifts = Paint(shifts, checkout, date, phReallocated);

        var result = new Stage1Computation
        {
            HasData = true,
            Closed = false,
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            StaffOpen = staffOpen,
            StaffClose = staffClose,
            Windows =
            [
                new WindowRule("load_running", 6 * 60, 11 * 60),
                new WindowRule("reset", 6 * 60, 20 * 60 + 30),
                new WindowRule("admin", 6 * 60, 20 * 60 + 30),
                new WindowRule("specials", 8 * 60, 20 * 60),
                new WindowRule("cleaning", 6 * 60, 20 * 60 + 30),
            ],
            Blocks = checkout,
            AdminTasks =
            [
                new ResolvedAdminTask { Name = "Banking", Minutes = 30, WindowStart = 7 * 60, WindowEnd = 9 * 60, WindowLabel = "07:00–09:00" },
                new ResolvedAdminTask { Name = "Ordering", Minutes = 45, WindowStart = 13 * 60, WindowEnd = 16 * 60, WindowLabel = "13:00–16:00" },
            ],
        };
        // The benchmark: what was painted, give or take a little per group, so the table
        // has variances to show.
        double Tiles(string g) => shifts.Sum(s => s.Blocks.Count(b => b.Group == g)) * 0.25;
        var allocations = new List<GroupAllocation>();
        foreach (var g in new[] { "load_running", "reset", "admin", "specials", "cleaning", "non_op" })
        {
            var painted = Tiles(g);
            var bench = Math.Max(0, painted + (rng.Next(0, 5) - 2) * 0.25);
            allocations.Add(new GroupAllocation(g, bench, painted));
        }
        result.Allocations = allocations;
        var checkoutHours = Tiles("registers") + Tiles("checkout");
        result.TotalBenchmark = allocations.Sum(a => a.BenchmarkHours) + checkoutHours + (rng.Next(0, 7) - 3) * 0.25;
        result.Flags = rng.Next(0, 4) == 0 ? ["Reset: 0.50h could not fit inside its window and the staffed day"] : [];

        var check = DayChecker.CheckPlanDay(result, shifts.Select(s => s.ToChecked()).ToList(), new DayCheckOptions { Cover = Cover });
        var notes = new List<string>();
        var plan = new PlanShiftComputation
        {
            HasData = true,
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            Shifts = shifts,
            Notes = notes,
            Checks = check.Flags,
            Hours = check.Hours,
            Flags = [.. notes, .. check.Flags],
        };
        return new PlanSnapshotDay
        {
            Date = date,
            Ok = DayChecker.DayIsOk([.. result.Flags, .. plan.Flags]),
            Result = result,
            Plan = plan,
        };
    }

    // Gives every quarter-hour of every shift a job: the tills first (as many people as
    // the block needs), then the load in the morning, the manager's admin, and the rest
    // spread over reset, specials and cleaning. Tiles of the four sub-task groups are
    // labelled by turns.
    private static List<PlanShift> Paint(List<PlanShift> shifts, List<PlannedBlock> checkout, DateOnly date, bool phReallocated)
    {
        var need = checkout.ToDictionary(b => b.Time, b => b.Headcount);
        var building = Catalogue.SpecialsBuildingDays.Contains(Period.DowMon0(date));
        var specials = building ? new[] { "building", "tidy", "running" } : ["tidy", "running"];
        string? SubFor(string g, int n) => g switch
        {
            "load_running" => new[] { "produce", "long_life", "freezer", "ambient", "bread", "short_life" }[n % 6],
            "reset" => new[] { "re_runs", "decarding" }[n % 2],
            "cleaning" => new[] { "front_end", "shop_floor", "boh" }[n % 3],
            "specials" => specials[n % specials.Length],
            _ => null,
        };
        var taken = new Dictionary<double, int>();
        var out_ = new List<PlanShift>();
        for (var i = 0; i < shifts.Count; i++)
        {
            var s = shifts[i];
            var blocks = new List<DisplayBlock>();
            foreach (var b in s.Blocks)
            {
                if (b.Group == TaskGroups.Break)
                {
                    blocks.Add(b);
                    continue;
                }
                var t = b.Time;
                var n = (int)(t / 15) + i;
                string group;
                if (need.TryGetValue(t, out var heads) && taken.GetValueOrDefault(t) < heads && s.PlanRole == RoleBadges.Sa)
                {
                    taken[t] = taken.GetValueOrDefault(t) + 1;
                    group = "registers";
                }
                else if (t < 11 * 60 && !(phReallocated && n % 4 == 0)) group = s.PlanRole == RoleBadges.Mgr && t >= 7 * 60 && t < 9 * 60 ? "admin" : "load_running";
                else if (s.PlanRole == RoleBadges.Mgr && t >= 13 * 60 && t < 14 * 60) group = "admin";
                else if (t >= 19 * 60 + 30) group = "cleaning";
                else group = new[] { "reset", "specials", "cleaning", "specials" }[n % 4];
                if (group == "load_running" && phReallocated && n % 4 == 0) group = TaskGroups.Flex;
                blocks.Add(new DisplayBlock(t, group, SubFor(group, n)));
            }
            out_.Add(s with { Blocks = blocks });
        }
        return out_;
    }
}
