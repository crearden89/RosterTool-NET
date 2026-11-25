// Port of the pure part of src/lib/subtasks/stored.ts. The reads - the
// published shifts (generated_shifts) and their tiles (shift_task_blocks), the
// held plan's Stage 1 record per date, and each date's label context - are the
// data layer's.
// ─── Sub-tasks on a PUBLISHED roster (the printed roster) ────────────────────
// A published roster's tiles are rows of shift_task_blocks; since 0049 each
// carries its sub-task. Rows published before that have none and are labelled
// here when read, by the normal rules (Reset from the held plan's Stage 1
// record). Nothing is written back.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.SubTasks;

/// <summary>A published shift (generated_shifts: id, employee_id, date).</summary>
public sealed record StoredShift(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("employee_id")] string EmployeeId,
    [property: JsonPropertyName("date")] DateOnly Date);

/// <summary>A published tile (shift_task_blocks).</summary>
public sealed record StoredTile(
    [property: JsonPropertyName("shift_id")] string ShiftId,
    [property: JsonPropertyName("block_time")] string BlockTime,
    [property: JsonPropertyName("labour_group")] string LabourGroup,
    [property: JsonPropertyName("sub_task")] string? SubTask);

public static class Stored
{
    /// <summary>
    /// One date's published shifts as tiles, in time order, each with the
    /// sub-task it was published with when it is one of its own group's.
    /// </summary>
    public static List<(string EmployeeId, List<DisplayBlock> Blocks)> DayTiles(IReadOnlyList<StoredShift> dayShifts, IReadOnlyList<StoredTile> tiles)
    {
        var tilesOf = new JsMap<string, List<DisplayBlock>>();
        foreach (var t in tiles)
        {
            var b = Catalogue.IsSubtaskOf(t.LabourGroup, t.SubTask)
                ? new DisplayBlock(Grid.TimeToMin(t.BlockTime), t.LabourGroup, t.SubTask)
                : new DisplayBlock(Grid.TimeToMin(t.BlockTime), t.LabourGroup);
            tilesOf.Set(t.ShiftId, [.. tilesOf.GetOr(t.ShiftId, []), b]);
        }
        return dayShifts.Select(s => (s.EmployeeId, Js.Sort(tilesOf.GetOr(s.Id, []), (a, b) => a.Time - b.Time))).ToList();
    }

    /// <summary>
    /// Each published shift's sub-task codes in the order they are worked, keyed
    /// <c>employeeId:date</c> - what the printed roster shows under the shift
    /// times. <paramref name="contextFor"/> gives a date's label context (Day.ContextFor, with
    /// the held plan's Stage 1 record for the date, or null).
    /// </summary>
    public static JsMap<string, List<string>> PublishedSubtaskRuns(
        IReadOnlyList<StoredShift> shifts,
        IReadOnlyList<StoredTile> tiles,
        Func<DateOnly, LabelContext> contextFor)
    {
        var out_ = new JsMap<string, List<string>>();
        var byDate = new JsMap<DateOnly, List<StoredShift>>();
        foreach (var s in shifts) byDate.Set(s.Date, [.. byDate.GetOr(s.Date, []), s]);
        foreach (var (date, dayShifts) in byDate)
        {
            var ids = new JsSet<string>(dayShifts.Select(s => s.Id));
            var day = DayTiles(dayShifts, tiles.Where(t => ids.Has(t.ShiftId)).ToList());
            var labelled = Day.WithDayLabels(day.Select(d => (IReadOnlyList<DisplayBlock>)d.Blocks).ToList(), contextFor(date));
            for (var i = 0; i < day.Count; i++) out_.Set($"{day[i].EmployeeId}:{date.ToIso()}", Runs.SubtaskRun(labelled[i]));
        }
        return out_;
    }
}
