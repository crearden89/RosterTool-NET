// Port of src/lib/leave/compute.ts: the pure assembly of the Leave Liability
// screens' figures from rows already loaded. The loading itself (the queries,
// the scope, the request cache) is in Roster.Web/Features/LeaveLiability's
// LeaveLiabilityService, which calls these with what it read.

using Roster.Engine.Core;

namespace Roster.Engine.Leave;

/// <summary>The national leave settings and the accrual bands (<c>getLeaveConfig</c>).</summary>
public sealed record LeaveConfig(IReadOnlyList<AccrualBand> Bands, double TargetHoursPerFte, double FteDivisor);

/// <summary>A row of the liability file with its projection to 31 December (<c>TeamMember</c>).</summary>
public sealed record TeamMember : LiabilityRow
{
    /// <summary>Leave notional used to convert hours ↔ days.</summary>
    public double LeaveNotional { get; init; }
    /// <summary>Annual + lieu as at the snapshot, lieu signed.</summary>
    public double CurrentLiabilityHours { get; init; }
    /// <summary>Approved future leave DAYS booked between today and 31 Dec.</summary>
    public double PlannedDays { get; init; }
    /// <summary>Those days priced at the leave notional.</summary>
    public double PlannedHours { get; init; }
    /// <summary>Hours that will accrue between today and 31 Dec inclusive.</summary>
    public double EoyAccrualHours { get; init; }
    /// <summary>current liability − planned + still-to-accrue.</summary>
    public double EoyBalanceHours { get; init; }
    public double EoyBalanceWeeks { get; init; }
    /// <summary>Leave DAYS still to book this year to land at or under the target.</summary>
    public double DaysToTake { get; init; }
    /// <summary>True when no band matched - every projected figure is then unreliable.</summary>
    public bool Unbanded { get; init; }

    /// <summary><c>{ ...row, ...projection }</c>.</summary>
    public static TeamMember From(LiabilityRow r, Projection p) => new()
    {
        UkgEmployeeId = r.UkgEmployeeId,
        EmployeeName = r.EmployeeName,
        Job = r.Job,
        RoleClass = r.RoleClass,
        ContractHours = r.ContractHours,
        NotionalHours = r.NotionalHours,
        AnnualHours = r.AnnualHours,
        SickHours = r.SickHours,
        LieuHours = r.LieuHours,
        BankedHours = r.BankedHours,
        Banked2Hours = r.Banked2Hours,
        LeaveNotional = p.LeaveNotional,
        CurrentLiabilityHours = p.CurrentLiabilityHours,
        PlannedDays = p.PlannedDays,
        PlannedHours = p.PlannedHours,
        EoyAccrualHours = p.EoyAccrualHours,
        EoyBalanceHours = p.EoyBalanceHours,
        EoyBalanceWeeks = p.EoyBalanceWeeks,
        DaysToTake = p.DaysToTake,
        Unbanded = p.Unbanded,
    };
}

/// <summary>One store's Leave Liability figures for one pay period (<c>LeaveStoreView</c>).</summary>
/// <param name="UnbandedCombos">(contract, role) combinations with no accrual band - shown as a warning.</param>
public sealed record LeaveStoreView(
    DateOnly PeriodEnd,
    DateOnly? PreviousPeriodEnd,
    IReadOnlyList<TeamMember> Members,
    StoreLiability Liability,
    StoreLiability? PreviousLiability,
    double? ChangePct,
    IReadOnlyList<TeamMember> TopCurrent,
    IReadOnlyList<TeamMember> TopEoy,
    CalendarGrid Calendar,
    int MaxStaffOnLeave,
    IReadOnlyList<DateOnly> Holidays,
    IReadOnlyList<string> UnbandedCombos,
    double Target);

/// <summary>A store as the Network tab lists it.</summary>
public sealed record LeaveStoreRef(Guid Id, string Code, string Name);

/// <summary>One store's row in the Network league table.</summary>
/// <param name="TotalLiabilityHours">annual + lieu - the whole liability, matching the store view.</param>
public sealed record NetworkRow(
    Guid StoreId,
    string Code,
    string Name,
    int Headcount,
    double TotalLiabilityHours,
    double Fte,
    double? HoursPerFte,
    double? VsTarget);

/// <summary>What <c>computeLeaveNetwork</c> returns.</summary>
/// <param name="Periods">Every pay period held for any store, newest first (the page's period picker).</param>
public sealed record LeaveNetworkView(
    DateOnly? PeriodEnd,
    IReadOnlyList<NetworkRow> Rows,
    double Target,
    IReadOnlyList<DateOnly> Periods);

/// <summary>One point of the liability-per-FTE trend.</summary>
public sealed record TrendPoint(DateOnly PeriodEnd, double HoursPerFte);

public static class LeaveCompute
{
    /// <summary>
    /// The period on screen: the one asked for when it is held, else the newest -
    /// and the period before it, if any. <paramref name="periods"/> is newest first.
    /// </summary>
    public static (DateOnly? Current, DateOnly? Previous) PickPeriods(IReadOnlyList<DateOnly> periods, DateOnly? periodEnd)
    {
        DateOnly? current = periodEnd is { } p && periods.Contains(p) ? p : periods.Count > 0 ? periods[0] : null;
        if (current is not { } c) return (null, null);
        var at = IndexOf(periods, c);
        DateOnly? previous = at + 1 < periods.Count ? periods[at + 1] : null;
        return (current, previous);
    }

    /// <summary>
    /// The store view, from the current period's rows, the previous period's (null
    /// when there is no previous period) and the store's bookings.
    /// </summary>
    public static LeaveStoreView BuildStoreView(
        LeaveConfig config,
        DateOnly today,
        DateOnly current,
        DateOnly? previous,
        IReadOnlyList<LiabilityRow> rows,
        IReadOnlyList<LiabilityRow>? previousRows,
        IReadOnlyList<LeaveRequestRow> requests,
        int maxStaffOnLeave,
        IReadOnlyList<DateOnly> holidays)
    {
        var yearEnd = new DateOnly(today.Year, 12, 31);

        // Casuals accrue nothing and are excluded from every figure - they would
        // otherwise drag the per-FTE ratio without ever contributing leave.
        var live = rows.Where(r => r.RoleClass != RoleClass.CASUAL && r.ContractHours > 0).ToList();
        var byEmp = new JsMap<string, List<LeaveRequestRow>>();
        foreach (var r in requests)
        {
            var arr = byEmp.GetOr(r.UkgEmployeeId, []);
            arr.Add(r);
            byEmp.Set(r.UkgEmployeeId, arr);
        }

        var members = live.Select(r =>
        {
            var days = LeaveCalendar.FutureApprovedDays(byEmp.GetOr(r.UkgEmployeeId, []), today, yearEnd);
            return TeamMember.From(r, LeaveAccrual.ProjectEmployee(r, config.Bands, days, today, config.TargetHoursPerFte));
        }).ToList();

        var combos = new JsSet<string>();
        foreach (var m in members.Where(m => m.Unbanded)) combos.Add($"{Js.NumberToString(m.ContractHours)}h {m.RoleClass}");
        var unbandedCombos = Js.SortDefault(combos.ToList());

        var liability = LeaveMetrics.StoreLiability(rows, config.FteDivisor, config.TargetHoursPerFte);
        var previousLiability = previous != null
            ? LeaveMetrics.StoreLiability(previousRows ?? [], config.FteDivisor, config.TargetHoursPerFte)
            : null;

        var calendar = LeaveCalendar.BuildCalendar(
            live.Select(r => r.UkgEmployeeId).ToList(),
            requests,
            new DateOnly(today.Year, 1, 1),
            yearEnd,
            maxStaffOnLeave);

        return new LeaveStoreView(
            PeriodEnd: current,
            PreviousPeriodEnd: previous,
            Members: members,
            Liability: liability,
            PreviousLiability: previousLiability,
            ChangePct: LeaveMetrics.LiabilityChange(liability.HoursPerFte, previousLiability?.HoursPerFte),
            TopCurrent: LeaveMetrics.TopBy(members, m => m.CurrentLiabilityHours),
            TopEoy: LeaveMetrics.TopBy(members, m => m.EoyBalanceHours),
            Calendar: calendar,
            MaxStaffOnLeave: maxStaffOnLeave,
            Holidays: holidays,
            UnbandedCombos: unbandedCombos,
            Target: config.TargetHoursPerFte);
    }

    /// <summary>
    /// The Network league table: every store (in <paramref name="stores"/>' order, by
    /// code) that has rows in <paramref name="snapshots"/>, highest per FTE first.
    /// </summary>
    public static List<NetworkRow> BuildNetworkRows(
        LeaveConfig config,
        IReadOnlyList<LeaveStoreRef> stores,
        IEnumerable<(Guid StoreId, LiabilityRow Row)> snapshots)
    {
        var byStore = new JsMap<Guid, List<LiabilityRow>>();
        foreach (var (storeId, row) in snapshots)
        {
            var arr = byStore.GetOr(storeId, []);
            arr.Add(row);
            byStore.Set(storeId, arr);
        }

        var rows = stores
            .Where(s => byStore.Has(s.Id))
            .Select(s =>
            {
                var l = LeaveMetrics.StoreLiability(byStore[s.Id], config.FteDivisor, config.TargetHoursPerFte);
                return new NetworkRow(
                    StoreId: s.Id,
                    Code: s.Code,
                    Name: s.Name,
                    Headcount: l.Headcount,
                    TotalLiabilityHours: l.TotalLiabilityHours,
                    Fte: l.Fte,
                    HoursPerFte: l.HoursPerFte,
                    VsTarget: l.VsTarget);
            });
        return Js.Sorted(rows, (a, b) => (b.HoursPerFte ?? -1) - (a.HoursPerFte ?? -1));
    }

    /// <summary>
    /// Liability per FTE by period, oldest first - the trend chart's series. Periods
    /// whose figure is zero (or has no FTE) are left out.
    /// </summary>
    public static List<TrendPoint> BuildTrend(LeaveConfig config, IEnumerable<(DateOnly PeriodEnd, LiabilityRow Row)> snapshots)
    {
        var byPeriod = new JsMap<DateOnly, List<LiabilityRow>>();
        foreach (var (periodEnd, row) in snapshots)
        {
            var arr = byPeriod.GetOr(periodEnd, []);
            arr.Add(row);
            byPeriod.Set(periodEnd, arr);
        }
        var points = byPeriod
            .Select(kv => new TrendPoint(
                kv.Key,
                LeaveMetrics.StoreLiability(kv.Value, config.FteDivisor, config.TargetHoursPerFte).HoursPerFte ?? 0))
            .Where(p => p.HoursPerFte > 0);
        // ISO dates: localeCompare on them is the calendar order.
        return Js.Sorted(points, (a, b) => a.PeriodEnd.CompareTo(b.PeriodEnd));
    }

    private static int IndexOf(IReadOnlyList<DateOnly> xs, DateOnly x)
    {
        for (var i = 0; i < xs.Count; i++)
            if (xs[i] == x) return i;
        return -1;
    }
}
