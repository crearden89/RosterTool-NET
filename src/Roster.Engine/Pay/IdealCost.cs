// Port of the pure part of src/lib/payroll/idealCost.ts (the caller supplies
// the day's Stage 1 demand, the rates and the day type).

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public static class IdealCost
{
    // JSA is deliberately EXCLUDED from the pricing pool: the floor assumes every
    // demand block is worked by an adult at the Store Assistant (or cheapest
    // eligible adult) rate. When the store then covers hours with juniors, the
    // saving lands as a bonus BELOW the ideal, instead of the ideal assuming a
    // junior-heavy roster nobody is required to build.
    private static readonly string[] AdultRoles = [Roles.SM, Roles.ASM, Roles.DSM, Roles.RSM, Roles.SA];
    // The compulsory opener/closer pair may only be held by a core manager.
    private static readonly string[] CoreMgrRoles = [Roles.SM, Roles.ASM, Roles.DSM];

    /// <summary>
    /// The theoretical labour-cost floor for a day (spec §6.5): every Stage 1 demand
    /// block priced at the CHEAPEST adult role legally eligible for that task at
    /// that time, PLUS the two compulsory manager shifts - one manager on the floor
    /// from staff-open to staff-close, two during the ~1h handover overlap. Where
    /// demand exists the manager does benchmark work (so only their rate PREMIUM
    /// over the displaced cheapest role is added); where none does, their presence
    /// is still a real, unavoidable cost and is added in full. Coverage/contract
    /// reality, breaks and minimum lengths are ignored. The Δ against the real
    /// Stage 2 roster shows what reshaping around actual staff costs.
    /// </summary>
    public static double ComputeIdealDayCost(
        bool hasData,
        IEnumerable<(string Group, double Time, double Headcount)> blocks,
        double staffOpen,
        double staffClose,
        IEnumerable<PayRateRow> payRates,
        string dayType)
    {
        if (!hasData) return 0;
        var rateMap = new JsMap<string, double>();
        foreach (var r in payRates) rateMap.Set($"{r.DayType}:{Js.NumberToString(r.Hour)}:{r.Role}", r.Rate);

        // Role-generic base rate: the floor prices each role at its DEFAULT position
        // (year 1, standard contract size), and RSM at the Store Assistant rate - its
        // sole-manager allowance is a coverage cost, not part of the theoretical floor.
        double RateFor(double hour, string role)
        {
            var key = Positions.DefaultPositionFor(role == Roles.RSM ? Roles.SA : role);
            return rateMap.GetOr($"{dayType}:{Js.NumberToString(hour)}:{key}", double.PositiveInfinity);
        }
        double MgrRateFor(double hour) => Js.Min(CoreMgrRoles.Select(role => RateFor(hour, role)));

        // Demand priced at the cheapest eligible ADULT rate, tracking per-time
        // totals so the manager pass below can substitute into them.
        double ideal = 0;
        var demandAt = new JsMap<double, (double Headcount, double Cheapest)>();
        foreach (var b in blocks)
        {
            var hour = Math.Floor(b.Time / 60);
            // Non-Op is generic (any adult role); otherwise only roles eligible for
            // the task.
            var eligible = AdultRoles.Where(role => b.Group == TaskGroups.NonOp || TaskGroups.EligibleFor(role, b.Group));
            var cheapest = Js.Min(eligible.Select(role => RateFor(hour, role)));
            if (!double.IsFinite(cheapest)) continue;
            ideal += b.Headcount * cheapest * (Grid.BlockMin / 60);
            var had = demandAt.TryGet(b.Time, out var cur);
            demandAt.Set(b.Time, ((had ? cur.Headcount : 0) + b.Headcount, Math.Min(had ? cur.Cheapest : double.PositiveInfinity, cheapest)));
        }

        // The two compulsory manager shifts: opener from staff-open, closer to
        // staff-close, overlapping for the ~1h handover at the span midpoint.
        if (staffClose <= staffOpen) return ideal;
        var mid = Js.Round((staffOpen + staffClose) / 2 / Grid.BlockMin) * Grid.BlockMin;
        var overlapStart = mid - 30;
        var overlapEnd = mid + 30;
        for (var t = staffOpen; t < staffClose; t += Grid.BlockMin)
        {
            double presence = t >= overlapStart && t < overlapEnd ? 2 : 1;
            var mgrRate = MgrRateFor(Math.Floor(t / 60));
            if (!double.IsFinite(mgrRate)) continue;
            var hasD = demandAt.TryGet(t, out var d);
            // Managers absorb demand first (their hours come out of the benchmark,
            // not on top of it) - the ideal only pays their premium over the cheapest
            // role they displace. Presence beyond demand is pure compulsory cost.
            var substituted = Math.Min(presence, hasD ? d.Headcount : 0);
            var additive = presence - substituted;
            ideal += (substituted * Math.Max(0, mgrRate - (hasD ? d.Cheapest : 0)) + additive * mgrRate) * (Grid.BlockMin / 60);
        }
        return ideal;
    }
}
