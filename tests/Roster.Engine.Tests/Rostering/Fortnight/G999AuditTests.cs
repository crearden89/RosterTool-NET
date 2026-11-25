// Port of src/lib/stage2/g999.audit.test.ts.
// ─── The G999 fortnight, replayed with the real inputs ───────────────────────
// The real engine over the frozen real-store fixture (G999Fixture). The live
// scenario is exactly what the manager sees on Regenerate; the cleared-markers
// scenario removes every weekend-off switch to separate engine behaviour from
// input constraints. The bounds below are REGRESSIONS: they pin the
// fortnight-repair pass's measured performance on true data, a little above
// the achieved figures so incidental drift doesn't flake, and they encode the
// audit's structural findings:
//
//   - Harvey (45h/wk) has only NINE workable days with his week-2 weekend off
//     and the closed Wednesday, and 9 days x 9 paid = 81 < 90: his contract
//     is arithmetically unreachable under those markers.
//   - EVERY slot is now covered in both scenarios. The last holdout - 08/08
//     14:00-19:30 - needed the step-across rescue: no junior could take that
//     late slot (adult presence thins there), and every adult already worked
//     the day, so it took moving an adult ONTO it and giving a junior the
//     midday shift they vacated.
//   - Grace's fixed roster pays ~51h against a 30h fortnight contract; the
//     ~21h overhang is locked and must come out of someone else's contract.
//   - Leave over the CLOSED Wednesday (12/08) credits nothing - client
//     decision ("leave them short for now"). Stuart (20h/wk), Rhys and
//     Ella (25h/wk each) are on leave across it, so 4+5+5 = 14h of
//     shortfall is by-design in BOTH scenarios.
//
// If a change makes these bounds fail LOW (better), tighten them.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;
using Xunit.Abstractions;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

/// <summary>The two audit runs, each made once and shared by the tests that read it.</summary>
internal static class G999Runs
{
    private static readonly Lazy<AuditRun> live = new(() => G999Audit.RunAudit());
    private static readonly Lazy<AuditRun> cleared = new(() => G999Audit.RunAudit(new AuditScenario { Markers = [] }));

    public static AuditRun Live => live.Value;
    public static AuditRun Cleared => cleared.Value;
}

public class G999AuditLiveMarkersTests(ITestOutputHelper log)
{
    private static AuditRun Run => G999Runs.Live;

    [Fact]
    public void Prints_the_outcome_table()
    {
        log.WriteLine("── live markers ──\n" + G999Audit.FormatOutcomes(Run));
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIT_DEBUG")))
            log.WriteLine("Harvey days:\n" + G999Audit.FormatPersonDays(Run, "Harvey Chen"));
        Assert.NotEmpty(Run.Outcomes);
    }

    [Fact]
    public void Keeps_the_total_shortfall_inside_the_audited_envelope()
    {
        // Greedy-only was 51.3h. Structural floor (Harvey ceiling + Saturday
        // shortage + fixed-roster overhang interplay) is ~21h, plus the 14h of
        // by-design closed-day leave shortfall. The phase engine's approved
        // hardest-days-first fill order plateaus ~5h above the old chronological
        // fill under these live markers (measured 53.8) while covering EVERY slot
        // - the coverage guarantee outranks the last few contract hours.
        Assert.True(Run.TotalShortfall <= 55, $"{Run.TotalShortfall}");
    }

    [Fact]
    public void Covers_every_slot_even_under_the_restrictive_live_markers() =>
        Assert.True(Run.Unfilled.Count == 0, string.Join(", ", Run.Unfilled));

    [Fact]
    public void Keeps_the_3_adult_floor_around_every_junior_shift()
    {
        // The rescue moves may not buy coverage by breaking the safety rule.
        foreach (var day in Run.Days)
        {
            var r = Run.Result.ByDate.Get(day.Date);
            if (r is null) continue;
            int AdultsAt(double m) => r.Shifts.Count(x => !x.Unfilled && x.Role != "JSA" && x.Start <= m && m < x.End);
            foreach (var s in r.Shifts)
            {
                if (s.Unfilled || s.Role != "JSA") continue;
                for (var m = s.Start; m < s.End; m += 15)
                    Assert.True(AdultsAt(m) >= 3, $"{day.Date} {m / 60}h beside {s.Name}");
            }
        }
    }

    [Fact]
    public void Keeps_every_DSM_within_a_shift_of_contract()
    {
        foreach (var o in Run.Outcomes)
            if (o.Role == "DSM") Assert.True(o.Gap <= 7, $"{o.Name} gap {o.Gap}");
    }

    [Fact]
    public void Credits_leave_on_every_OPEN_weekday_the_closed_Wednesday_leaves_one_day_short()
    {
        // Full-period leave used to land exactly on contract. The closed 12/08 now
        // credits nothing (client decision), so each is short precisely one
        // notional day - contract/5 - and no more.
        foreach (var name in new[] { "Ella Bishop", "Rhys Brooks", "Stuart Ramsay" })
        {
            var o = Run.Outcomes.Find(x => x.Name == name)!;
            var oneDay = o.Contract / 2 / 5;
            Assert.True(Math.Abs(o.Gap - oneDay) <= 0.05, name);
        }
    }
}

public class G999AuditClearedMarkersTests(ITestOutputHelper log)
{
    private static AuditRun Run => G999Runs.Cleared;

    [Fact]
    public void Prints_the_outcome_table()
    {
        log.WriteLine("── markers cleared ──\n" + G999Audit.FormatOutcomes(Run));
        Assert.NotEmpty(Run.Outcomes);
    }

    [Fact]
    public void Fills_every_slot_when_weekend_availability_opens_up() => Assert.Empty(Run.Unfilled);

    [Fact]
    public void Shrinks_the_shortfall_to_about_the_fixed_roster_overhang_plus_closed_day_leave()
    {
        // Grace's locked fixed roster PAYS ~51h against a 30h contract. It sits
        // almost exactly on real plan slots (only 2.8h above plan across the
        // fortnight), so it is not over-cover - but those ~21h of slot time are
        // spent on someone who needs 30, and cannot go to anyone else's contract.
        // Greedy-only left 41.1h on the table. The other 14h is the by-design
        // closed-Wednesday leave shortfall.
        Assert.True(Run.TotalShortfall <= 35, $"{Run.TotalShortfall}");
    }

    [Fact]
    public void Lands_every_manager_within_a_single_shift_of_contract()
    {
        foreach (var o in Run.Outcomes)
            if (o.Role is "SM" or "ASM" or "DSM" or "RSM") Assert.True(o.Gap <= 9, $"{o.Name} gap {o.Gap}");
    }
}

public class G999AuditCasualsTests
{
    [Fact]
    public void Fills_the_15_08_junior_slot_rather_than_leaving_it_open()
    {
        // Week 2 reads over-resourced (plan 309.8 paid vs 326.0 rosterable adult
        // contract), so the casual bench is ON - correctly, since every plan hour
        // should go to fulfilling adult contracts. It must never mean an EMPTY
        // slot though: the 15/08 11:45-17:45 shift is covered either by an adult
        // down-covering or, failing that, by a junior.
        var day = G999Runs.Live.Result.ByDate["2026-08-15"];
        var slot = day.Shifts.Find(s => s.Start == 705 && s.End == 1065);
        Assert.True(slot is not null, "the 15/08 slot vanished from the roster");
        Assert.False(slot.Unfilled, "slot left unfilled");
    }

    [Fact]
    public void Prefers_an_adult_over_a_casual_wherever_one_can_legally_take_the_slot()
    {
        // Juniors are the last resort, never the convenient one: every junior
        // shift must be on a slot no available adult could have held.
        var run = G999Runs.Live;
        foreach (var day in run.Days)
        {
            var r = run.Result.ByDate.Get(day.Date);
            foreach (var s in r?.Shifts ?? [])
            {
                if (s.Unfilled || s.Role != "JSA") continue;
                var idleAdult = run.Employees.Find(e =>
                    e.Role != "JSA" &&
                    day.Avail.Get(e.Id)?.Available == true &&
                    !r!.Shifts.Any(x => !x.Unfilled && x.EmployeeId == e.Id));
                Assert.True(idleAdult is null, $"{day.Date}: {idleAdult?.Name} was free but a casual worked");
            }
        }
    }

    [Fact]
    public void Covers_the_thin_late_08_08_slot_with_an_adult_and_puts_a_junior_to_work()
    {
        // The client's own suggestion, generalised. 08/08 is the tightest day of
        // the fortnight (leave + weekend markers + a day-off leave only four
        // floor adults). The late slot must never go to a junior (adult presence
        // thins after 18:00) and must never stay open - an adult holds it, with
        // the junior-eligible shifts they free picked up by juniors. WHO ends up
        // where is the engine's choice and shifts with the fill order; the
        // invariants are what this test pins.
        var r = G999Runs.Live.Result.ByDate["2026-08-08"];
        var late = r.Shifts.Find(s => s.Start == 14 * 60 && s.End == 19.5 * 60);
        Assert.True(late is not null, "the late slot vanished");
        Assert.False(late.Unfilled);
        Assert.True(late.Role != "JSA", "a junior should never hold the thin late slot");
        // ...and the day still uses at least one junior on an eligible shift.
        Assert.Contains(r.Shifts, s => !s.Unfilled && s.Role == "JSA");
    }

    [Fact]
    public void Lets_a_junior_take_a_pre_open_slot()
    {
        // Juniors may not start before 06:00 - but a 06:00 start is fine, even
        // though the store does not open to customers until 08:30. Clipping the
        // window to tradingOpen barred juniors from every early shift.
        var rules = new Dictionary<string, BreakRule>
        {
            ["SM"] = Rule(breakType: "flat_30_unpaid", maxShiftHours: 12, ot15AfterHours: null),
            ["ASM"] = Rule(breakType: "flat_30_unpaid", maxShiftHours: 12, ot15AfterHours: null),
            ["DSM"] = Rule(breakType: "flat_30_unpaid", maxShiftHours: 9, ot15AfterHours: null),
            ["RSM"] = Rule(maxShiftHours: 9, ot15AfterHours: null),
            ["SA"] = Rule(maxShiftHours: 9, ot15AfterHours: null),
            ["JSA"] = Rule(maxShiftHours: 6, ot15AfterHours: null),
        };
        var dates = IsoDates();
        var ids = new[] { "a1", "a2", "a3", "j" };
        List<PlanSlot> Slots(int i) =>
        [
            Slot($"d{i}-a", "SA", 6 * H, 15 * H),
            Slot($"d{i}-b", "SA", 6 * H, 15 * H),
            Slot($"d{i}-c", "SA", 6 * H, 15 * H),
            // Pre-open and junior-length, so the planner labels it a JUNIOR slot.
            // (It used to be built as an adult "SA" slot that the junior rescue then
            // handed to the junior - the very move that put juniors on registers.)
            Slot($"d{i}-early", "JSA", 6 * H, 10.5 * H),
        ];
        // ONE open day: four slots, three adults, one junior. The adults take the
        // three long slots, leaving the pre-open junior slot - and the point of the
        // test is that its 06:00 start does not bar the junior.
        var days = dates.Select((d, i) =>
            OpenDay(d, i, i == 0 ? Slots(i) : [], ids) with
            {
                TradingOpen = 8.5 * H, // store opens to customers at 08:30
                Closed = i != 0,
            }).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("a1", "SA", 40), Emp("a2", "SA", 40), Emp("a3", "SA", 40), Emp("j", "JSA", 0)], rules));
        var juniorShifts = 0;
        var unfilled = 0;
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled) unfilled++;
                else if (s.EmployeeId == "j")
                {
                    juniorShifts++;
                    Assert.True(s.Start >= 6 * H, "junior started before 06:00");
                }
            }
        }
        Assert.True(juniorShifts > 0, "the junior was barred from the pre-open slot");
        Assert.Equal(0, unfilled);
    }
}
