// Port of src/lib/subtasks/loadBoard.ts.
// ─── Sub-tasks: what the load board says arrives on an average day ───────────
// Load Running's split between its sub-tasks follows the load board (master
// plan, decision 4.6): the store's average pallets of each load type on the
// same weekday over the eight weeks before the plan's fortnight, times that
// type's minutes per pallet. A weekday counts only the dates the load board
// has pallet counts for - a date nobody entered is unknown, not empty. "D's"
// are kept apart as Ambient's last part (see Catalogue). Pure.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.SubTasks;

/// <summary>A load_types row, numbers already read (null where the column is).</summary>
public sealed record LoadTypeRow
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("minutes_per_pallet")] public double? MinutesPerPallet { get; init; }
    [JsonPropertyName("priority")] public double? Priority { get; init; }
    /// <summary>The sub-task saved on the load type (Load types page); empty = by name.</summary>
    [JsonPropertyName("sub_task")] public string? SubTask { get; init; }
}

/// <summary>A daily_loads row: one load type's pallets on one date.</summary>
public sealed record DailyLoadRow
{
    [JsonPropertyName("date")] public DateOnly Date { get; init; }
    [JsonPropertyName("load_type_id")] public string LoadTypeId { get; init; } = "";
    [JsonPropertyName("qty")] public double? Qty { get; init; }
}

/// <summary>The load board's average day for each weekday (index 0 = Monday).</summary>
public sealed record LoadBoardAverages(List<LoadSplit> ByDow);

public static class LoadBoard
{
    public const int LoadHistoryWeeks = 8;

    /// <summary>The first date of the history window: eight weeks before the fortnight starts.</summary>
    public static DateOnly LoadHistoryFrom(DateOnly periodStart, int weeks = LoadHistoryWeeks) => periodStart.AddDays(-weeks * 7);

    /// <summary>
    /// Average minutes of each Load Running sub-task arriving on each weekday
    /// (index 0 = Monday), from the load board rows dated before <paramref name="periodStart"/>
    /// and no more than eight weeks before it, and each sub-task's priority (the
    /// best of its load types'). A weekday with no history has empty minutes.
    /// </summary>
    public static LoadBoardAverages LoadBoardAverages(IReadOnlyList<LoadTypeRow> types, IReadOnlyList<DailyLoadRow> rows, DateOnly periodStart)
    {
        var from = LoadHistoryFrom(periodStart);
        var typeById = new JsMap<string, LoadTypeRow>(types.Select(t => (t.Id, t)));
        var priority = new JsMap<string, double>();
        foreach (var t in types)
        {
            var k = Catalogue.LoadPartFor(t.Name, t.SubTask);
            if (k == Catalogue.AmbientLastPart) continue; // worked last, whatever its priority
            var p = t.Priority ?? 100;
            priority.Set(k, Js.Min(priority.GetOr(k, double.PositiveInfinity), double.IsFinite(p) ? p : 100));
        }
        var minutes = Enumerable.Range(0, 7).Select(_ => new JsMap<string, double>()).ToList();
        var dates = Enumerable.Range(0, 7).Select(_ => new JsSet<DateOnly>()).ToList();
        foreach (var r in rows)
        {
            if (r.Date < from || r.Date >= periodStart) continue;
            if (!typeById.TryGet(r.LoadTypeId, out var t)) continue;
            var dow = Period.DowMon0(r.Date);
            dates[dow].Add(r.Date);
            var qty = r.Qty ?? 0;
            var mpp = t.MinutesPerPallet ?? 0;
            if (!(qty > 0) || !(mpp > 0)) continue;
            var k = Catalogue.LoadPartFor(t.Name, t.SubTask);
            minutes[dow].Set(k, minutes[dow].GetOr(k, 0) + qty * mpp);
        }
        return new LoadBoardAverages(minutes.Select((m, dow) =>
        {
            var n = dates[dow].Size;
            var avg = new JsMap<string, double>();
            if (n > 0) foreach (var (k, v) in m) avg.Set(k, v / n);
            return new LoadSplit { Minutes = avg, Priority = priority };
        }).ToList());
    }
}
