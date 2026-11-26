// Port of src/app/(app)/admin/leave-liability/leaveUi.tsx (the helpers; Person,
// TabLink and NoLeaveData are LeavePerson.razor, LeaveTabLink.razor and NoLeaveData.razor).
// ─── Small pieces shared by the Leave Liability tabs ────────────────────────

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Web.Features.LeaveLiability;

/// <summary>The page's five views.</summary>
public static class LeaveTab
{
    public const string Overview = "overview", Team = "team", Calendar = "calendar", Network = "network", Settings = "settings";
}

public static partial class LeaveUi
{
    public static string H1(double? n) => n is { } v ? Js.ToFixed(v, 1) : "-";
    public static string H2(double? n) => n is { } v ? Js.ToFixed(v, 2) : "-";
    public static string Pct(double? n) => n is { } v ? $"{(v >= 0 ? "+" : "−")}{Js.ToFixed(Math.Abs(v * 100), 1)}%" : "-";

    private static readonly string[] MonthsLong =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static string MonthName(int m) => MonthsLong[m];

    /// <summary>"8 November 2026" from 2026-11-08.</summary>
    public static string LongDate(DateOnly d) => $"{d.Day} {MonthsLong[d.Month - 1]} {d.Year}";

    /// <summary>UKG job titles arrive shouted ("ST MANAGER"); soften them for the table.</summary>
    public static string PositionLabel(TeamMember m) =>
        m.Job is { Length: > 0 } job ? WordStart().Replace(job.ToLowerInvariant(), c => c.Value.ToUpperInvariant()) : m.RoleClass;

    // /\b[a-z]/g: ECMAScript's \b, whose word characters are ASCII only.
    [GeneratedRegex(@"\b[a-z]", RegexOptions.ECMAScript)]
    private static partial Regex WordStart();

    /// <summary>Red over the target, green at or under it - the page's one colour rule.</summary>
    public static string OverColour(bool over) => over ? "text-[var(--ui-bad)]" : "text-[var(--ui-good)]";

    /// <summary>The bar colours either side of a target: red over it, a soft green under.</summary>
    public const string TargetAbove = "var(--ui-bad)";
    public const string TargetBelow = "#7cc49b";

    /// <summary>A number as JavaScript writes it in a template ("76", "37.5").</summary>
    public static string N(double n) => Js.NumberToString(n);
}
