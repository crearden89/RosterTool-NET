// Port of src/app/(app)/plan/sales/adjustment.ts.
// What a Sales adjustment (a sales_overrides row) does to its day, read the way
// the planner reads it (lib/till/compute.ts): a closed day is zeroed, a
// percentage other than 100 or a new value rescales the day's sales, and
// anything else leaves the forecast as it is. Pure, for the page and its
// service's actions alike.

using Roster.Engine.Core;

namespace Roster.Web.Features.Sales;

/// <summary>A sales_overrides row's adjustment fields (<c>AdjustmentRow</c>).</summary>
public interface IAdjustment
{
    bool Closed { get; }
    /// <summary>"pct", "value" or null.</summary>
    string? Method { get; }
    double? Pct { get; }
    double? Value { get; }
    string? Reason { get; }
    string? Notes { get; }
}

/// <summary>An adjustment on its own, without its day.</summary>
public sealed record AdjustmentRow : IAdjustment
{
    public bool Closed { get; init; }
    public string? Method { get; init; }
    public double? Pct { get; init; }
    public double? Value { get; init; }
    public string? Reason { get; init; }
    public string? Notes { get; init; }
}

public static class Adjustment
{
    /// <summary>Whether the adjustment changes the sales figure the day is planned on.</summary>
    public static bool ChangesSales(IAdjustment o)
    {
        if (o.Closed) return true;
        if (o.Method == "pct") return o.Pct != null && o.Pct != 100;
        if (o.Method == "value") return o.Value != null;
        return false;
    }

    /// <summary>
    /// An adjustment that changes nothing and gives no reason - what setting a day
    /// back to 100% used to leave behind (F10 in the run-through report). It is no
    /// adjustment at all: no pill on the chart, nothing in the table.
    /// </summary>
    public static bool IsEmptyAdjustment(IAdjustment o) => !ChangesSales(o) && Js.Trim(NoteOf(o)) == "";

    /// <summary><c>o.notes || o.reason || ""</c>: the note, else the legacy reason (an empty string counts as none).</summary>
    public static string NoteOf(IAdjustment o) =>
        !string.IsNullOrEmpty(o.Notes) ? o.Notes : !string.IsNullOrEmpty(o.Reason) ? o.Reason : "";
}
