// Port of src/lib/load/allocate.ts.
// ─── Load Planner allocator ────────────────────────────────────────────────
// Converts the manual HTML "Load Planner" into a roster-driven one:
// the crew and their start times come from today's approved roster (anyone
// with Load Running tiles), the day's pallet counts are entered once, and THIS
// module decides who unloads what - the one job the HTML left to a human.
//
// Model (real store practice): each person OWNS a load category and works it
// end-to-end - they don't all pick at the same pile. Categories get headcount
// in proportion to their size (a big Ambient load gets 2 people, Chiller/
// Freezer/Produce one each), so everyone finishes around the same time. When a
// person clears their category, their leftover time goes to whichever category
// still has the most work outstanding - they help finish it rather than stand
// idle. Each pallet of a type takes a fixed number of minutes and a person's
// pallets stack sequentially from their start with no gaps. Deterministic:
// everyone who opens the page sees the same chart.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Load;

public sealed record LoadCrewMember
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public double StartMin { get; init; } // when their Load Running work begins (minutes from midnight)
    /// <summary>
    /// Which tenure band this person is in today, from their start date. Each
    /// load type carries its own minutes per pallet per band, so a new starter's
    /// pallets are priced from the Load Types table rather than by inflating a
    /// veteran's number. Null means "gt90" - an unknown tenure never slows
    /// anyone down.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenureBand { get; init; }

    public LoadCrewMember() { }

    public LoadCrewMember(string key, string name, double startMin, string? tenureBand = null)
    {
        Key = key;
        Name = name;
        StartMin = startMin;
        TenureBand = tenureBand;
    }
}

public sealed record LoadItem
{
    public string Type { get; init; } = "";
    public string Color { get; init; } = "";
    /// <summary>Minutes per pallet for each tenure band - see Probation.</summary>
    public PalletMinutes Minutes { get; init; } = new();
    public double Qty { get; init; }
    /// <summary>
    /// Allocation order (lower = worked first): freeze-to-thaw before liquor
    /// before fresh, per the operating rule. Null = unranked (100).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Priority { get; init; }

    public LoadItem() { }

    public LoadItem(string type, string color, PalletMinutes minutes, double qty, double? priority = null)
    {
        Type = type;
        Color = color;
        Minutes = minutes;
        Qty = qty;
        Priority = priority;
    }
}

/// <summary>A run of one person's bar: the warm-up, or pallets of one type. Mutable: pallets are added to the last run.</summary>
public sealed class LoadSegment
{
    public string Type { get; set; } = "";
    public string Color { get; set; } = "";
    public double Qty { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    public LoadSegment() { }

    public LoadSegment(string type, string color, double qty, double start, double end)
    {
        Type = type;
        Color = color;
        Qty = qty;
        Start = start;
        End = end;
    }
}

public sealed class CrewSchedule
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public double StartMin { get; set; }
    /// <summary>Resolved tenure band - always set once the plan is built.</summary>
    public string TenureBand { get; set; } = TenureBands.Gt90;
    public double FinishMin { get; set; }
    public List<LoadSegment> Segments { get; set; } = new();
    /// <summary>The category this person owns from the start.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryType { get; set; }
}

public sealed record LoadPlan(List<CrewSchedule> Crew, double TeamStart, double TeamFinish, double TotalPallets);

public sealed record AllocateOptions
{
    // Manual overrides (SM edit mode): crew key → the category that person OWNS
    // today, taking precedence over automatic owner assignment.
    public JsMap<string, string>? Pinned { get; init; }
    // Rotation history: `${crewKey}:${type}` → how many times that person owned
    // that category recently. Auto assignment prefers the person who has done a
    // category the LEAST, so unload jobs rotate through the team over the days.
    public JsMap<string, double>? TypeCounts { get; init; }
}

public static class Allocate
{
    // ─── Stretching ──────────────────────────────────────────────────────────
    // Every person warms up before touching a pallet. It is not optional and not a
    // load type, so it is seeded by the allocator rather than entered on the truck
    // form - and it genuinely moves the finish time, which is the point of showing
    // it rather than quietly assuming it away.

    /// <summary>
    /// Mandatory warm-up at the start of every crew member's shift, in minutes.
    /// Counted inside the base roster's Load Running time - the roster never shows
    /// it separately; only the Load Planner does.
    /// </summary>
    public const double StretchMinutes = 5;

    /// <summary>Segment label. Not a load type - carries no pallets and no minutes/pallet.</summary>
    public const string StretchType = "Stretching";

    /// <summary>
    /// Its own tile colour: deliberately outside the load-type palette, because it
    /// is preparation rather than product.
    /// </summary>
    public const string StretchColor = "#64748b";

    /// <summary>Does this segment carry pallets, or is it the warm-up?</summary>
    public static bool IsStretch(LoadSegment s) => IsStretch(s.Type);

    /// <inheritdoc cref="IsStretch(LoadSegment)"/>
    public static bool IsStretch(string type) => type == StretchType;

    private sealed class Category
    {
        public string Type = "";
        public string Color = "";
        public PalletMinutes Minutes = new();
        public double Remaining; // pallets left to unload
        public double Priority;
    }

    public static LoadPlan AllocateLoad(IReadOnlyList<LoadCrewMember> crewIn, IReadOnlyList<LoadItem> loads, AllocateOptions? opts = null)
    {
        opts ??= new AllocateOptions();
        var crew = Js.Sorted(crewIn, (a, b) => (a.StartMin - b.StartMin).Or(Js.LocaleCompare(a.Name, b.Name)))
            .Select(c => new CrewSchedule
            {
                Key = c.Key,
                Name = c.Name,
                StartMin = c.StartMin,
                TenureBand = c.TenureBand ?? TenureBands.Gt90,
                // Pallets start AFTER the warm-up, so every downstream time already
                // accounts for it without any other phase needing to know it exists.
                FinishMin = c.StartMin + StretchMinutes,
                Segments = [new LoadSegment(StretchType, StretchColor, 0, c.StartMin, c.StartMin + StretchMinutes)],
            })
            .ToList();

        // Categories with real work: the configured PRIORITY first (freeze-to-thaw
        // -> liquor -> fresh -> long life -> ambient/freezer), then biggest
        // total-minutes among equals (stable by name).
        var cats = loads
            .Select(l => new Category
            {
                Type = l.Type,
                Color = l.Color,
                Minutes = l.Minutes,
                Remaining = Js.Max(0, Math.Floor(l.Qty)),
                Priority = l.Priority ?? 100,
            })
            .Where(c => c.Remaining > 0)
            .ToList();
        // Sized on the TRAINED figure so the ordering of the work does not shuffle
        // about with who happens to be rostered on.
        static double TotalMin(Category c) => c.Remaining * c.Minutes.Gt90;
        Js.Sort(cats, (a, b) => (a.Priority - b.Priority).Or(TotalMin(b) - TotalMin(a)).Or(Js.LocaleCompare(a.Type, b.Type)));

        if (crew.Count == 0 || cats.Count == 0)
        {
            // Everyone still stretches, so report that window rather than zeroes - a
            // 00:00-00:00 plan would read as broken rather than as "no pallets yet".
            return new LoadPlan(
                crew,
                crew.Count > 0 ? Js.Min(crew.Select(c => c.StartMin)) : 0,
                crew.Count > 0 ? Js.Max(crew.Select(c => c.FinishMin)) : 0,
                0);
        }
        var catByType = new JsMap<string, Category>(cats.Select(c => (c.Type, c)));

        // ── Headcount per category: one each, then hand spare bodies to whichever
        //    category has the worst minutes-per-person (so a big Ambient load draws
        //    the extra body). Fewer people than categories → the biggest categories
        //    get the single owners; the rest are picked up in the help phase.
        var headcount = new JsMap<string, double>(cats.Select(c => (c.Type, 0.0)));
        var p = crew.Count;
        if (p >= cats.Count)
        {
            foreach (var c in cats) headcount.Set(c.Type, 1);
            var spare = p - cats.Count;
            while (spare > 0)
            {
                var worst = cats[0];
                foreach (var c in cats)
                {
                    if (TotalMin(c) / headcount[c.Type] > TotalMin(worst) / headcount[worst.Type]) worst = c;
                }
                headcount.Set(worst.Type, headcount[worst.Type] + 1);
                spare--;
            }
        }
        else
        {
            for (var i = 0; i < p; i++) headcount.Set(cats[i].Type, 1);
        }

        // ── Assign owners. Pinned people (SM edit mode) take their category first
        //    and consume one of its slots. Remaining slots fill biggest-category
        //    first, each going to the unassigned person who has owned that category
        //    the LEAST recently (rotation) - ties broken by earliest start, then
        //    name, so the result stays deterministic.
        var activeType = new JsMap<string, string?>();
        var unassigned = new JsSet<string>(crew.Select(c => c.Key));
        foreach (var person in crew)
        {
            var pin = opts.Pinned?.Get(person.Key);
            if (string.IsNullOrEmpty(pin) || !catByType.Has(pin)) continue;
            person.PrimaryType = pin;
            activeType.Set(person.Key, pin);
            unassigned.Delete(person.Key);
            headcount.Set(pin, Js.Max(0, headcount.GetOr(pin, 0) - 1));
        }
        double CountFor(string key, string type) => opts.TypeCounts?.GetOr($"{key}:{type}", 0) ?? 0;
        foreach (var c in cats)
        {
            for (var k = 0; k < headcount.GetOr(c.Type, 0); k++)
            {
                CrewSchedule? pick = null;
                foreach (var key in unassigned)
                {
                    var person = crew.First(x => x.Key == key);
                    if (
                        pick is null ||
                        CountFor(person.Key, c.Type) < CountFor(pick.Key, c.Type) ||
                        (CountFor(person.Key, c.Type) == CountFor(pick.Key, c.Type) &&
                            (person.StartMin < pick.StartMin ||
                                (person.StartMin == pick.StartMin && Js.LocaleCompare(person.Name, pick.Name) < 0))))
                    {
                        pick = person;
                    }
                }
                if (pick is null) break;
                pick.PrimaryType = c.Type;
                activeType.Set(pick.Key, c.Type);
                unassigned.Delete(pick.Key);
            }
        }

        // ── Simulate: while work remains, the earliest-free person takes the next
        //    pallet from their active category; when it empties they switch to the
        //    category with the most work left (help finish it). Contiguous blocks
        //    per person by construction.
        double totalPallets = 0;
        while (cats.Any(c => c.Remaining > 0))
        {
            var person = crew.Aggregate((best, c) =>
                c.FinishMin < best.FinishMin ||
                (c.FinishMin == best.FinishMin &&
                    (c.StartMin < best.StartMin ||
                        (c.StartMin == best.StartMin && Js.LocaleCompare(c.Name, best.Name) < 0)))
                    ? c
                    : best);
            var active = activeType.Get(person.Key);
            var cat = !string.IsNullOrEmpty(active) ? catByType.Get(active) : null;
            if (cat is null || cat.Remaining <= 0)
            {
                // Switch by the same order the day is worked: highest priority first
                // (freeze-to-thaw before liquor before fresh), most outstanding minutes
                // among equals. A freed body must not drift to a big Ambient pile while
                // a time-critical category still has pallets down.
                cat = cats
                    .Where(c => c.Remaining > 0)
                    .Aggregate((best, c) =>
                        c.Priority < best.Priority ||
                        (c.Priority == best.Priority &&
                            (TotalMin(c) > TotalMin(best) ||
                                (TotalMin(c) == TotalMin(best) && string.CompareOrdinal(c.Type, best.Type) < 0)))
                            ? c
                            : best);
                activeType.Set(person.Key, cat.Type);
            }
            // This person's minutes for one pallet of this category - the category
            // rate stretched by their own speed factor.
            var palletMin = Probation.MinutesForBand(cat.Minutes, person.TenureBand);
            var last = person.Segments.Count > 0 ? person.Segments[^1] : null;
            if (last is not null && last.Type == cat.Type)
            {
                last.Qty += 1;
                last.End += palletMin;
            }
            else
            {
                person.Segments.Add(new LoadSegment(cat.Type, cat.Color, 1, person.FinishMin, person.FinishMin + palletMin));
            }
            person.FinishMin += palletMin;
            cat.Remaining--;
            totalPallets++;
        }

        var working = crew.Where(c => c.Segments.Count > 0).ToList();
        return new LoadPlan(
            crew,
            working.Count > 0 ? Js.Min(working.Select(c => c.StartMin)) : 0,
            working.Count > 0 ? Js.Max(working.Select(c => c.FinishMin)) : 0,
            totalPallets);
    }
}
