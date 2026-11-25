// Port of the value and section helpers in src/app/(app)/admin/rules/rostering/RulesClient.tsx.

using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Web.Features.Rules.Rostering;

/// <summary>How the Rostering Rules page writes, reads and groups each built-in rule's value.</summary>
public static partial class RuleValueText
{
    /// <summary>"HH:MM" for a minutes value (the page's own: hours floored, minutes rounded).</summary>
    public static string MinToTime(double m) =>
        $"{Js.PadStart(Js.NumberToString(Math.Floor(m / 60)), 2, '0')}:{Js.PadStart(Js.NumberToString(Js.Round(m) % 60), 2, '0')}";

    /// <summary>"HH:MM" to minutes; a blank or unreadable part counts as 0.</summary>
    public static double TimeToMinutes(string t)
    {
        var parts = t.Split(':');
        var h = Js.Number(parts[0]);
        var m = parts.Length > 1 ? Js.Number(parts[1]) : double.NaN;
        return (Js.Truthy(h) ? h : 0) * 60 + (Js.Truthy(m) ? m : 0);
    }

    public static string UnitLabel(RuleMeta meta) => meta.Unit switch
    {
        "minutes" => "min",
        "hours" => "h",
        "percent" => "%",
        _ => meta.Unit,
    };

    /// <summary>A value as the Default column and the range message write it: "60 min", "06:00".</summary>
    public static string FormatValue(RuleMeta meta, double v) =>
        meta.Unit == "time" ? MinToTime(v) : $"{Js.NumberToString(v)} {UnitLabel(meta)}";

    /// <summary>Why a value outside the rule's range was refused: "Allowed range: 60 min – 480 min."</summary>
    public static string RangeMessage(RuleMeta meta) => $"Allowed range: {FormatValue(meta, meta.Min)} – {FormatValue(meta, meta.Max)}.";

    /// <summary>A value as its box shows it.</summary>
    public static string TextOf(RuleMeta meta, double v) => meta.Unit == "time" ? MinToTime(v) : Js.NumberToString(v);

    /// <summary>A box's text as a value (a blank number box reads 0, as Number("") does).</summary>
    public static double ValueOf(RuleMeta meta, string text) => meta.Unit == "time" ? TimeToMinutes(text) : Js.Number(text);

    /// <summary>True when the box holds a different value from the one it was edited from.</summary>
    public static bool IsDirty(RuleMeta meta, string text, double @base) => Math.Abs(ValueOf(meta, text) - @base) > 1e-9;

    /// <summary>The number box's step: 15 for minutes, 0.5 for hours, else 1.</summary>
    public static double Step(RuleMeta meta) => meta.Unit switch
    {
        "minutes" => 15,
        "hours" => 0.5,
        _ => 1,
    };

    /// <summary>"Juniors (JSA)" → "juniors-jsa": a section's anchor id.</summary>
    public static string Slug(string s) => EdgeDashes().Replace(NotAlphanumeric().Replace(s.ToLowerInvariant(), "-"), "");

    /// <summary>The rules' categories, in the order they first appear in the rule list.</summary>
    public static readonly IReadOnlyList<string> Categories = RuleMetas.All.Select(m => m.Category).Distinct().ToList();

    /// <summary>One line on what each group of rules is for.</summary>
    public static readonly IReadOnlyDictionary<string, string> Purpose = new Dictionary<string, string>
    {
        ["Shift limits"] = "How short a shift may be, and how far shifts and the load may stretch for breaks.",
        ["Weekly limits"] = "Rest between days, runs of worked days and the days off in each week.",
        ["Floor safety"] = "Who must be on the floor at any time.",
        ["Juniors (JSA)"] = "When a junior may work, for how long, and their hours in a week.",
        ["Manager pair"] = "The managers' shortest shift and their handover.",
        ["Plan settings"] = "Times and counts the planner reads when it lays out the day.",
        ["Sub-task shares"] = "How a group's tiles are labelled by sub-task. Labels only: the hours planned never change.",
    };

    /// <summary>The page's sections: each category, then Pay rules and Custom rules.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Sections =
    [
        .. Categories.Select(c => (Slug(c), c)),
        ("pay-rules", "Pay rules"),
        ("custom-rules", "Custom rules"),
    ];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex("^-|-$")]
    private static partial Regex EdgeDashes();
}
