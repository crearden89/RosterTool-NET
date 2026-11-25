// Port of the data src/app/(app)/plan/roster/page.tsx works out before it renders: the
// fields are what its reads produce, the properties below them its decisions.

using Roster.Data.Services.Approval;
using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Review;
using Roster.Web.Features.RosterPage.Grid;

namespace Roster.Web.Features.RosterPage;

/// <summary>The page's address: <c>/plan/roster?store=&amp;date=&amp;period=</c>, as typed.</summary>
public sealed record RosterPageQuery(string? Store = null, string? Date = null, string? Period = null);

/// <summary>An active team member, for the editor's person pickers (by name).</summary>
public sealed record RosterEmployee(Guid Id, string Name, string Role);

/// <summary>A filled shift the editor may change (computeRosterRows' lockRows); open shifts have none.</summary>
public sealed record RosterLockRow(Guid EmployeeId, string Name, string Role, double Start, double End, bool Locked);

/// <summary>The selected day's roster as the grid shows it (computeRosterRows).</summary>
public sealed record RosterDayView
{
    /// <summary>False when Stage 2 could not produce a roster for the day.</summary>
    public bool HasData { get; init; }

    /// <summary>The decorated rows, keyed by employee id (open shifts by their slot).</summary>
    public IReadOnlyList<RosterGridRow> Rows { get; init; } = [];

    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Each filled shift's cost, by row key. Open shifts have no wage and no entry.</summary>
    public IReadOnlyDictionary<string, double> CostByKey { get; init; } = new Dictionary<string, double>();

    public double DayTotalCost { get; init; }

    /// <summary>The ideal floor's cost for the day (spec §6.5).</summary>
    public double IdealCost { get; init; }

    public IReadOnlyList<RosterLockRow> LockRows { get; init; } = [];
}

/// <summary>The fortnight's cost against the ideal floor (computeFortnightCostDelta, spec §6.5).</summary>
public sealed record RosterFortnightCost(double Ideal, double Actual, double Delta);

/// <summary>One person more than half an hour off their fortnight contract (lib/roster/contracts ContractGap).</summary>
public sealed record RosterContractGap(Guid EmployeeId, string Name, string Role, bool Over, double Hours);

/// <summary>
/// Where the team landed against contract this fortnight (lib/roster/contracts
/// ContractFigures): the engine's own outcomes, held with the roster since it was built.
/// </summary>
public sealed record RosterContracts
{
    /// <summary>Everyone more than half an hour off their contract, the largest gap first.</summary>
    public IReadOnlyList<RosterContractGap> People { get; init; } = [];

    public int Under { get; init; }
    public int Over { get; init; }

    // The totals of the hours as shown (two decimals each), so they agree with the rows
    // they total.
    public double UnderHours { get; init; }
    public double OverHours { get; init; }

    /// <summary>The team's line, as the engine words it: which way its contracts are out.</summary>
    public string? Team { get; init; }
}

/// <summary>One of the day's checks, graded as lib/roster/dayStatus grades them.</summary>
public sealed record RosterCheck(string Key, CheckTone Tone, string Message);

/// <summary>Everything /plan/roster shows for the signed-in user and the page's query.</summary>
public sealed record RosterPageModel
{
    /// <summary>The stores in the switcher, by code.</summary>
    public IReadOnlyList<StoreOption> Stores { get; init; } = [];

    /// <summary>A store login's own store, else the store asked for or remembered; null when there is none.</summary>
    public Guid? SelectedStore { get; init; }

    public DateOnly PeriodStart { get; init; }

    /// <summary>The day shown: <c>?date=</c>, or the fortnight's first day.</summary>
    public DateOnly SelectedDate { get; init; }

    /// <summary>The fortnight's dates.</summary>
    public IReadOnlyList<DateOnly> PlanDates { get; init; } = [];

    /// <summary>A finished fortnight is a record, not a plan: it opens read-only.</summary>
    public bool IsPastPeriod { get; init; }

    /// <summary>The viewer's tier only reads (PA, DSO).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>The viewer is a store login (the Review link speaks to them).</summary>
    public bool IsStoreTier { get; init; }

    /// <summary>
    /// Earlier fortnights still unapproved, when the page opened on the earliest unapproved one
    /// (§5.2): no <c>?period=</c> or <c>?date=</c> was given.
    /// </summary>
    public IReadOnlyList<DateOnly> OverduePeriods { get; init; } = [];

    /// <summary>A plan is held for the fortnight (the roster is built from it).</summary>
    public bool HasPlan { get; init; }

    /// <summary>A roster is held for the fortnight.</summary>
    public bool HasSnapshot { get; init; }

    /// <summary>When the held roster was generated.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    public PlanReviewState ReviewState { get; init; } = PlanReviewState.NotSubmitted;

    /// <summary>Plan -> Review -> Roster (lib/review/gate).</summary>
    public RosterGate Gate { get; init; } = new();

    /// <summary>
    /// Every day of the fortnight is approved, or deliberately closed (a closed day has nothing
    /// to roster and counts as approved, else one closed day would hide Print forever).
    /// </summary>
    public bool RosterApproved { get; init; }

    /// <summary>When the fortnight went onto the floor: the latest approved day's stamp.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Has the selected day been re-solved since it was published to the floor?</summary>
    public RosterDrift Drift { get; init; } = RosterDrift.None;

    /// <summary>Days whose errors need a note on cover before the roster is published (§6.4).</summary>
    public IReadOnlyList<DateOnly> CoverNoteDays { get; init; } = [];

    /// <summary>Plain words for why the note is asked for, naming the days.</summary>
    public string? CoverNoteReason { get; init; }

    public RosterFortnightCost? FortnightCost { get; init; }

    /// <summary>Null for a roster held before the figures were kept.</summary>
    public RosterContracts? Contracts { get; init; }

    /// <summary>
    /// The fortnight flags listed under the contract figures: every flag the figures do not
    /// show (lib/roster/contracts otherFortnightFlags), or all of them for a roster held
    /// without figures.
    /// </summary>
    public IReadOnlyList<string> ContractFlags { get; init; } = [];

    public IReadOnlyList<DayChip> DayChips { get; init; } = [];

    /// <summary>The selected day's roster; null when there is no held roster for it.</summary>
    public RosterDayView? Day { get; init; }

    /// <summary>The selected day is a closed day in the held roster.</summary>
    public bool HeldDayClosed { get; init; }

    /// <summary>The day's checks: errors, then warnings and notes, or the one "no flags" line.</summary>
    public IReadOnlyList<RosterCheck> DayChecks { get; init; } = [];

    /// <summary>Active team members, by name, for the editor's pickers.</summary>
    public IReadOnlyList<RosterEmployee> ActiveEmployees { get; init; } = [];

    // ─── page.tsx's decisions ────────────────────────────────────────────────

    public string? StoreName => Stores.FirstOrDefault(s => s.Id == SelectedStore)?.Name;

    public string ReviewHref => $"plan/review?store={SelectedStore}&period={Iso(PeriodStart)}";

    public bool CanChangeRoster => Gate.Unlocked && !Gate.OutOfDate;

    public bool ShowReview => !IsReadOnly && !IsPastPeriod && SelectedStore is not null;

    public bool CanPublish => ShowReview && CanChangeRoster && HasSnapshot;

    public bool CanGenerate => !IsReadOnly && !IsPastPeriod && SelectedStore is not null && Gate.Unlocked && HasPlan;

    /// <summary>The roster is locked until the plan is approved - unless it is already on the floor.</summary>
    public bool Locked => !Gate.Unlocked && !Gate.Published;

    /// <summary>The editor is read-only: a read-only tier, a past fortnight, no store, or a roster that can't change.</summary>
    public bool EditorReadOnly => IsReadOnly || IsPastPeriod || SelectedStore is null || !CanChangeRoster;

    public bool DayHasError => DayChecks.Any(c => c.Tone == CheckTone.Error);

    /// <summary>
    /// Built from an earlier version of the plan: shown faded and read-only until rebuilt -
    /// the floor keeps a published one meanwhile.
    /// </summary>
    public string? OutOfDateNote =>
        !Gate.OutOfDate ? null
        : Gate.PlanApproved
            ? "Built from an earlier version of the plan - Regenerate Roster to rebuild it from the approved plan."
            : "The plan has changed since this roster was published. The floor keeps this roster until the new plan is approved and the roster is rebuilt from it.";

    /// <summary>The header's line of facts.</summary>
    public string Subtitle =>
        (StoreName is { } n ? $"{n} · " : "") +
        $"fortnight {Dates.AuRange(PeriodStart, Period.PeriodEndFrom(PeriodStart))}" +
        (GeneratedAt is { } g
            ? $" · held roster generated {ReviewFormat.Stamp(g)}{(Gate.PlanApproved && !Gate.OutOfDate ? " from the approved plan" : "")}"
            : "");

    /// <summary>The floor bar's title: what the Floor Display and the printed roster show now.</summary>
    public string FloorTitle =>
        RosterApproved
            ? $"Published{(PublishedAt is { } p ? $" {ReviewFormat.Stamp(p)}" : "")} · all {PlanDates.Count} days"
            : "Not on the floor yet";

    /// <summary>The floor bar's line under its title.</summary>
    public string FloorLine =>
        RosterApproved
            ? Drift.Drifted && !Gate.OutOfDate
                // The floor display shows what was published; this page shows the roster as
                // last built (lib/roster/drift).
                ? $"The floor still shows the roster published {(Drift.ApprovedAt is { } a ? ReviewFormat.Stamp(a) : "earlier")}; this one was built {(Drift.RegeneratedAt is { } r ? ReviewFormat.Stamp(r) : "since")}. Withdraw and publish again to put this one on the floor."
                : "Edits need a withdraw and publish again before the floor shows them."
            : CanPublish
                ? "Approve the roster to put it on the Floor Display and the printed roster."
                : "The Floor Display and the printed roster wait for the roster to be approved.";

    public string PrintHref => $"/api/print/ukg-roster?store={SelectedStore}&start={Iso(PeriodStart)}";

    public string ExportHref => $"/api/export/ukg?store={SelectedStore}&start={Iso(PeriodStart)}";

    /// <summary>What the day's card says when there is no roster to show for it.</summary>
    public string EmptyDayMessage =>
        !HasSnapshot
            ? "No roster has been generated for this fortnight yet - press Generate Roster to build it from the approved plan."
            : HeldDayClosed
                ? "Store closed on this date - nothing to roster."
                : "No roster could be generated for this date (needs benchmark, trading hours and POS quarter-hour data). Regenerate after fixing the inputs.";

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
