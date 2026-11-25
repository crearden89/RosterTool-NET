// Port of src/lib/subtasks/catalogue.ts.
// ─── Sub-tasks: the catalogue ────────────────────────────────────────────────
// Four labour groups are opened up into the jobs a store actually does inside
// them (the owner's request, 14/11/2026; master plan section 8). A sub-task is
// a LABEL on a painted tile, never a demand of its own: the engine plans the
// group's benchmark hours exactly as before, then one pass after painting
// gives every tile of these groups exactly one sub-task, so the sub-tasks of
// a group always add up to the group. Pure.

using System.Text.RegularExpressions;

namespace Roster.Engine.SubTasks;

/// <summary>A sub-task: its key, group, two-letter tile code, label and the Rostering rule holding its share.</summary>
public sealed record Subtask(string Key, string Group, string Code, string Label, string? ShareRule = null);

public static class SubtaskKeys
{
    public const string ReRuns = "re_runs";
    public const string Decarding = "decarding";
    public const string Produce = "produce";
    public const string LongLife = "long_life";
    public const string Freezer = "freezer";
    public const string Ambient = "ambient";
    public const string Bread = "bread";
    public const string ShortLife = "short_life";
    public const string FreezeToThaw = "freeze_to_thaw";
    public const string FrontEnd = "front_end";
    public const string ShopFloor = "shop_floor";
    public const string Boh = "boh";
    public const string Building = "building";
    public const string Tidy = "tidy";
    public const string Running = "running";
}

public static class Catalogue
{
    public static readonly IReadOnlyList<string> SubtaskGroups = ["load_running", "reset", "specials", "cleaning"];

    /// <summary>SUBTASKS, by group, in the TypeScript's key order (reset, load_running, cleaning, specials).</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<Subtask> Subtasks)> Subtasks =
    [
        ("reset", [
            new(SubtaskKeys.ReRuns, "reset", "RE", "Re-runs"),
            new(SubtaskKeys.Decarding, "reset", "DC", "Decarding"),
        ]),
        ("load_running", [
            new(SubtaskKeys.Produce, "load_running", "PR", "Produce", "subLoadProducePct"),
            new(SubtaskKeys.LongLife, "load_running", "LL", "Long Life", "subLoadLongLifePct"),
            new(SubtaskKeys.Freezer, "load_running", "FZ", "Freezer", "subLoadFreezerPct"),
            new(SubtaskKeys.Ambient, "load_running", "AM", "Ambient", "subLoadAmbientPct"),
            new(SubtaskKeys.Bread, "load_running", "BD", "Bread", "subLoadBreadPct"),
            new(SubtaskKeys.ShortLife, "load_running", "SL", "Short Life", "subLoadShortLifePct"),
            new(SubtaskKeys.FreezeToThaw, "load_running", "FT", "Freeze to Thaw", "subLoadFreezeToThawPct"),
        ]),
        ("cleaning", [
            new(SubtaskKeys.FrontEnd, "cleaning", "FE", "Front end", "subCleaningFrontEndPct"),
            new(SubtaskKeys.ShopFloor, "cleaning", "SF", "Shop floor", "subCleaningShopFloorPct"),
            new(SubtaskKeys.Boh, "cleaning", "BH", "BOH", "subCleaningBohPct"),
        ]),
        ("specials", [
            new(SubtaskKeys.Building, "specials", "BU", "Building", "subSpecialsBuildingPct"),
            new(SubtaskKeys.Tidy, "specials", "TD", "Tidy", "subSpecialsTidyPct"),
            new(SubtaskKeys.Running, "specials", "RN", "Running", "subSpecialsRunningPct"),
        ]),
    ];

    public static IReadOnlyList<Subtask> SubtasksOf(string group)
    {
        foreach (var (g, subs) in Subtasks)
            if (g == group) return subs;
        throw new ArgumentException($"not a sub-task group: {group}", nameof(group));
    }

    public static readonly IReadOnlyDictionary<string, Subtask> SubtaskByKey =
        Subtasks.SelectMany(g => g.Subtasks).ToDictionary(s => s.Key);

    public static bool IsSubtaskGroup(string group) => group is "reset" or "load_running" or "cleaning" or "specials";

    /// <summary>True when <paramref name="sub"/> is one of <paramref name="group"/>'s sub-tasks.</summary>
    public static bool IsSubtaskOf(string group, string? sub) =>
        !string.IsNullOrEmpty(sub) && IsSubtaskGroup(group) && SubtaskByKey.TryGetValue(sub, out var s) && s.Group == group;

    /// <summary>"Reset · Re-runs" - the hover text of a labelled tile.</summary>
    public static string SubtaskTitle(string groupLabel, string? sub) =>
        sub is not null && SubtaskByKey.TryGetValue(sub, out var s) ? $"{groupLabel} · {s.Label}" : groupLabel;

    // ─── Specials: building days ─────────────────────────────────────────────

    /// <summary>
    /// The weekdays the specials are built (owner, 14/11/2026: Tuesday and
    /// Friday), Monday = 0. On these days Specials is split between Building,
    /// Tidy and Running by their three shares; on every other day no tile is
    /// Building, and Specials is split between Tidy and Running by their two.
    /// </summary>
    public static readonly IReadOnlyList<int> SpecialsBuildingDays = [1, 4];

    // ─── The load board's load types -> Load Running sub-tasks ───────────────
    // Each load board type is the Load Running sub-task of its own name (owner,
    // 14/11/2026). "D's" are a part of Ambient, usually completed last (owner,
    // 14/11/2026): labelled Ambient, but handed out after every other part of the
    // load. Anything unrecognised is Ambient.

    private static readonly (Regex Re, string Key)[] LoadTypeMap =
    [
        (new Regex("produce", RegexOptions.ECMAScript), SubtaskKeys.Produce),
        (new Regex(@"long\s*life", RegexOptions.ECMAScript), SubtaskKeys.LongLife),
        (new Regex(@"short\s*life", RegexOptions.ECMAScript), SubtaskKeys.ShortLife),
        (new Regex(@"freeze\s*(to\s*)?thaw", RegexOptions.ECMAScript), SubtaskKeys.FreezeToThaw),
        (new Regex("freez", RegexOptions.ECMAScript), SubtaskKeys.Freezer),
        (new Regex("bread", RegexOptions.ECMAScript), SubtaskKeys.Bread),
        (new Regex("ambient", RegexOptions.ECMAScript), SubtaskKeys.Ambient),
    ];

    /// <summary>The load types worked last, as part of Ambient: "D's".</summary>
    private static readonly Regex AmbientLast = new("^d['’]?s$");

    /// <summary>Ambient's part worked last (D's), kept apart while the load is handed out.</summary>
    public const string AmbientLastPart = "ambient_last";

    /// <summary>
    /// Which part of the load a load board type is: its sub-task, or Ambient's last
    /// part. A mapping saved on the load type (load_types.sub_task, the Load types
    /// page) decides; with none, the type's name does - so a renamed type keeps
    /// its place once it has a mapping.
    /// </summary>
    public static string LoadPartFor(string typeName, string? mapped = null)
    {
        if (IsLoadPart(mapped)) return mapped!;
        return AmbientLast.IsMatch(JsTrimLower(typeName)) ? AmbientLastPart : LoadSubtaskFor(typeName);
    }

    /// <summary>True for a value a load type's mapping may hold: a Load Running sub-task, or Ambient's last part.</summary>
    public static bool IsLoadPart(string? v) => v == AmbientLastPart || IsSubtaskOf("load_running", v);

    /// <summary>The choices for a load type's mapping, in the load board's order (Load types page).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> LoadPartOptions =
        [.. SubtasksOf("load_running").Select(s => (s.Key, s.Label)), (AmbientLastPart, "Ambient - worked last (like D's)")];

    // ─── Reset tasks -> Reset sub-tasks ──────────────────────────────────────
    // A Reset task counts under the sub-task saved on it (reset_tasks.sub_task,
    // the Labour rules page); with none, its name decides: a name containing
    // "re-run" is Re-runs, anything else Decarding.

    private static readonly Regex ReRunName = new(@"re-?\s?run", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);

    private static bool IsReRunName(string name) => ReRunName.IsMatch(name);

    /// <summary>The Reset sub-task a Reset task counts under: its saved mapping, else its name.</summary>
    public static string ResetSubtaskFor(string taskName, string? mapped = null)
    {
        if (mapped is SubtaskKeys.ReRuns or SubtaskKeys.Decarding) return mapped;
        return IsReRunName(taskName) ? SubtaskKeys.ReRuns : SubtaskKeys.Decarding;
    }

    /// <summary>The Load Running sub-task a load board load type is worked as. Anything unrecognised is Ambient.</summary>
    public static string LoadSubtaskFor(string typeName)
    {
        var n = JsTrimLower(typeName);
        foreach (var (re, key) in LoadTypeMap)
            if (re.IsMatch(n)) return key;
        return SubtaskKeys.Ambient;
    }

    /// <summary>
    /// Which Load Running sub-task is worked first when the load board has no
    /// history to say (the live load types' priorities): Freeze to Thaw first,
    /// then Produce and Short Life, Long Life, Freezer and Ambient, Bread last.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, double Priority)> DefaultLoadPriority =
    [
        (SubtaskKeys.FreezeToThaw, 1),
        (SubtaskKeys.Produce, 3),
        (SubtaskKeys.ShortLife, 3),
        (SubtaskKeys.LongLife, 4),
        (SubtaskKeys.Freezer, 5),
        (SubtaskKeys.Ambient, 5),
        (SubtaskKeys.Bread, 100),
    ];

    // JavaScript's trim() and toLowerCase().
    private static string JsTrimLower(string s) => s.Trim().ToLowerInvariant();
}
