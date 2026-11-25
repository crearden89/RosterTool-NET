// Port of src/app/floor/[token]/floorLoad.ts.
// ─── Floor Display: the Load Planner strip ───────────────────────────────────
// The day's truck as the Load page plans it: the same crew (everyone with Load
// Running tiles on the approved roster, starting at their first one), the same
// pallet minutes for each person's tenure band, the same rotation history and
// the SM's pins, through the same allocator. Built the way the Load page
// builds its inputs (LoadPlanner/LoadPlannerService.cs), so the two screens
// agree on the total, the team finish and how many are on the load. Pure: the
// service reads the rows. It never writes - the Load page records the day's
// rotation.

using Roster.Engine.Core;
using Roster.Engine.Load;

namespace Roster.Web.Features.Floor;

/// <summary>A load_types row as the strip reads it.</summary>
public sealed record LoadTypeRow(
    string Id,
    string Name,
    double MinutesPerPallet,
    double? MinutesPerPalletLt90,
    double? MinutesPerPalletLt60,
    double? MinutesPerPalletLt30,
    string Color,
    double? Priority);

/// <summary>A shift on the approved roster: its id, who (null for an open shift) and their name.</summary>
public sealed record LoadShiftRow(string Id, string? EmployeeId, string EmployeeName);

/// <summary>One line of the day's load entry.</summary>
public sealed record DailyLoadRow(string LoadTypeId, double Qty);

/// <summary>A load_assignments row: who owned which load type on a day (manual = the SM's pin).</summary>
public sealed record LoadHistoryRow(DateOnly Date, string EmployeeId, string LoadType, bool Manual);

public sealed record LoadInputs(
    DateOnly Day,
    // The approved roster's shifts for the day; none when it is not approved.
    IReadOnlyList<LoadShiftRow> Shifts,
    // Each shift's first Load Running quarter-hour, for shifts that have one.
    IReadOnlyDictionary<string, double> FirstLoadRunning,
    IReadOnlyDictionary<string, DateOnly?> StartDates, // employee id -> start date
    IReadOnlyList<LoadTypeRow> Types, // in the Load page's order (sort_order)
    IReadOnlyList<DailyLoadRow> Loads, // the day's entry
    // load_assignments for the fortnight up to and including the day.
    IReadOnlyList<LoadHistoryRow> History);

/// <summary>One load type on the strip: its pallets, in the Load page's order.</summary>
public sealed record LoadStripItem(string Type, string Color, double Qty);

public sealed record LoadSummary(
    bool Entered, // a load has been entered for the day
    double Total,
    IReadOnlyList<LoadStripItem> Items,
    int Crew, // people with Load Running tiles
    int OnLoad, // people the plan gives pallets or the warm-up to
    double? TeamFinish)
{
    /// <summary>A day with nothing entered and nobody on the load.</summary>
    public static readonly LoadSummary Nothing = new(false, 0, [], 0, 0, null);
}

public static class FloorLoad
{
    public static LoadSummary SummariseLoad(LoadInputs i)
    {
        var crew = i.Shifts
            .Where(s => i.FirstLoadRunning.ContainsKey(s.Id) && !string.IsNullOrEmpty(s.EmployeeId))
            .Select(s => new LoadCrewMember(
                s.EmployeeId!,
                s.EmployeeName,
                i.FirstLoadRunning[s.Id],
                Probation.BandForStartDate(i.StartDates.GetValueOrDefault(s.EmployeeId!), i.Day)))
            .ToList();

        var qtyByType = new Dictionary<string, double>();
        foreach (var l in i.Loads) qtyByType[l.LoadTypeId] = l.Qty;
        var items = i.Types
            .Select(t => new LoadItem
            {
                Type = t.Name,
                Color = t.Color,
                Minutes = new PalletMinutes(t.MinutesPerPallet, t.MinutesPerPalletLt90, t.MinutesPerPalletLt60, t.MinutesPerPalletLt30),
                Qty = qtyByType.GetValueOrDefault(t.Id, 0),
                Priority = t.Priority ?? 100,
            })
            .Where(t => t.Qty > 0)
            .ToList();

        // Today's manual rows are the SM's pins; earlier rows are the rotation.
        var pinned = new JsMap<string, string>();
        var typeCounts = new JsMap<string, double>();
        foreach (var h in i.History)
        {
            if (h.Date == i.Day)
            {
                if (h.Manual) pinned.Set(h.EmployeeId, h.LoadType);
                continue;
            }
            var k = $"{h.EmployeeId}:{h.LoadType}";
            typeCounts.Set(k, typeCounts.GetOr(k, 0) + 1);
        }

        var entered = i.Loads.Count > 0;
        var plan = entered && crew.Count > 0
            ? LoadAllocator.AllocateLoad(crew, items, new AllocateOptions { Pinned = pinned, TypeCounts = typeCounts })
            : null;
        var total = plan is not null ? plan.TotalPallets : items.Sum(t => t.Qty);
        return new LoadSummary(
            Entered: entered,
            Total: total,
            Items: items.Select(t => new LoadStripItem(t.Type, t.Color, t.Qty)).ToList(),
            Crew: crew.Count,
            OnLoad: plan is not null ? plan.Crew.Count(c => c.Segments.Count > 0) : 0,
            TeamFinish: plan is not null && total > 0 ? plan.TeamFinish : null);
    }
}
