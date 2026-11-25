// Port of src/lib/stage2/order.ts.
// ─── Roster order: the sequence of phases Stage 2 runs ───────────────────────
// Pure module (engine, tests, admin page). The Roster Order super-user page
// stores the configured steps in roster_order_steps; an empty table means
// DEFAULT_ORDER - the client's approved initial order:
//   1. SM to contract (manager shifts)
//   2. ASM opposite / covering the SM (the remaining open/close slot each day
//      the SM works falls to the ASM automatically, and the SM's off days are
//      covered to the ASM's contract)
//   3. DSM the remaining manager shifts (RSM steps up only when no DSM can)
//   4. Floor filled day-by-day in the order Sat, Sun, Tue, Fri, Mon, Wed, Thu
//      (hardest-to-staff days claim scarce availability first)
//   5. Shuffle + JSAs within rules (the fortnight repair pass + junior rescue)
//   6. Contract check, highest contracts first (weighted balance + flags)

namespace Roster.Engine.Rostering.Painter;

/// <summary>The six phase types (OrderStepType).</summary>
public static class OrderStepTypes
{
    public const string AllocateSm = "ALLOCATE_SM";
    public const string AllocateAsm = "ALLOCATE_ASM";
    public const string AllocateDsm = "ALLOCATE_DSM";
    public const string FillFloor = "FILL_FLOOR";
    public const string ShuffleJsa = "SHUFFLE_JSA";
    public const string ContractCheck = "CONTRACT_CHECK";

    public static readonly IReadOnlyList<string> All = [AllocateSm, AllocateAsm, AllocateDsm, FillFloor, ShuffleJsa, ContractCheck];
}

/// <param name="Type">An <see cref="OrderStepTypes"/> value.</param>
/// <param name="DayOrder">
/// ALLOCATE_*/FILL_FLOOR: day-of-week order within each Mon-Sun week
/// (Monday=0 .. Sunday=6) the phase visits days in. Absent = the default
/// hardest-days-first order. Allocating chronologically instead burns weekly
/// day caps on Monday-Friday and leaves the weekend without cover.
/// </param>
public sealed record OrderStep(string Type, IReadOnlyList<int>? DayOrder = null);

public sealed record OrderStepMeta(string Type, string Label, string Description);

public static class RosterOrder
{
    public static readonly IReadOnlyList<string> DowLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // Sat, Sun, Tue, Fri, Mon, Wed, Thu (client's approved order).
    public static readonly IReadOnlyList<int> DefaultFloorDayOrder = [5, 6, 1, 4, 0, 2, 3];

    public static readonly IReadOnlyList<OrderStep> DefaultOrder =
    [
        new(OrderStepTypes.AllocateSm),
        new(OrderStepTypes.AllocateAsm),
        new(OrderStepTypes.AllocateDsm),
        new(OrderStepTypes.FillFloor, DefaultFloorDayOrder),
        new(OrderStepTypes.ShuffleJsa),
        new(OrderStepTypes.ContractCheck),
    ];

    /// <summary>Every phase's label and description, in this order (ORDER_STEP_META).</summary>
    public static readonly IReadOnlyList<OrderStepMeta> StepMeta =
    [
        new(OrderStepTypes.AllocateSm, "Store Manager to contract",
            "The SM takes one open/close manager shift per day until their contract hours are met."),
        new(OrderStepTypes.AllocateAsm, "ASM opposite / covering the SM",
            "The ASM takes the opposite manager shift (opener vs closer) on days the SM works, and covers the SM's off days, to their contract."),
        new(OrderStepTypes.AllocateDsm, "DSMs on the remaining manager shifts",
            "Duty Store Managers fill the manager shifts still open (the RSM steps up only when no DSM can)."),
        new(OrderStepTypes.FillFloor, "Fill the floor",
            "Store-assistant and junior shifts filled day by day in the configured day order - hardest-to-staff days first claim scarce availability."),
        new(OrderStepTypes.ShuffleJsa, "Shuffle + juniors within rules",
            "The fortnight repair pass: moves hours between people until no legal move improves contracts, then rescues open shifts with juniors (including step-across moves)."),
        new(OrderStepTypes.ContractCheck, "Contract check, highest first",
            "Verifies everyone against contract (managers, then largest contracts down) and raises the under-contract flags."),
    ];

    /// <summary>orderStepMeta(type): the phase's meta; null for an unknown type.</summary>
    public static OrderStepMeta? StepMetaFor(string type) => StepMeta.FirstOrDefault(m => m.Type == type);

    /// <summary>
    /// Normalise an untrusted step list (DB rows / admin input) into a valid order.
    /// Unknown types are dropped; a FILL_FLOOR dayOrder is kept only if it is a
    /// permutation of 0..6. Returns DEFAULT_ORDER when nothing valid remains.
    /// (The rule keeps or drops every step's dayOrder, whatever its type, as the
    /// TypeScript does.)
    /// </summary>
    public static IReadOnlyList<OrderStep> NormaliseOrder(IReadOnlyList<OrderStep>? steps)
    {
        if (steps == null || steps.Count == 0) return DefaultOrder;
        var known = new HashSet<string>(StepMeta.Select(m => m.Type));
        var out_ = new List<OrderStep>();
        foreach (var s in steps)
        {
            if (!known.Contains(s.Type)) continue;
            var d = s.DayOrder;
            var valid = d != null && d.Count == 7 && d.Order().SequenceEqual([0, 1, 2, 3, 4, 5, 6]);
            out_.Add(new OrderStep(s.Type, valid ? d : null));
        }
        return out_.Count > 0 ? out_ : DefaultOrder;
    }

    /// <summary>
    /// Human-readable structural warnings for a configured order - shown on the
    /// admin page so a super user sees the consequences before the next generate.
    /// </summary>
    public static List<string> ValidateOrder(IReadOnlyList<OrderStep> steps)
    {
        var warnings = new List<string>();
        var types = steps.Select(s => s.Type).ToList();
        bool Has(string t) => types.Contains(t);
        if (!Has(OrderStepTypes.FillFloor))
            warnings.Add("No 'Fill the floor' step: store-assistant and junior shifts will be left unfilled.");
        if (!Has(OrderStepTypes.AllocateSm) && !Has(OrderStepTypes.AllocateAsm) && !Has(OrderStepTypes.AllocateDsm) && Has(OrderStepTypes.FillFloor))
            warnings.Add(
                "No manager allocation steps: manager shifts are only picked up during the floor fill, so open/close cover may land poorly.");
        if (!Has(OrderStepTypes.ShuffleJsa))
            warnings.Add(
                "No 'Shuffle + juniors' step: the fortnight repair/rescue pass will not run, leaving contract shortfalls and open shifts the shuffle would have fixed.");
        if (!Has(OrderStepTypes.ContractCheck))
            warnings.Add("No 'Contract check' step: under-contract flags will not be raised on the roster.");
        var floorIdx = types.IndexOf(OrderStepTypes.FillFloor);
        foreach (var t in new[] { OrderStepTypes.AllocateSm, OrderStepTypes.AllocateAsm, OrderStepTypes.AllocateDsm })
        {
            var i = types.IndexOf(t);
            if (i >= 0 && floorIdx >= 0 && i > floorIdx)
                warnings.Add(
                    $"'{StepMetaFor(t)!.Label}' runs AFTER the floor fill - the floor may consume the days that cohort needed for manager cover.");
        }
        var shuffleIdx = types.IndexOf(OrderStepTypes.ShuffleJsa);
        if (shuffleIdx >= 0 && floorIdx >= 0 && shuffleIdx < floorIdx)
            warnings.Add("'Shuffle + juniors' runs BEFORE the floor fill - it will have nothing to shuffle.");
        return warnings;
    }
}
