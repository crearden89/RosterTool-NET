// Port of src/lib/stage2/g999.audit.ts.
// ─── G999 audit harness (pure, shared by tests) ──────────────────────────────
// Reconstructs the fortnight engine's inputs from the frozen real-store
// fixture EXACTLY the way the compute layer does from the database (leave,
// weekend-off markers, granted day-offs, fixed rosters, school days), runs the
// real RunFortnightStage2, and summarises where every person lands against
// contract. Scenarios let a test vary one input - e.g. clear the weekend
// markers - to separate "the engine failed" from "the inputs make it
// impossible".

using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public sealed record AuditScenario
{
    /// <summary>Weekend markers in force (default: the fixture's, as saved by the manager).</summary>
    public IReadOnlyList<G999Marker>? Markers { get; init; }
    public IReadOnlyList<G999Leave>? Leave { get; init; }
}

public sealed record PersonOutcome
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    /// <summary>Fortnight hours.</summary>
    public double Contract { get; init; }
    public double LeaveCredit { get; init; }
    public double Paid { get; init; }
    public int Days { get; init; }
    /// <summary>contract - paid - leaveCredit (positive = under).</summary>
    public double Gap { get; init; }
}

public sealed record AuditUnfilled(string Date, double Start, double End);

public sealed record AuditRun(
    FortnightResult Result,
    List<DayContext> Days,
    List<FortnightEmp> Employees,
    List<PersonOutcome> Outcomes,
    List<AuditUnfilled> Unfilled,
    /// <summary>Σ positive gaps, adults only.</summary>
    double TotalShortfall);

public static class G999Audit
{
    private static string AddDays(string iso, int n) => DateOnlyExtensions.ParseIso(iso).AddDays(n).ToIso();

    private static bool Le(string a, string b) => string.CompareOrdinal(a, b) <= 0;

    public static (List<DayContext> Days, List<FortnightEmp> Employees, Dictionary<string, BreakRule> BreakRules) BuildDays(AuditScenario? scenario = null)
    {
        var markers = scenario?.Markers ?? G999Fixture.Markers;
        var leave = scenario?.Leave ?? G999Fixture.Leave;
        var employees = G999Fixture.Employees.Select(e => new FortnightEmp
        {
            Id = e.Id,
            Name = e.Name,
            Role = e.Role,
            ContractHours = e.ContractHours,
            Restricted = [],
        }).ToList();
        var markerByKey = new JsMap<string, G999Marker>(markers.Select(m => ($"{m.EmployeeId}:{m.WeekendStart}", m)));
        var dayOffSet = new JsSet<string>(G999Fixture.DayOffs.Select(d => $"{d.EmployeeId}:{d.Date}"));
        var fixedByKey = new JsMap<string, G999Fixed>(G999Fixture.Fixed.Select(f => ($"{f.EmployeeId}:{f.Dow}", f)));

        DayAvail AvailFor(string empId, string date)
        {
            var dow = Period.DowMon0(DateOnlyExtensions.ParseIso(date));
            var onLeave = leave.Any(l => l.EmployeeId == empId && Le(l.Start, date) && Le(date, l.End));
            var weekendOff = false;
            if (dow == 5) weekendOff = markerByKey.Get($"{empId}:{date}")?.SatOff == true;
            else if (dow == 6) weekendOff = markerByKey.Get($"{empId}:{AddDays(date, -1)}")?.SunOff == true;
            var dayOff = dayOffSet.Has($"{empId}:{date}");
            var fx = fixedByKey.Get($"{empId}:{dow}");
            return new DayAvail
            {
                OnLeave = onLeave,
                LeaveDayHours = null, // fixture leave predates per-day amounts: full days
                Available = !onLeave && !weekendOff && !dayOff,
                FromMin = null,
                ToMin = null,
                Fixed = fx is not null ? new MinuteWindow(fx.Start, fx.End) : null,
            };
        }

        var days = G999Fixture.Days.Select((d, di) =>
        {
            // The fixture freezes the slot GEOMETRY the shaper cut from real benchmark
            // demand. Which of those slots a junior may take is a RULE, not geometry,
            // so re-apply the current labelling instead of preserving whatever the
            // snapshot happened to record - otherwise a rule fix (e.g. the JSA window
            // being clipped to trading hours) can't be seen in the audit.
            var slots = d.Slots.Select(s => s.ToPlanSlot()).ToList();
            PlanShaper.LabelJsaSlots(slots, G999Fixture.SchoolDays.Contains(d.Date), ShaperRule.From(G999Fixture.BreakRules["JSA"]));
            return new DayContext
            {
                Date = d.Date,
                WeekIndex = di < 7 ? 0 : 1,
                IsSchoolDay = G999Fixture.SchoolDays.Contains(d.Date),
                Closed = d.Closed,
                StaffOpen = d.StaffOpen,
                StaffClose = d.StaffClose,
                TradingOpen = d.TradingOpen,
                TradingClose = d.TradingClose,
                Slots = slots,
                // The slots' own work: the audit is about who fills them (see SlotDemand).
                DemandByBlock = FortnightKit.SlotDemand(slots),
                Avail = new JsMap<string, DayAvail>(employees.Select(e => (e.Id, AvailFor(e.Id, d.Date)))),
            };
        }).ToList();

        var breakRules = G999Fixture.BreakRules.ToDictionary(kv => kv.Key, kv => kv.Value with { });
        return (days, employees, breakRules);
    }

    public static AuditRun RunAudit(AuditScenario? scenario = null)
    {
        var (days, employees, breakRules) = BuildDays(scenario);
        var result = FortnightEngine.RunFortnightStage2(new FortnightInput { Days = days, Employees = employees, BreakRules = breakRules });

        var paid = new JsMap<string, double>();
        var dayCount = new JsMap<string, int>();
        var workedDays = new JsMap<string, JsSet<int>>();
        var unfilled = new List<AuditUnfilled>();
        for (var di = 0; di < days.Count; di++)
        {
            var day = days[di];
            var r = result.ByDate.Get(day.Date);
            if (r is null) continue;
            foreach (var s in r.Shifts)
            {
                if (s.Unfilled)
                {
                    unfilled.Add(new AuditUnfilled(day.Date, s.Start, s.End));
                    continue;
                }
                var rule = breakRules[s.Role];
                double unpaidMin =
                    rule.BreakType == "flat_30_unpaid"
                        ? 30
                        : (s.End - s.Start) / 60 <= 4
                            ? 0
                            : (s.End - s.Start) / 60 <= 6
                                ? 10
                                : 30;
                paid.Set(s.EmployeeId, paid.GetOr(s.EmployeeId, 0) + (s.End - s.Start - unpaidMin) / 60);
                dayCount.Set(s.EmployeeId, dayCount.GetOr(s.EmployeeId, 0) + 1);
                if (!workedDays.Has(s.EmployeeId)) workedDays.Set(s.EmployeeId, new JsSet<int>());
                workedDays[s.EmployeeId].Add(di);
            }
        }

        var outcomes = Js.Sort(
            employees
                .Where(e => e.Role != "JSA")
                .Select(e =>
                {
                    var credit =
                        FortnightEngine.LeaveHoursFor(e, days) +
                        FortnightEngine.PhCreditFor(e, days, di => workedDays.Get(e.Id)?.Has(di) ?? false);
                    var p = paid.GetOr(e.Id, 0);
                    return new PersonOutcome
                    {
                        Name = e.Name,
                        Role = e.Role,
                        Contract = e.ContractHours,
                        LeaveCredit = credit,
                        Paid = Js.Round(p * 100) / 100,
                        Days = dayCount.GetOr(e.Id, 0),
                        Gap = Js.Round((e.ContractHours - p - credit) * 100) / 100,
                    };
                })
                .ToList(),
            (a, b) => b.Gap - a.Gap);

        var totalShortfall = outcomes.Aggregate(0.0, (s, o) => s + Math.Max(0, o.Gap));
        return new AuditRun(result, days, employees, outcomes, unfilled, totalShortfall);
    }

    // Pretty console table for the audit tests' output.
    public static string FormatOutcomes(AuditRun run)
    {
        var lines = run.Outcomes.Select(o =>
            $"{o.Name.PadRight(24)} {o.Role.PadRight(4)} contract {Js.NumberToString(o.Contract).PadLeft(5)} " +
            $"paid {Js.ToFixed(o.Paid, 1).PadLeft(6)} leave {Js.ToFixed(o.LeaveCredit, 1).PadLeft(5)} " +
            $"days {o.Days.ToString().PadLeft(2)}  gap {(o.Gap >= 0 ? "+" : "")}{Js.ToFixed(o.Gap, 1)}").ToList();
        var unfilled = string.Join(", ", run.Unfilled.Select(u => $"{u.Date} {Js.NumberToString(u.Start / 60)}-{Js.NumberToString(u.End / 60)}"));
        lines.Add(
            $"TOTAL shortfall {Js.ToFixed(run.TotalShortfall, 1)}h; unfilled {run.Unfilled.Count} " +
            $"({(unfilled.Length > 0 ? unfilled : "none")})");
        return string.Join("\n", lines);
    }

    // Per-day grid for one person plus who holds each day's longest slots - the
    // debugging view for "why is this person stuck short".
    public static string FormatPersonDays(AuditRun run, string name)
    {
        var emp = run.Employees.Find(e => e.Name == name);
        if (emp is null) return $"{name}: not found";
        var lines = new List<string>();
        foreach (var day in run.Days)
        {
            var r = run.Result.ByDate.Get(day.Date);
            if (r is null) continue;
            var mine = r.Shifts.Find(s => s.EmployeeId == emp.Id);
            var longest = string.Join(", ", Js.Sorted(r.Shifts.Where(s => !s.Unfilled), (a, b) => b.End - b.Start - (a.End - a.Start))
                .Take(2)
                .Select(s => $"{s.Name.Split(' ')[0]} {Js.NumberToString((s.End - s.Start) / 60)}h[{s.SlotRole}]"));
            var avail = day.Avail.Get(emp.Id);
            var mineText = mine is not null
                ? $"{Js.NumberToString(mine.Start / 60)}-{Js.NumberToString(mine.End / 60)} ({Js.NumberToString((mine.End - mine.Start) / 60)}h {mine.SlotRole})"
                : avail?.Available == true ? "—" : "unavail";
            lines.Add($"{day.Date} {(day.Closed ? "CLOSED" : "")} mine={mineText}  longest: {longest}");
        }
        return string.Join("\n", lines);
    }
}
