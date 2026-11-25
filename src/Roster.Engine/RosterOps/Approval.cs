// Port of the pure parts of src/lib/roster/approval.ts. The writes (the held
// snapshot read, day_plans, approve_roster_day, generated_shifts and the audit)
// are the data layer's; these say what to write.
//
// Publishing and withdrawing a fortnight's roster - the work behind the
// Roster page's Approve/Withdraw and the EMSO's decision on the review page.

using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.SubTasks;

namespace Roster.Engine.RosterOps;

/// <summary>One tile of a published shift (shift_task_blocks).</summary>
public sealed record ApprovedBlock(
    [property: JsonPropertyName("block_time")] string BlockTime,
    [property: JsonPropertyName("labour_group")] string LabourGroup,
    [property: JsonPropertyName("sub_task")] string? SubTask);

/// <summary>One published shift, as approve_roster_day takes it (p_shifts).</summary>
public sealed record ApprovedShift(
    [property: JsonPropertyName("employee_id")] string EmployeeId,
    [property: JsonPropertyName("employee_name")] string EmployeeName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("start_time")] string StartTime,
    [property: JsonPropertyName("end_time")] string EndTime,
    [property: JsonPropertyName("break_minutes")] double BreakMinutes,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("blocks")] List<ApprovedBlock> Blocks);

/// <summary>What publishing does with one date of the fortnight.</summary>
public enum PublishStep
{
    /// <summary>No computable roster (no data): skipped, not failed.</summary>
    Skip,
    /// <summary>A closed day: signed off with the rest (roster_status approved), zero shifts.</summary>
    ApproveClosed,
    /// <summary>A rostered day: its shifts and tiles persisted and the day approved (approve_roster_day).</summary>
    Approve,
}

public static class Approval
{
    /// <summary>
    /// Soft block (spec §6.4): a day with an open shift or a cover gap - an
    /// ERROR, graded as the Review grades them (DayStatus) - may be published,
    /// with a note on the attempts to cover it. Warnings and notes alone never
    /// ask for one. Null when publishing may go ahead.
    /// </summary>
    public static string? CoverNoteError(Func<DateOnly, RosterDay?> dayOf, IReadOnlyList<DateOnly> dates, string? comment)
    {
        var needNote = DayStatus.DaysNeedingCoverNote(dayOf, dates);
        if (needNote.Count == 0 || !string.IsNullOrEmpty(comment?.Trim())) return null;
        return $"{DayStatus.CoverNoteReason(needNote)}. Add a note on how you tried to cover them before publishing.";
    }

    /// <inheritdoc cref="CoverNoteError(Func{DateOnly, RosterDay?}, IReadOnlyList{DateOnly}, string?)"/>
    public static string? CoverNoteError(Func<string, RosterDay?> dayOf, IReadOnlyList<string> dates, string? comment)
    {
        var needNote = DayStatus.DaysNeedingCoverNote(dayOf, dates);
        if (needNote.Count == 0 || !string.IsNullOrEmpty(comment?.Trim())) return null;
        return $"{DayStatus.CoverNoteReason(needNote)}. Add a note on how you tried to cover them before publishing.";
    }

    /// <summary>
    /// A closed day is signed off with the rest of the fortnight - zero shifts
    /// is its correct roster, and leaving it un-approved would hold the whole
    /// period's approved state (and the Print UKG button) hostage. A day with no
    /// computable roster (e.g. store closed) is skipped, not failed.
    /// </summary>
    public static PublishStep StepFor(RosterDay? roster)
    {
        if (roster is not null && roster.Closed) return PublishStep.ApproveClosed;
        if (roster is null || !roster.HasData) return PublishStep.Skip;
        return PublishStep.Approve;
    }

    /// <summary>
    /// The day's shifts as approve_roster_day takes them: the held roster's named
    /// shifts with their task tiles, Checkout resolved (Server.ResolvePersistableBlocks)
    /// so the Floor Display matches the Roster view exactly. Open shifts have no
    /// employee and are never persisted. BREAK tiles are persisted alongside the
    /// task tiles - they used to be stripped here, which left the Floor Display
    /// with no record of when anyone actually went on break, so the real break
    /// showed as an unexplained GAP and a second break appeared where the formula
    /// guessed. Only "flex" (genuinely unpainted time) is dropped. A tile's
    /// sub-task is kept only when it belongs to the tile's own group.
    /// </summary>
    public static List<ApprovedShift> ShiftPayload(IReadOnlyList<Shift> shifts)
    {
        var resolved = Server.ResolvePersistableBlocks(shifts);
        var out_ = new List<ApprovedShift>();
        for (var si = 0; si < shifts.Count; si++)
        {
            var s = shifts[si];
            if (s.Unfilled) continue;
            var blocks = s.Blocks
                .Select((b, bi) => new ApprovedBlock(
                    Grid.MinToSqlTime(b.Time),
                    resolved[si][bi],
                    Catalogue.IsSubtaskOf(resolved[si][bi], b.Sub) ? b.Sub : null))
                .Where(b => b.LabourGroup != TaskGroups.Flex)
                .ToList();
            out_.Add(new ApprovedShift(s.EmployeeId, s.Name, s.Role, Grid.MinToSqlTime(s.Start), Grid.MinToSqlTime(s.End), s.BreakMinutes, s.Locked, blocks));
        }
        return out_;
    }

    /// <summary>The audit's coverage comment: the note as typed, trimmed; null when empty.</summary>
    public static string? CoverageComment(string? comment)
    {
        var t = comment?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public const string NothingToPublish = "No roster could be generated for any day in this fortnight";
    public const string GenerateFirst = "Generate the roster first.";
}
