// Port of src/lib/stage2/shaper.test.ts.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Shaper;

public class ShaperTests
{
    // Flat demand of `total` bodies over [start, end), single group.
    internal static List<DemandBlock> Flat(double start, double end, double total, string group = "cleaning")
    {
        var out_ = new List<DemandBlock>();
        for (var m = start; m < end; m += 15) out_.Add(new DemandBlock(m, total, new JsMap<string, double> { [group] = total }));
        return out_;
    }

    // Merge overlapping demand layers (sums totals/groups at each block).
    internal static List<DemandBlock> Merge(params List<DemandBlock>[] layers)
    {
        var map = new JsMap<double, DemandBlock>();
        foreach (var layer in layers)
        {
            foreach (var d in layer)
            {
                var cur = map.Get(d.Time) ?? new DemandBlock(d.Time, 0);
                cur.Total += d.Total;
                foreach (var (g, n) in d.Groups) cur.Groups.Set(g, cur.Groups.GetOr(g, 0) + n);
                map.Set(d.Time, cur);
            }
        }
        return Js.Sorted(map.Values(), (a, b) => a.Time - b.Time);
    }

    internal static ShaperInput Input(List<DemandBlock> demand) => new()
    {
        StaffOpen = 6 * 60,
        StaffClose = 21 * 60 + 30,
        TradingOpen = 8 * 60,
        TradingClose = 20 * 60,
        IsSchoolDay = false,
        DemandByBlock = demand,
        MgrRule = new ShaperRule(3, 12),
        SaRule = new ShaperRule(3, 9.5),
        JsaRule = new ShaperRule(3, 6),
    };

    private static double Worked(IEnumerable<PlanSlot> slots) =>
        slots.Sum(x =>
        {
            var type = x.Role == "MGR" ? "flat_30_unpaid" : "tiered";
            return (x.End - x.Start) - Breaks.BreakForShift(type, (x.End - x.Start) / 60).TotalMinutes;
        });

    private static double CoverIn(ShapedDay s, double m) => s.Slots.Count(x => x.Start <= m && m < x.End);

    private static string Show(IEnumerable<PlanSlot> slots) => string.Join("; ", slots);

    // ─── rostering-rule overrides thread through the shaper ─────────────────────

    [Fact]
    public void Moves_the_junior_window_with_an_overridden_earliest_start()
    {
        // Enough flat demand that several SA slots exist; by default a slot
        // starting 06:00 can never be junior-labelled (jsaEarliestMin 06:00 means
        // 06:00 IS legal) - so instead tighten the rule and check the label
        // disappears from a slot the default would have allowed.
        var demand = Flat(10 * 60, 20 * 60, 4);
        var dflt = PlanShaper.ShapePlan(Input(demand));
        var dfltJsa = dflt.Slots.Count(s => s.Role == "JSA");
        var strict = PlanShaper.ShapePlan(Input(demand) with
        {
            Rules = new Dictionary<string, double> { ["jsaEarliestMin"] = 19 * 60, ["jsaSchoolDayStartMin"] = 19 * 60 },
        });
        var strictJsa = strict.Slots.Count(s => s.Role == "JSA");
        Assert.True(strictJsa <= dfltJsa);
        foreach (var s in strict.Slots)
            if (s.Role == "JSA") Assert.True(s.Start >= 19 * 60);
        // And a fresh default run is unaffected by the previous override run.
        var again = PlanShaper.ShapePlan(Input(demand));
        Assert.Equal(dfltJsa, again.Slots.Count(s => s.Role == "JSA"));
    }

    // ─── lengthBadness ───────────────────────────────────────────────────────────

    [Fact]
    public void LengthBadness_ranks_by_cost_per_worked_hour_straight_off_the_awards_break_tiers()
    {
        // 9.5h is the cheapest hour of work available; 6.00h is the cheapest short
        // shift, and beats every length in the eights. 6.25h is the worst on the
        // board - a quarter hour past the six-hour tier, so the entitlement
        // doubles and the shift delivers less work for more money.
        Assert.Equal(0, PlanShaper.LengthBadness(570)); // 9.5h
        Assert.True(PlanShaper.LengthBadness(360) < PlanShaper.LengthBadness(480)); // 6h beats 8h
        Assert.Equal(new double[] { 180, 240, 360, 375, 480, 570 }.Max(m => PlanShaper.LengthBadness(m)), PlanShaper.LengthBadness(375)); // 6.25h, worst of the sample
        Assert.True(PlanShaper.LengthBadness(180) > PlanShaper.LengthBadness(240)); // 3h worse than 4h
    }

    [Fact]
    public void LengthBadness_ranks_a_manager_separately_a_flat_unpaid_break_has_no_tier_to_fall_off()
    {
        // Nothing to trip over, so longer is simply cheaper per worked hour.
        Assert.True(PlanShaper.LengthBadness(720, "flat_30_unpaid") < PlanShaper.LengthBadness(360, "flat_30_unpaid"));
    }

    // ─── shapePlan - manager pair ────────────────────────────────────────────────

    [Fact]
    public void Emits_exactly_two_manager_slots_opener_from_span_start_closer_to_span_end()
    {
        var slots = PlanShaper.ShapePlan(Input(Flat(8 * 60, 20 * 60, 3))).Slots;
        var mgrs = slots.Where(s => s.Role == "MGR").ToList();
        Assert.Equal(2, mgrs.Count);
        Assert.Equal("mgr-open", mgrs[0].Id);
        Assert.Equal(8 * 60, mgrs[0].Start);
        Assert.Equal("mgr-close", mgrs[1].Id);
        Assert.Equal(20 * 60, mgrs[1].End);
    }

    [Fact]
    public void Gives_the_pair_a_mandatory_overlap_of_at_least_an_hour()
    {
        var mgrs = PlanShaper.ShapePlan(Input(Flat(8 * 60, 20 * 60, 3))).Slots.Where(s => s.Role == "MGR").ToList();
        Assert.True(mgrs[0].End - mgrs[1].Start >= 60);
    }

    [Fact]
    public void Cuts_the_pair_on_cover_not_to_a_list_of_preferred_lengths()
    {
        // The pair used to be cut to a hand-typed list of "efficient" lengths -
        // 9h + 6h of work on this flat 14-hour day - before any other shift
        // existed: length deciding the build (Roster Engine Review 07/11/2026,
        // P5, A04-4). On cover alone a flat day splits evenly around the hour's
        // handover; the cost of each length is judged last, on the finished day.
        var mgrs = PlanShaper.ShapePlan(Input(Flat(6 * 60, 20 * 60, 2)) with { TradingOpen = 6 * 60 }).Slots.Where(s => s.Role == "MGR").ToList();
        Assert.Equal(new double[] { 480, 480 }, new[] { mgrs[0].End - mgrs[0].Start, mgrs[1].End - mgrs[1].Start });
    }

    // ─── shapePlan - demand exactness + efficient SA lengths ─────────────────────

    [Fact]
    public void Plans_exactly_the_demanded_work_hours()
    {
        // 2 bodies × 9.5h = 19h of demanded work → slot clock time minus each
        // slot's break entitlement must come back to exactly 19h.
        var slots = PlanShaper.ShapePlan(Input(Flat(8 * 60, 17 * 60 + 30, 2))).Slots;
        Assert.Equal(19 * 60, Worked(slots));
    }

    [Fact]
    public void Covers_every_demanded_block_with_enough_bodies()
    {
        var s = PlanShaper.ShapePlan(Input(Merge(Flat(8 * 60, 20 * 60, 2), Flat(10 * 60, 16 * 60, 2))));
        foreach (var d in s.DemandByBlock) Assert.True(CoverIn(s, d.Time) >= d.Total);
    }

    [Fact]
    public void Never_creates_a_shift_under_3_hours()
    {
        var slots = PlanShaper.ShapePlan(Input(Merge(Flat(8 * 60, 20 * 60, 2), Flat(10 * 60, 11 * 60, 1)))).Slots;
        foreach (var s in slots) Assert.True(s.End - s.Start >= 180);
    }

    [Fact]
    public void Flags_rather_than_hides_hours_planned_above_the_curve()
    {
        // A lone extra body for 45min: the shift it spawns can't legally end
        // before 3h, so the over-cover is measured and flagged.
        var flags = PlanShaper.ShapePlan(Input(Merge(Flat(9 * 60, 17 * 60, 1), Flat(10 * 60, 10 * 60 + 45, 1)))).Flags;
        Assert.Contains(flags, f => f.Contains("above the curve"));
    }

    // ─── shapePlan - pre-open safety floor ───────────────────────────────────────

    [Fact]
    public void Floats_a_lone_pre_open_start_later_so_2_people_begin_together()
    {
        // 1 body of Load Running 06:00-08:00 → compressed to 2 bodies 07:00-08:00.
        var s = PlanShaper.ShapePlan(Input(Merge(Flat(6 * 60, 8 * 60, 1, "load_running"), Flat(8 * 60, 18 * 60, 2))));
        var preOpen = s.DemandByBlock.Where(d => d.Time < 8 * 60).ToList();
        Assert.Equal(4, preOpen.Count); // one hour of blocks, not two
        Assert.Equal(7 * 60, preOpen[0].Time);
        foreach (var d in preOpen) Assert.Equal(2, d.Total);
        // Nothing starts before the compressed 07:00.
        foreach (var x in s.Slots) Assert.True(x.Start >= 7 * 60);
        // Total pre-open hours preserved (2h of work either way).
        Assert.Equal(8, preOpen.Sum(d => d.Total));
    }

    [Fact]
    public void Leaves_a_pre_open_window_that_already_has_2_or_more_people_untouched()
    {
        var s = PlanShaper.ShapePlan(Input(Merge(Flat(6 * 60, 8 * 60, 2, "load_running"), Flat(8 * 60, 18 * 60, 2))));
        Assert.Equal(6 * 60, s.DemandByBlock[0].Time);
        Assert.Equal(2, s.DemandByBlock[0].Total);
    }

    // ─── shapePlan - JSA labelling + hierarchy ───────────────────────────────────
    // The evening spike is a DIFFERENT group (specials) so the levelling pass
    // keeps it in its own evening window instead of smoothing it away.

    private static List<DemandBlock> EveningSpike() => Merge(Flat(8 * 60, 16 * 60, 2), Flat(16 * 60, 20 * 60, 5, "specials"));

    [Fact]
    public void Labels_a_short_evening_slot_JSA_when_3_other_adults_are_present()
    {
        var jsas = PlanShaper.ShapePlan(Input(EveningSpike())).Slots.Where(s => s.Role == "JSA").ToList();
        Assert.NotEmpty(jsas);
        foreach (var s in jsas)
        {
            Assert.True(s.End - s.Start <= 6 * 60);
            Assert.True(s.Start >= 8 * 60);
            Assert.True(s.End <= 20 * 60);
        }
    }

    [Fact]
    public void Never_labels_JSA_on_a_school_day_before_16_00()
    {
        var slots = PlanShaper.ShapePlan(Input(EveningSpike()) with { IsSchoolDay = true }).Slots;
        foreach (var s in slots.Where(x => x.Role == "JSA")) Assert.True(s.Start >= 16 * 60);
    }

    [Fact]
    public void Outputs_hierarchy_order_MGR_opener_MGR_closer_SAs_JSAs()
    {
        var slots = PlanShaper.ShapePlan(Input(EveningSpike())).Slots;
        Assert.Equal("mgr-open", slots[0].Id);
        Assert.Equal("mgr-close", slots[1].Id);
        var roles = slots.Select(s => s.Role).ToList();
        var lastSa = roles.LastIndexOf("SA");
        var firstJsa = roles.IndexOf("JSA");
        if (firstJsa != -1) Assert.True(firstJsa > lastSa);
    }

    // ─── shapePlan - safety floor flagging ───────────────────────────────────────

    [Fact]
    public void Brings_a_second_person_in_rather_than_leave_one_alone_and_says_so()
    {
        // Demand of a single body all day would leave one person alone outside
        // the managers' handover. The floor is a safety rule ("always"), so the
        // plan covers it instead of only flagging it, and says why.
        var s = PlanShaper.ShapePlan(Input(Flat(9 * 60, 17 * 60, 1)));
        for (double m = 9 * 60; m < 17 * 60; m += 15)
        {
            var on = CoverIn(s, m);
            Assert.True(on == 0 || on >= 2, $"only {on} at {m / 60}");
        }
        Assert.Contains(s.Flags, f => f.Contains("so nobody works alone"));
    }

    // ─── shapePlan - manager slots stay inside the award maximum ─────────────────

    [Fact]
    public void Cuts_the_pair_so_PAID_hours_never_exceed_the_max()
    {
        // The award maximum caps PAID hours, not clock: a manager capped at 9 paid
        // hours works a 9.5h span carrying a 30-minute unpaid break. The pair is
        // cut so that holds - previously it was cut on clock, which quietly docked
        // half an hour of pay from every full-length manager shift and left the
        // whole cohort under contract.
        // A 17-hour day: two 9-paid-hour managers and their hour's handover are
        // exactly enough, so both are cut to the full cap.
        const double Max = 9;
        var slots = PlanShaper.ShapePlan(Input(Flat(5 * 60, 22 * 60, 3)) with
        {
            StaffOpen = 5 * 60,
            StaffClose = 22 * 60,
            TradingOpen = 8 * 60 + 30,
            TradingClose = 21 * 60,
            MgrRule = new ShaperRule(3, Max, "flat_30_unpaid"),
        }).Slots;
        var mgrs = slots.Where(s => s.Role == "MGR").ToList();
        Assert.Equal(2, mgrs.Count);
        foreach (var m in mgrs)
        {
            var clockHours = (m.End - m.Start) / 60;
            var paidHours = clockHours - Breaks.BreakForShift("flat_30_unpaid", clockHours).UnpaidMinutes / 60;
            Assert.True(paidHours <= Max, $"{m.Id} pays {paidHours}h - over the {Max}h cap");
        }
        // ...and the long side of the pair is cut to the FULL cap, not silently
        // shortened by half an hour to make room for a break that is already
        // outside the paid figure.
        Assert.Equal(Max + 0.5, mgrs.Max(m => m.End - m.Start) / 60);
        // The pair still spans the whole day with the mandatory handover.
        Assert.Equal(5 * 60, mgrs.Min(m => m.Start));
        Assert.Equal(22 * 60, mgrs.Max(m => m.End));
    }

    // ─── shapePlan - fixed rosters are reserved in the plan ──────────────────────
    // A fixed roster is a standing commitment: the person works it whatever the
    // plan says. Before this the shaper was blind to them, so the roster was
    // forced to drop the shift onto whichever slot happened to overlap - the
    // hours landed where the benchmark hadn't asked for them, and the slot the
    // shift displaced went short.

    private static readonly FixedShift Fixed = new(8 * 60, 17 * 60, "SA");

    [Fact]
    public void Emits_the_fixed_shift_as_a_slot_of_its_own()
    {
        var slots = PlanShaper.ShapePlan(Input(Flat(8 * 60, 17 * 60 + 30, 2)) with { FixedShifts = [Fixed] }).Slots;
        var reserved = slots.Where(s => s.Id.StartsWith("fixed-")).ToList();
        Assert.Single(reserved);
        Assert.Equal(Fixed.Start, reserved[0].Start);
        Assert.Equal(Fixed.End, reserved[0].End);
        Assert.Equal("SA", reserved[0].Role);
    }

    [Fact]
    public void Cuts_the_rest_of_the_curve_AROUND_it_instead_of_on_top_of_it()
    {
        // Same demand, with and without the commitment. Reserving it must not add
        // hours to the day - the fixed shift absorbs demand a cut slot would have
        // covered, so total planned WORK is unchanged. (A full day: on a short one
        // the managers' six-hour minimum and their hour-long handover - owner
        // decisions R7 and 5, which the trim may no longer cut - already cover
        // most of the demand, and any reserved shift adds hours.)
        var demand = Flat(6 * 60, 21 * 60, 2);
        var without = PlanShaper.ShapePlan(Input(demand));
        var withFixed = PlanShaper.ShapePlan(Input(demand) with { FixedShifts = [Fixed] });
        // Within one 15-min block: the commitment has a fixed span that rarely
        // lands exactly on the length the cutter would have chosen, so the residual
        // cut can differ by a block. What must NOT happen is the fixed hours being
        // added ON TOP of a full cut of the curve.
        Assert.True(Math.Abs(Worked(withFixed.Slots) - Worked(without.Slots)) <= 15);
        // ...and coverage still meets every block's demand.
        foreach (var d in withFixed.DemandByBlock)
        {
            var cover = CoverIn(withFixed, d.Time);
            Assert.True(cover >= d.Total, $"{d.Time} covered {cover} of {d.Total}");
        }
    }

    [Fact]
    public void Never_re_labels_a_reserved_shift_for_a_junior()
    {
        // The slot already belongs to a named adult - JSA labelling must skip it.
        var shortFixed = new FixedShift(16 * 60, 20 * 60, "SA");
        var slots = PlanShaper.ShapePlan(Input(EveningSpike()) with { FixedShifts = [shortFixed] }).Slots;
        Assert.Equal("SA", slots.First(s => s.Id.StartsWith("fixed-")).Role);
    }

    // ─── task-driven groups keep the placement Stage 1 gave them ─────────────────
    // Admin allowed all day (its labour-group window is wide), but Stage 1 put
    // 8 blocks of it before 08:30 because a named task is due by then.

    private static List<DemandBlock> TaskDemand()
    {
        var d = new List<DemandBlock>();
        for (double m = 6 * 60; m < 20 * 60; m += 15)
        {
            var early = m < 8 * 60 + 30;
            d.Add(new DemandBlock(m, early ? 4 : 3, early
                ? new JsMap<string, double> { ["admin"] = 1, ["load_running"] = 3 }
                : new JsMap<string, double> { ["load_running"] = 3 }));
        }
        return d;
    }

    private static readonly Func<double, IReadOnlyList<string>?> AllDayAllowed = _ => ["admin", "load_running"];

    private static double AdminBefore(ShapedDay s) => s.DemandByBlock.Where(b => b.Time < 8 * 60 + 30).Sum(b => b.GroupCount("admin"));

    [Fact]
    public void Levels_admin_away_from_its_deadline_when_it_is_NOT_pinned()
    {
        // The behaviour that put Monday's 08:30 date checks at 11:00: pooled
        // across admin's whole legal window, most of it leaves the morning.
        var s = PlanShaper.ShapePlan(Input(TaskDemand()) with { AllowedAt = AllDayAllowed });
        Assert.True(AdminBefore(s) < 10); // 10 blocks of it were placed there
    }

    [Fact]
    public void Leaves_admin_where_Stage_1_put_it_once_pinned()
    {
        var s = PlanShaper.ShapePlan(Input(TaskDemand()) with { AllowedAt = AllDayAllowed, PinnedGroups = ["admin"] });
        // 06:00-08:30 is ten 15-minute blocks, each carrying one admin body.
        Assert.Equal(10, AdminBefore(s));
    }

    [Fact]
    public void Still_levels_the_bulk_work_around_the_pinned_task()
    {
        var s = PlanShaper.ShapePlan(Input(TaskDemand()) with { AllowedAt = AllDayAllowed, PinnedGroups = ["admin"] });
        var lr = s.DemandByBlock.Sum(b => b.GroupCount("load_running"));
        var want = TaskDemand().Sum(b => b.GroupCount("load_running"));
        Assert.Equal(want, lr); // nothing lost, just redistributed
    }

    // ─── break cover in a window that closes early ───────────────────────────────
    // A morning load: five bodies of Load Running 06:00-10:00 and nothing else
    // until the store opens. Every one of those shifts owes an award break, and
    // the award forces it into the same window - no break in the first hour, and
    // a long shift's first segment in its first half. Cut to the demand alone,
    // the window has zero spare minutes and each break is an hour of load that
    // never gets run.

    private static List<DemandBlock> MorningLoad() => Merge(Flat(6 * 60, 10 * 60, 5, "load_running"), Flat(10 * 60, 20 * 60, 3, "checkout"));

    private static readonly Func<double, IReadOnlyList<string>?> LoadAllowed =
        t => t < 10 * 60 ? ["load_running"] : ["checkout", "cleaning"];

    [Fact]
    public void Covers_the_breaks_in_the_load_window_rather_than_lose_the_load_however_small_the_shortfall()
    {
        // "Add a body if necessary for load running" (U2) - and if necessary means
        // if necessary (owner decision 6). This used to weigh a three-hour body
        // against the shortfall and, below half a shift, plan the load short
        // instead: on G999 load running came out short on 8 of 14 days, most of
        // them with no flag (Roster Engine Review 07/11/2026, A01-7, A02-5). Every
        // shift here starts at 06:00, so there is nothing to stretch into the
        // window and a body is the only way to buy the slack.
        foreach (var load in new double[] { 5, 6 })
        {
            var demand = Merge(Flat(6 * 60, 10 * 60, load, "load_running"), Flat(10 * 60, 20 * 60, 3, "checkout"));
            var s = PlanShaper.ShapePlan(Input(demand) with { AllowedAt = LoadAllowed });
            Assert.DoesNotContain(s.Flags, f => f.Contains("planned short") || f.Contains("under benchmark"));
            // The window holds a body free for every break the award puts in it,
            // whoever that body is - an extra one, or the closing manager in early.
            double free = 0;
            for (double m = 6 * 60; m < 10 * 60; m += 15) free += Math.Max(0, CoverIn(s, m) - load);
            var breaks = s.Slots
                .Where(x => x.Start < 10 * 60)
                .Sum(x => Breaks.BreakForShift(x.Role == "MGR" ? "flat_30_unpaid" : "tiered", (x.End - x.Start) / 60).TotalMinutes / 15);
            Assert.True(free > 0, $"{load} on the load");
            Assert.True(free <= breaks);
        }
    }

    [Fact]
    public void Buys_the_extra_break_cover_the_painter_found_short()
    {
        // The shaper can only estimate where the painter's breaks will land, so
        // computeDaySlots paints each shape as a trial and passes back the load
        // it still lost. That much more slack goes into the window.
        static double Slack(ShapedDay s)
        {
            double n = 0;
            for (double m = 6 * 60; m < 10 * 60; m += 15) n += Math.Max(0, CoverIn(s, m) - 5);
            return n;
        }
        var @base = PlanShaper.ShapePlan(Input(MorningLoad()) with { AllowedAt = LoadAllowed });
        Assert.Equal<double?>(Slack(@base), @base.BreakSlack?.Get("load_running"));
        var more = PlanShaper.ShapePlan(Input(MorningLoad()) with
        {
            AllowedAt = LoadAllowed,
            MinBreakSlack = new JsMap<string, double> { ["load_running"] = Slack(@base) + 4 },
        });
        Assert.True(Slack(more) >= Slack(@base) + 4);
    }

    [Fact]
    public void Does_not_add_cover_for_a_group_whose_window_runs_to_the_end_of_the_day()
    {
        // Cleaning to close: a break at 19:00 has the rest of the evening to be
        // made up in, so this pass deliberately stays out of it.
        var s = PlanShaper.ShapePlan(Input(Flat(14 * 60, 20 * 60, 4, "cleaning")) with { AllowedAt = _ => ["cleaning"] });
        Assert.DoesNotContain(s.Slots, x => x.Id.StartsWith("sa-cover-"));
    }

    // ─── a load runner moves later rather than stand idle ────────────────────────
    // G193 11/11's curve: four on the load 07:00-10:00 (three load runners and
    // the opening manager), five at 09:45, then two to four through the day.
    // Every load shift is at the 3-hour minimum and the body bought for the
    // crew's breaks is pinned, so nothing could be shaved: the window carried a
    // quarter-hour more than the work and the breaks at every point, and the
    // opening manager stood idle five times before 10:00. One load runner now
    // comes in later and stays on past 10:00, where the assistant who would
    // have started then starts later.

    private static readonly double[] G193Curve =
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 2, 2, 2, 2, 2, 2, 2, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3,
    ];

    private static List<DemandBlock> G193Demand() =>
        G193Curve.Select((n, i) =>
        {
            var time = 7 * 60 + i * 15;
            return new DemandBlock(time, n, new JsMap<string, double> { [time < 10 * 60 ? "load_running" : "checkout"] = n });
        }).ToList();

    private static readonly Func<double, IReadOnlyList<string>?> G193Allowed = t => t < 10 * 60 ? ["load_running"] : ["checkout"];

    private static ShaperInput G193Input() =>
        Input(G193Demand()) with { AllowedAt = G193Allowed, StaffOpen = 7 * 60, TradingOpen = 8 * 60 + 30, TradingClose = 20 * 60 };

    private static double OverCover(ShapedDay s) => G193Demand().Sum(d => Math.Max(0, CoverIn(s, d.Time) - d.Total));

    [Fact]
    public void Moves_a_store_assistant_later_never_a_manager()
    {
        var s = PlanShaper.ShapePlan(G193Input() with { SlideIdle = true });
        var runner = s.Slots.FirstOrDefault(x => x.Role != "MGR" && x.Start > 7 * 60 && x.Start < 10 * 60 && x.End > 10 * 60);
        Assert.True(runner is not null, Show(s.Slots));
        Assert.Equal(7 * 60, s.Slots.First(x => x.Id == "mgr-open").Start);
    }

    [Fact]
    public void Leaves_the_cut_as_it_was_unless_asked()
    {
        // (computeDaySlots asks when a painted day has idle time.)
        var s = PlanShaper.ShapePlan(G193Input());
        Assert.Equal(21, OverCover(s));
    }

    [Fact]
    public void Carries_less_shift_time_with_no_work_and_keeps_every_rule_of_the_cut()
    {
        var s = PlanShaper.ShapePlan(G193Input() with { SlideIdle = true });
        // Before: twenty-one quarter-hours over the work, eleven of them in the
        // load window.
        Assert.True(OverCover(s) < 21);
        foreach (var x in s.Slots) Assert.True(x.End - x.Start >= (x.Role == "MGR" ? 360 : 180));
        foreach (var d in G193Demand()) Assert.True(CoverIn(s, d.Time) >= Math.Max(2, d.Total), $"{d.Time}");
    }

    // ─── the break allowance only grows into work ────────────────────────────────
    // A heavy morning load and a thin trading day after it - G248's Monday in
    // miniature. The allowance used to run to the end of the day's demand span,
    // so shifts cut to finish with the load were grown straight past 10:00, the
    // block where the curve collapses from seven bodies to three. Those extra
    // quarter hours are unusable, and they cost twice: the shift crosses the
    // award's four-hour break tier, so a 15-minute entitlement becomes 30, and
    // the painter is left holding a shift with a hole in the middle of it.

    private static List<DemandBlock> HeavyMorning() => Merge(Flat(6 * 60, 10 * 60, 7, "load_running"), Flat(10 * 60, 20 * 60, 3, "checkout"));

    private static ShapedDay ShapeHeavyMorning() =>
        PlanShaper.ShapePlan(Input(HeavyMorning()) with { AllowedAt = G193Allowed, StaffOpen = 6 * 60, TradingOpen = 8 * 60 });

    [Fact]
    public void Does_not_leave_the_load_crew_standing_about_after_the_window_shuts()
    {
        var s = ShapeHeavyMorning();
        foreach (var d in HeavyMorning().Where(x => x.Time >= 10 * 60))
        {
            var cover = CoverIn(s, d.Time);
            Assert.True(cover <= d.Total + 2, $"{d.Time}: {cover} bodies for {d.Total}");
        }
    }

    [Fact]
    public void Still_funds_the_days_workload_and_adds_only_the_break_cover_it_names()
    {
        var s = ShapeHeavyMorning();
        var worked = Worked(s.Slots);
        var demanded = HeavyMorning().Sum(d => d.Total) * 15;
        // Cover bought for breaks is the one thing allowed above the workload,
        // and each piece of it is flagged with its size.
        double named = 0;
        foreach (var f in s.Flags)
        {
            var body = Regex.Match(f, @"^Extra body (\d\d):(\d\d)-(\d\d):(\d\d)");
            if (body.Success)
            {
                named += Js.Number(body.Groups[3].Value) * 60 + Js.Number(body.Groups[4].Value) - Js.Number(body.Groups[1].Value) * 60 - Js.Number(body.Groups[2].Value);
                continue;
            }
            var grown = Regex.Match(f, @"^([\d.]+)h added");
            if (grown.Success) named += Js.Number(grown.Groups[1].Value) * 60;
        }
        // The grow pass puts back exactly what the capped allowance no longer
        // pads in, to within a block.
        Assert.True(worked >= demanded - 15);
        Assert.True(worked - demanded <= named + 15);
    }

    // ─── the pre-open keeps Stage 1's placement (Roster Engine Review 07/11/2026, RC3)
    // A Monday: a manager alone on the date checks from 06:15 (due by 08:30),
    // load running from its calculated start of 07:00, trading from 08:30.

    private static List<DemandBlock> Monday() =>
        Merge(Flat(6 * 60 + 15, 8 * 60 + 30, 1, "admin"), Flat(7 * 60, 10 * 60, 3, "load_running"), Flat(8 * 60 + 30, 20 * 60, 3, "checkout"));

    private static readonly Func<double, IReadOnlyList<string>?> MondayWindows =
        t => t >= 7 * 60 && t < 10 * 60 ? ["admin", "checkout", "load_running"] : ["admin", "checkout"];

    private static ShapedDay ShapeMonday() =>
        PlanShaper.ShapePlan(Input(Monday()) with { TradingOpen = 8 * 60 + 30, AllowedAt = MondayWindows, PinnedGroups = ["admin"] });

    [Fact]
    public void Leaves_the_date_checks_where_Stage_1_put_them_and_the_load_at_its_start_time()
    {
        var shaped = ShapeMonday();
        DemandBlock? At(double m) => shaped.DemandByBlock.FirstOrDefault(d => d.Time == m);
        // Two hours and a quarter of admin before 08:30, starting 06:15.
        Assert.Equal(9, shaped.DemandByBlock.Where(d => d.Time < 8 * 60 + 30).Sum(d => d.GroupCount("admin")));
        Assert.Equal<double?>(1, At(6 * 60 + 15)?.Groups.Get("admin"));
        // No load running before 07:00.
        Assert.DoesNotContain(shaped.DemandByBlock, d => d.Time < 7 * 60 && d.GroupCount("load_running") > 0);
    }

    [Fact]
    public void Brings_a_second_person_in_with_the_opening_manager_rather_than_moving_the_work()
    {
        var shaped = ShapeMonday();
        var opener = shaped.Slots.First(s => s.Id == "mgr-open");
        Assert.Equal(6 * 60 + 15, opener.Start);
        Assert.Contains(shaped.Slots, s => !ReferenceEquals(s, opener) && s.Start <= 6 * 60 + 15);
    }

    // ─── levelling keeps the hours Stage 1 placed on purpose ─────────────────────
    // G999's shape: tills all day, a morning load, cleaning's quoted hour at
    // 09:00-10:00 and its open window from 18:00, where the floor is quiet.

    private static List<DemandBlock> G999Day() =>
        Merge(
            Flat(8 * 60, 20 * 60, 2, "checkout"),
            Flat(7 * 60, 10 * 60, 2, "load_running"),
            Flat(9 * 60, 10 * 60, 1, "cleaning"),
            Flat(18 * 60, 20 * 60, 1, "cleaning"));

    private static IReadOnlyList<string> G999Windows(double t)
    {
        var out_ = new List<string>();
        if (t >= 8 * 60 && t < 20 * 60) out_.Add("checkout");
        if (t >= 7 * 60 && t < 10 * 60) out_.Add("load_running");
        if ((t >= 9 * 60 && t < 10 * 60) || t >= 18 * 60) out_.Add("cleaning");
        return out_;
    }

    private static readonly QuotedWindow Quote = new("cleaning", 9 * 60, 10 * 60, 1);

    private static double Units(ShapedDay s, string g, double from, double to) =>
        s.DemandByBlock.Where(d => d.Time >= from && d.Time < to).Sum(d => d.GroupCount(g));

    [Fact]
    public void Sends_the_quoted_hour_to_the_quiet_evening_when_it_does_not_know_the_quote()
    {
        // The fault (Roster Engine Review 07/11/2026, RC4): pooled with the rest
        // of the cleaning, the hour goes wherever cleaning's window is quietest.
        var s = PlanShaper.ShapePlan(Input(G999Day()) with { AllowedAt = G999Windows });
        Assert.Equal(0, Units(s, "cleaning", 9 * 60, 10 * 60));
    }

    [Fact]
    public void Keeps_a_quoted_windows_hours_inside_it_and_loses_none()
    {
        var s = PlanShaper.ShapePlan(Input(G999Day()) with { AllowedAt = G999Windows, QuotedWindows = [Quote] });
        Assert.Equal(4, Units(s, "cleaning", 9 * 60, 10 * 60));
        Assert.Equal(12, Units(s, "cleaning", 0, 24 * 60));
    }

    [Fact]
    public void Leaves_work_Stage_1_put_outside_its_groups_window_where_it_is()
    {
        // The load moved in from tomorrow's public holiday: Stage 1 puts it after
        // 18:00, beyond Load Running's own window - which is exactly where pooling
        // used to send it back to.
        var eve = Merge(G999Day(), Flat(18 * 60, 19 * 60, 2, "load_running"));
        var s = PlanShaper.ShapePlan(Input(eve) with { AllowedAt = G999Windows });
        Assert.Equal(8, Units(s, "load_running", 18 * 60, 24 * 60));
        Assert.Equal(32, Units(s, "load_running", 0, 24 * 60));
    }

    [Fact]
    public void Does_not_re_stack_a_pre_open_that_holds_a_quoted_windows_hours()
    {
        // One person cleaning 06:00-07:00 - a quoted hour - before the doors open
        // at 08:00. Re-stacking the pre-open for a second body would deal the
        // hour out against the open, outside its window.
        var early = Merge(Flat(6 * 60, 7 * 60, 1, "cleaning"), Flat(8 * 60, 20 * 60, 2, "checkout"));
        var s = PlanShaper.ShapePlan(Input(early) with
        {
            AllowedAt = t => t >= 8 * 60 ? ["cleaning", "checkout"] : ["cleaning"],
            QuotedWindows = [new QuotedWindow("cleaning", 6 * 60, 7 * 60, 1)],
        });
        Assert.Equal(4, Units(s, "cleaning", 6 * 60, 7 * 60));
    }

    // ─── evening Cleaning in the close-down after a 21:00 close (owner, 14/11/2026)
    // Doors close at 21:00, the close-down runs to 21:30 with two people on
    // (decision 2) and only the manager's End of Day to do. The evening
    // Cleaning window now runs to 21:30, so the second person has Cleaning -
    // but no more than that: levelling filled the quiet close-down three deep
    // and kept three assistants on past the close (a junior's evening shift
    // went to an adult, and the roster reopened a morning slot).

    private static List<DemandBlock> Late() =>
        Merge(
            Flat(8 * 60, 21 * 60, 2, "checkout"),
            Flat(18 * 60, 21 * 60, 1, "reset"),
            Flat(21 * 60, 21 * 60 + 30, 1, "admin"),
            Flat(20 * 60, 21 * 60 + 30, 2, "cleaning"));

    private static IReadOnlyList<string> LateWindows(double t)
    {
        var out_ = new List<string>();
        if (t >= 8 * 60 && t < 21 * 60) out_.Add("checkout");
        if (t >= 18 * 60 && t < 21 * 60) out_.Add("reset");
        if (t >= 18 * 60)
        {
            out_.Add("cleaning");
            out_.Add("admin");
        }
        return out_;
    }

    private static double At(ShapedDay s, string g, double t) => s.DemandByBlock.FirstOrDefault(d => d.Time == t)?.GroupCount(g) ?? 0;

    private static ShaperInput LateInput() =>
        Input(Late()) with { TradingClose = 21 * 60, StaffClose = 21 * 60 + 30, AllowedAt = LateWindows, PinnedGroups = ["admin"] };

    [Fact]
    public void Levels_in_only_enough_Cleaning_for_the_second_person_kept_in()
    {
        var s = PlanShaper.ShapePlan(LateInput() with { CloseDownFrom = 21 * 60 });
        foreach (var t in new double[] { 21 * 60, 21 * 60 + 15 })
        {
            Assert.Equal(1, At(s, "admin", t));
            Assert.Equal(1, At(s, "cleaning", t));
        }
        // No Cleaning is lost: what does not fit the close-down is done before it.
        Assert.Equal(12, s.DemandByBlock.Sum(d => d.GroupCount("cleaning")));
    }

    [Fact]
    public void Is_levelled_as_before_where_no_window_was_run_on_into_the_close_down()
    {
        var s = PlanShaper.ShapePlan(LateInput());
        Assert.True(At(s, "cleaning", 21 * 60) > 1);
    }
}
