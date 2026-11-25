// Port of the data src/app/(app)/plan/generate/page.tsx works out before it renders.

using Roster.Engine.Core;

namespace Roster.Web.Features.Plan;

/// <summary>A store in the Plan page's switcher (every store in the network: Plan is peer-visible).</summary>
public sealed record PlanStore(Guid Id, string Code, string Name, Guid? AreaId = null);

/// <summary>A day chip's dot: green (OK), red (has an error), grey (closed, no figures, another store's plan).</summary>
public enum PlanChipTone { Ok, Error, Neutral }

/// <summary>One day of the fortnight's chips: the date, where it goes, its dot, its figure and what it says.</summary>
public sealed record PlanDayChip(DateOnly Date, string Href, PlanChipTone Tone, string? Note, string? Status);

/// <summary>
/// Everything /plan/generate shows (page.tsx after its reads): the store and fortnight, who
/// may do what, the held plan and its figures, and the selected day.
/// </summary>
public sealed record PlanPageModel
{
    /// <summary>
    /// Set when the page sends the browser elsewhere instead: an old <c>?view=roster</c> link
    /// lands on /plan/roster, keeping store and date.
    /// </summary>
    public string? RedirectTo { get; init; }

    /// <summary>Every store, by code (the switcher).</summary>
    public IReadOnlyList<PlanStore> Stores { get; init; } = [];

    /// <summary>The store the page is on, or null when the viewer has none to show.</summary>
    public Guid? SelectedStore { get; init; }

    /// <summary>The selected store's row (code and name), when it is one of <see cref="Stores"/>.</summary>
    public PlanStore? StoreRow { get; init; }

    /// <summary>Another store's manager looking in: read-only, and trimmed to the shift grid and benchmark hours.</summary>
    public bool Peer { get; init; }

    /// <summary>Nothing can be changed: peer view, or a read-only tier (PA, DSO).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>The viewer is a store login (the Review link and the locked note speak to them).</summary>
    public bool IsStoreTier { get; init; }

    public DateOnly PeriodStart { get; init; }

    /// <summary>The day shown: <c>?date=</c>, or the fortnight's first day.</summary>
    public DateOnly SelectedDate { get; init; }

    /// <summary>A finished fortnight is a record, not a plan: it opens read-only.</summary>
    public bool IsPastPeriod { get; init; }

    /// <summary>Earlier fortnights still not approved (the overdue banner). Never for peer view.</summary>
    public IReadOnlyList<DateOnly> OverduePeriods { get; init; } = [];

    /// <summary>An approved plan is locked (owner decision R5): no Generate, no edit, no public-holiday move.</summary>
    public bool Locked { get; init; }

    public bool HasSnapshot { get; init; }

    /// <summary>When the held plan was generated.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>Who generated the held plan (their username). Never for peer view.</summary>
    public string? GeneratorName { get; init; }

    /// <summary>The fortnight in figures, as the Review counts it. Never for another store's plan.</summary>
    public PlanFortnightFigures? Figures { get; init; }

    public IReadOnlyList<PlanDayChip> DayChips { get; init; } = [];

    /// <summary>The selected day is a public holiday: the load-running reallocation is offered.</summary>
    public bool IsPublicHolidayDay { get; init; }

    /// <summary>The selected day's public-holiday reallocation is on.</summary>
    public bool PhReallocated { get; init; }

    /// <summary>Night fill (Store Attributes): load running is permitted at night.</summary>
    public bool NightFill { get; init; }

    /// <summary>Where the Review of this fortnight is.</summary>
    public string ReviewHref => $"/plan/review?store={SelectedStore}&period={PeriodStart.ToIso()}";

    /// <summary>The way into the Review shows in the header (not for peer, read-only or past fortnights).</summary>
    public bool ShowReview => !Peer && !IsReadOnly && !IsPastPeriod && SelectedStore is not null;

    /// <summary>Generate is offered.</summary>
    public bool CanGenerate => !IsReadOnly && !IsPastPeriod && SelectedStore is not null && !Locked;

    /// <summary>Why Generate is greyed out although the viewer may generate (the plan is approved and locked).</summary>
    public string? LockedNote =>
        Locked && !IsReadOnly && !IsPastPeriod && SelectedStore is not null
            ? IsStoreTier
                ? "Approved plans are locked - ask on Review to unlock it, then regenerate."
                : "Approved plans are locked - unlock it on Review to regenerate."
            : null;

    /// <summary>The selected day may be edited.</summary>
    public bool CanEdit => !IsReadOnly && !IsPastPeriod && !Locked;
}
