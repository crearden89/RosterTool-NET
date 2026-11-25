// Port of the pure parts of app/(app)/setup/staff/page.tsx (the rank order and
// the fixed rosters' paid hours), StaffClient.tsx and FixedRosters.tsx (the
// lists and labels they show), and the checks of setup/staff/actions.ts and
// setup/requests/actions.ts (fixedShiftRefusal, syncWeekendMarker's merge), so
// the page, the service and its stand-in all apply the same rules.

using System.Globalization;
using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Web.Features.Team;

/// <summary>How a weekday of availability reads: all day, part of the day, or not at all.</summary>
public enum AvailabilityMode
{
    All,
    Window,
    Off,
}

/// <summary>One square of the availability strip: the weekday, its mode and the words for it.</summary>
public sealed record AvailabilitySquare(string Day, AvailabilityMode Mode, string Text);

/// <summary>A leave type in the editor's picker ("ANN - Annual").</summary>
public sealed record LeaveTypeOption(string Code, string Label);

/// <summary>The positions of one base role, for an &lt;optgroup&gt; in the position pickers.</summary>
public sealed record PositionGroup(string Role, string Label, IReadOnlyList<PositionOption> Items);

public static partial class TeamRules
{
    // ─── The team's order (page.tsx) ─────────────────────────────────────────
    // UKG names carry a rank prefix ("4-Nye, Harper"), and the team list follows
    // it ascending (1 → 6) with lettered variants (5L, 6C) after their base rank.
    // A name without a prefix (typed in by hand) slots in at the rank UKG would
    // give its role, so mixed teams still read top-down by seniority.

    private static readonly IReadOnlyDictionary<string, double> RoleRank = new Dictionary<string, double>
    {
        ["SM"] = 1,
        ["ASM"] = 2,
        ["DSM"] = 3,
        ["RSM"] = 3.5,
        ["SA"] = 4,
        ["JSA"] = 6,
    };

    // JavaScript's \d is ASCII only.
    [GeneratedRegex("^([0-9]+)([A-Za-z]*)-")]
    private static partial Regex RankPrefix();

    /// <summary>The rank a name's UKG prefix gives ("5L-..." is 5, "L"), else its role's rank.</summary>
    public static (double Rank, string Variant) RankOf(string name, string role)
    {
        var m = RankPrefix().Match(name);
        return m.Success
            ? (Js.Number(m.Groups[1].Value), m.Groups[2].Value.ToUpperInvariant())
            : (RoleRank.TryGetValue(role, out var r) ? r : 9, "");
    }

    /// <summary>The team in rank order, then variant, then name.</summary>
    public static List<TeamMember> SortTeam(IEnumerable<TeamMember> employees) =>
        Js.Sorted(employees, (a, b) =>
        {
            var (na, sa) = RankOf(a.Name, a.Role);
            var (nb, sb) = RankOf(b.Name, b.Role);
            return (na - nb).Or(Js.LocaleCompare(sa, sb)).Or(Js.LocaleCompare(a.Name, b.Name));
        });

    // ─── Fixed rosters' paid hours (page.tsx) ────────────────────────────────

    /// <summary>
    /// FORTNIGHTLY paid hours of each fixed roster, so the page can warn when a fixed
    /// roster commits more hours than the person's contract carries. For a multi-week
    /// cycle the total is averaged over the cycle and scaled to the fortnight - the
    /// rate the person is actually paid at, in the same unit the contract is stored in.
    /// </summary>
    public static Dictionary<Guid, double> FixedPaidHours(
        IEnumerable<TeamMember> employees,
        IEnumerable<FixedShift> fixedShifts,
        IEnumerable<FixedRosterCycleRow> cycles)
    {
        var roleById = new Dictionary<Guid, string>();
        foreach (var e in employees) roleById[e.Id] = e.Role;
        var cycleWeeksById = new Dictionary<Guid, int>();
        foreach (var c in cycles) cycleWeeksById[c.EmployeeId] = c.CycleWeeks;

        var paid = new Dictionary<Guid, double>();
        foreach (var f in fixedShifts)
        {
            var span = Minutes(f.EndTime) - Minutes(f.StartTime);
            var unpaid = Breaks.BreakForRole(roleById.GetValueOrDefault(f.EmployeeId) ?? Roles.SA, span / 60).UnpaidMinutes;
            paid[f.EmployeeId] = paid.GetValueOrDefault(f.EmployeeId) + Math.Max(0, span - unpaid) / 60;
        }
        foreach (var id in paid.Keys.ToList())
            paid[id] = paid[id] / (cycleWeeksById.TryGetValue(id, out var w) ? w : 1) * 2;
        return paid;
    }

    /// <summary>
    /// A fixed roster pays more than the contract: the warning on a person's row, in the
    /// editor and in the sheet. Casuals (JSA) carry no contract, so never.
    /// </summary>
    public static bool PaysOverContract(string role, double contractHours, double fixedPaid) =>
        role != Roles.JSA && fixedPaid > contractHours + 0.01;

    // ─── What the page lists (StaffClient.tsx, FixedRosters.tsx) ─────────────

    public static readonly IReadOnlyList<LeaveTypeOption> LeaveTypes =
    [
        new("ANN", "ANN - Annual"),
        new("LS", "LS - Long Service"),
        new("BRV", "BRV - Bereavement"),
        new("SCK", "SCK - Sick"),
        new("CAR", "CAR - Carer's"),
        new("UNP", "UNP - Unpaid"),
    ];

    // Leave codes in words, for the switch beside each booking.
    private static readonly IReadOnlyDictionary<string, string> LeaveWordsByCode = new Dictionary<string, string>
    {
        ["ANN"] = "Annual leave",
        ["LS"] = "Long service",
        ["BRV"] = "Bereavement",
        ["SCK"] = "Sick leave",
        ["CAR"] = "Carer's leave",
        ["UNP"] = "Unpaid leave",
    };

    /// <summary>A leave code in words ("Annual leave"); null for a code it doesn't know.</summary>
    public static string? LeaveWords(string leaveType) => LeaveWordsByCode.GetValueOrDefault(leaveType);

    /// <summary>The labour groups a person can be restricted from, in the editor's order.</summary>
    public static readonly IReadOnlyList<string> Restrictable =
        ["registers", "scos", "load_running", "reset", "admin", "specials", "cleaning", "non_op"];

    /// <summary>Positions grouped by base role for the &lt;optgroup&gt; in every position picker.</summary>
    public static List<PositionGroup> GroupPositions(IEnumerable<PositionOption> positions)
    {
        var list = positions.ToList();
        string[] order = [Roles.SM, Roles.ASM, Roles.DSM, Roles.RSM, Roles.SA, Roles.JSA];
        return order
            .Select(role => new PositionGroup(role, Roles.Labels.GetValueOrDefault(role) ?? role, list.Where(p => p.BaseRole == role).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    /// <summary>
    /// One person's week, Monday to Sunday, from the rows the editor reads: a day with no
    /// row (or an open one) is available all day, a window is part of the day, and
    /// Available = false is a day they can't work.
    /// </summary>
    public static List<AvailabilitySquare> AvailabilityDays(IEnumerable<AvailabilityRow> rows)
    {
        var list = rows.ToList();
        return Period.DayLabels.Select((day, i) =>
        {
            var row = list.FirstOrDefault(a => a.DayOfWeek == i);
            if (row is not null && !row.Available) return new AvailabilitySquare(day, AvailabilityMode.Off, $"{day} not available");
            if (row is { FromTime: { } from, ToTime: { } to })
                return new AvailabilitySquare(day, AvailabilityMode.Window, $"{day} {Hm(from)}–{Hm(to)} only");
            return new AvailabilitySquare(day, AvailabilityMode.All, $"{day} all day");
        }).ToList();
    }

    /// <summary>The cycle lengths a fixed roster may have.</summary>
    public static readonly IReadOnlyList<int> CycleChoices = [1, 2, 4];

    /// <summary>"Every week", or "2-week cycle".</summary>
    public static string CycleLabel(int weeks) => weeks == 1 ? "Every week" : $"{weeks}-week cycle";

    // ─── The actions' checks (actions.ts) ────────────────────────────────────

    /// <summary>setContractAndNotional's checks: null when the figures may be saved.</summary>
    public static string? ContractRefusal(double contractHours, double? notionalHours)
    {
        if (!(contractHours >= 0 && contractHours <= 76)) return "Contract hours must be between 0 and 76";
        if (notionalHours is { } n && !(n >= 0 && n <= 24)) return "Notional hours must be between 0 and 24";
        return null;
    }

    /// <summary>addLeave's checks: null when the booking may be saved.</summary>
    public static string? LeaveRefusal(string leaveType, double? dayHours)
    {
        if (!LeaveTypes.Any(t => t.Code == leaveType)) return $"Unknown leave type {leaveType}";
        if (dayHours is { } h && !(h > 0 && h <= 24)) return "Hours per day must be between 0 and 24";
        return null;
    }

    /// <summary>setFixedRosterCycle's checks on its arguments, before the shift rules.</summary>
    public static string? CycleRefusal(int cycleWeeks, DateOnly? startDate, IEnumerable<FixedDay> days)
    {
        if (!CycleChoices.Contains(cycleWeeks)) return "Cycle must be 1, 2 or 4 weeks";
        if (startDate is null) return "A cycle start date is required";
        foreach (var d in days)
        {
            if (d.WeekIndex < 0 || d.WeekIndex >= cycleWeeks)
                return $"Week {d.WeekIndex + 1} is outside the {cycleWeeks}-week cycle";
            if (d.Start >= d.End) return $"Start must be before end for day {d.DayOfWeek}";
        }
        return null;
    }

    private static readonly string[] Weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>
    /// Why a fixed roster can't be saved, if it can't (owner decision R6): each shift at
    /// least three hours, within the role's maximum, a junior inside 06:00-21:00, and
    /// not on a closed day or past the store's close and its close-down. The rule is the
    /// role's break rule (none: no maximum), the week the store's trading hours, and the
    /// overrides the rostering-rule overrides (closeDownMin; 30 when not set).
    /// </summary>
    public static string? FixedShiftRefusal(
        string role,
        IEnumerable<FixedDay> days,
        BreakRule? rule,
        IReadOnlyList<TradingDayRow> week,
        IReadOnlyDictionary<string, double> overrides)
    {
        var max = rule is not null
            ? Breaks.MaxClockMinutes(rule.BreakType ?? (Roles.IsManager(role) ? BreakTypes.Flat30Unpaid : BreakTypes.Tiered), rule.MaxShiftHours)
            : double.PositiveInfinity;
        var closeDown = overrides.TryGetValue("closeDownMin", out var cd) && double.IsFinite(cd) ? cd : 30;
        foreach (var d in days)
        {
            var start = Minutes(d.Start);
            var end = Minutes(d.End);
            var weekday = d.DayOfWeek is >= 0 and < 7 ? Weekday[d.DayOfWeek] : Js.NumberToString(d.DayOfWeek);
            var label = $"Week {d.WeekIndex + 1} {weekday} {Hm(d.Start)}–{Hm(d.End)}";
            if (end - start < 180) return $"{label} is under the 3-hour minimum.";
            if (end - start > max) return $"{label} is longer than the {Js.ToFixed(max / 60, 2)}-hour maximum for a {role}.";
            if (role == Roles.JSA && (start < 6 * 60 || end > 21 * 60)) return $"{label} is outside a junior's 06:00–21:00.";
            var th = week.FirstOrDefault(w => w.DayOfWeek == d.DayOfWeek);
            if (th is { Closed: true }) return $"{label} falls on a day the store is closed.";
            if (!string.IsNullOrEmpty(th?.CloseTime) && end > Grid.TimeToMin(th.CloseTime) + closeDown)
                return $"{label} runs past the store's close ({First5(th.CloseTime)}) and its {Js.NumberToString(closeDown)}-minute close-down.";
        }
        return null;
    }

    // ─── Weekend-off markers kept in step with day-off requests ──────────────
    // A granted day-off that falls on a Saturday or Sunday must also switch on the
    // matching weekend-off marker (so the Weekends page reflects it, and the roster
    // engine - which reads weekend_markers, not day_off_requests, for weekend
    // availability - actually keeps that person off). Un-granting clears just that
    // day's flag again. The weekend marker is keyed on the Saturday of the weekend,
    // with a sat_off / sun_off flag apiece; the sibling day's flag is preserved.

    /// <summary>The Saturday of a weekend day's weekend; null on a weekday (no marker to touch).</summary>
    public static DateOnly? WeekendStartOf(DateOnly date)
    {
        var dow = Period.DowMon0(date);
        if (dow != 5 && dow != 6) return null;
        return dow == 5 ? date : Period.AddDaysIso(date, -1);
    }

    /// <summary>The marker's flags after a weekend day's request is (un)granted: that day follows the grant.</summary>
    public static (bool SatOff, bool SunOff) MergeWeekendMarker(DateOnly date, bool granted, bool? existingSatOff, bool? existingSunOff)
    {
        var dow = Period.DowMon0(date);
        return (dow == 5 ? granted : existingSatOff ?? false, dow == 6 ? granted : existingSunOff ?? false);
    }

    // ─── Times ───────────────────────────────────────────────────────────────

    /// <summary>Minutes from midnight (the TypeScript timeToMin of an "HH:MM" time).</summary>
    public static double Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    // s.slice(0, 5): "21:00:00" -> "21:00".
    private static string First5(string s) => s.Length > 5 ? s[..5] : s;

    /// <summary>"09:30": a time as the page writes it (the TypeScript slice(0, 5)).</summary>
    public static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
