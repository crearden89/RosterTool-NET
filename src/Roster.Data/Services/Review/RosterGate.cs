// Port of src/lib/review/gate.ts.

namespace Roster.Data.Services.Review;

// ─── Plan -> Review -> Roster ────────────────────────────────────────────────
// A fortnight's roster is built only from a plan its store submitted and its
// EMSO approved. These keep that true: the gate the Roster page and every
// roster action check, and the lapse that sends a changed plan back round.

public sealed record RosterGate
{
    // May the roster be built and edited? Only from a plan approved as it
    // stands (owner decision R5). A published roster no longer reopens it: once
    // published, a changed plan used to leave building and editing open for
    // good (review RC-17).
    public bool Unlocked { get; init; }
    // Approved through the review, on the plan as it stands - and so locked:
    // the plan can't be edited or regenerated until it is unlocked.
    public bool PlanApproved { get; init; }
    // Published to the floor: it stays readable whatever happens to the plan.
    public bool Published { get; init; }
    // The roster on the page was built from a plan that has since changed - the
    // plan was unlocked after the roster was published, or approved again after
    // it was built. The Roster page fades until it is rebuilt (R5).
    public bool OutOfDate { get; init; }
    public string? ReviewStatus { get; init; }
}

/// <summary>What <see cref="RosterGates.GateFrom"/> decides from.</summary>
public sealed record RosterGateInput
{
    public string? ReviewStatus { get; init; }
    public string? ReviewKey { get; init; }
    /// <summary>The held plan's version; null = no plan is held.</summary>
    public int? PlanVersion { get; init; }
    public IReadOnlyList<DateOnly> Dates { get; init; } = [];
    public IReadOnlySet<DateOnly> ApprovedDates { get; init; } = new HashSet<DateOnly>();
    // When the plan was approved, and when the held roster was built.
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? RosterGeneratedAt { get; init; }
}

/// <summary>Who did something on a review (the signed-in user).</summary>
public sealed record ReviewActor(Guid Id, string Username, string Tier);

public static class RosterGates
{
    public static RosterGate GateFrom(RosterGateInput input)
    {
        var planApproved = input.ReviewStatus == "approved" && input.PlanVersion is not null
            && input.ReviewKey == ReviewFormat.PlanKey(input.PlanVersion);
        var published = input.Dates.Count > 0 && input.Dates.All(input.ApprovedDates.Contains);
        var builtBeforeApproval = planApproved
            && input.RosterGeneratedAt is not null
            && input.DecidedAt is not null
            && input.RosterGeneratedAt.Value < input.DecidedAt.Value;
        return new RosterGate
        {
            Unlocked = planApproved,
            PlanApproved = planApproved,
            Published = published,
            OutOfDate = (published && !planApproved) || builtBeforeApproval,
            ReviewStatus = input.ReviewStatus,
        };
    }

    public const string RosterLocked =
        "The roster can't be built until this fortnight's plan is approved: the store submits it on the Review page and its EMSO approves it.";

    public const string RosterOutOfDate =
        "This roster was built from an earlier version of the plan. Regenerate it from the approved plan first.";

    // An approved plan is locked (owner decision R5): no edit, no regenerate, no
    // change to the inputs that rebuild it, until it is deliberately unlocked on
    // the Review page by the EMSO or above, at the store's request.
    public const string PlanLocked =
        "This fortnight's plan is approved and locked. To change it, ask for it to be unlocked on the Review page.";
}

/// <summary>The gate between plan, review and roster, read from the database.</summary>
public interface IReviewGate
{
    /// <summary>rosterGate: the gate for a store's fortnight.</summary>
    Task<RosterGate> RosterGateAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// rosterChangeRefusal: why the roster can't be changed or published now, if
    /// it can't (its plan isn't approved, or it was built from an earlier
    /// version of the plan); null when it can.
    /// </summary>
    Task<string?> RosterChangeRefusalAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>planLocked: the fortnight's plan is approved, so locked (R5).</summary>
    Task<bool> PlanLockedAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// lapseReviewOnPlanChange: the plan changed after it was submitted or
    /// approved, so the review starts again - back with the store, every day for
    /// the EMSO to look at afresh, the plan approval withdrawn - and the EMSO
    /// hears about it. <paramref name="what"/> says what changed ("The plan was
    /// regenerated"). Does nothing for a review in progress or returned.
    /// </summary>
    Task LapseReviewOnPlanChangeAsync(Guid storeId, DateOnly periodStart, ReviewActor actor, string what, CancellationToken ct = default);
}
