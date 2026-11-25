// Port of src/app/(app)/plan/generate/page.tsx: what the page works out from its reads
// (the lock, the selected day trimmed for peer view, the figures, the day chips and the
// trade curve). The reads themselves are the service's (PlanService, FakePlanService).

using System.Text.Json;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Review;

namespace Roster.Web.Features.Plan;

/// <summary>One row of the store's derived trade curve (peerTradeCurveRows): weekday (Mon = 0), "HH:MM", share.</summary>
public sealed record CurveRow(int DayOfWeek, string BlockTime, double Allocation);

/// <summary>What /plan/generate reads before it renders (page.tsx's queries), for <see cref="PlanPageComposer"/>.</summary>
public sealed record PlanPageReads
{
    public IReadOnlyList<PlanStore> Stores { get; init; } = [];
    public Guid? SelectedStore { get; init; }

    /// <summary>Another store's manager looking in (storeAccessFor = "peer").</summary>
    public bool Peer { get; init; }

    /// <summary>A read-only tier (PA, DSO).</summary>
    public bool ReadOnlyTier { get; init; }

    public bool IsStoreTier { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly SelectedDate { get; init; }
    public bool IsPastPeriod { get; init; }
    public IReadOnlyList<DateOnly> OverduePeriods { get; init; } = [];

    /// <summary>The held plan (readSnapshots' plan, or readPeerPlanSnapshot for peer view).</summary>
    public PlanSnapshot? Snapshot { get; init; }

    /// <summary>
    /// The selected day as held, labelled when it was held from before sub-tasks (labels only:
    /// its shifts, tiles and checks are exactly as held). Null when the plan has no such day.
    /// </summary>
    public PlanSnapshotDay? SelectedDay { get; init; }

    /// <summary>day_plans.ph_load_reallocated for the selected day.</summary>
    public bool PhReallocated { get; init; }

    public bool IsPublicHolidayDay { get; init; }
    public PlanReviewState ReviewState { get; init; }

    /// <summary>The store's cover rules (dayCheckOptions), for the live hours check while editing.</summary>
    public CoverRules? Cover { get; init; }

    public IReadOnlyList<CurveRow> CurveRows { get; init; } = [];
    public bool NightFill { get; init; }
    public string? GeneratorName { get; init; }
}

public static class PlanPageComposer
{
    public static PlanPageModel Compose(PlanPageReads r)
    {
        var peer = r.Peer;
        var isReadOnly = peer || r.ReadOnlyTier;
        var storeRow = r.Stores.FirstOrDefault(s => s.Id == r.SelectedStore);

        // An approved plan is locked (owner decision R5): no Generate, no edit and no
        // public-holiday move until it is unlocked on the Review page.
        var locked = r.ReviewState == PlanReviewState.Approved;
        var dayStatuses = r.Snapshot?.Days ?? [];
        var hasSnapshot = r.Snapshot is not null;

        // Peer trim happens on the server: coverage flags never enter what the page draws.
        var selected = r.SelectedDay;
        var dayForRender = selected is { Plan: { } p } && peer
            ? selected with
            {
                Result = Stripped(selected.Result),
                Plan = p with { Flags = [], Notes = [], Checks = [], Hours = null },
            }
            : selected;

        // The fortnight in figures, as the Review counts it - the cards, the day chips'
        // variances, the generate bar's OK days. Never for another store's plan.
        var figures = r.Snapshot is { } snap && !peer ? PageFigures.PlanFortnightFigures(snap.Days) : null;
        var figuresByDate = new Dictionary<DateOnly, PlanDayFigures>();
        foreach (var f in figures?.Days ?? []) figuresByDate[f.Date] = f;
        // Peer chips are navigation-only: every day neutral, no ok/problem colour.
        var dayChips = dayStatuses.Select(d =>
        {
            var f = figuresByDate.GetValueOrDefault(d.Date);
            var tone = f is null || f.Closed ? PlanChipTone.Neutral : d.Ok ? PlanChipTone.Ok : PlanChipTone.Error;
            return new PlanDayChip(
                d.Date,
                $"/plan/generate?store={r.SelectedStore}&date={d.Date.ToIso()}",
                tone,
                f is null ? null : f.Closed ? "Closed" : f.Planned ? PageFigures.Signed(f.Variance) : "No plan",
                f is null
                    ? null
                    : f.Closed
                        ? "closed"
                        : $"{(d.Ok ? "OK" : "has an error")}{(f.Planned ? $", {PageFigures.Signed(f.Variance)} h against the benchmark" : ", no plan")}");
        }).ToList();

        // The store's weekly trade pattern, for the panel under the plan - the SAME derived
        // curve the engines plan with - and at 15 minutes for the heat row in the grid.
        TradeCurveWeek? tradeCurve = null;
        IReadOnlyList<IReadOnlyList<CurveShare>>? quarterHours = null;
        if (r.CurveRows.Count > 0)
        {
            List<QuarterHourPoint> Of(int d) =>
                r.CurveRows.Where(c => c.DayOfWeek == d).Select(c => new QuarterHourPoint(c.BlockTime, c.Allocation)).ToList();
            tradeCurve = new TradeCurveWeek(Enumerable.Range(0, 7).Select(d => (IReadOnlyList<CurvePoint>)TradeCurve.BucketHourly(Of(d))).ToList());
            quarterHours = Enumerable.Range(0, 7).Select(d => (IReadOnlyList<CurveShare>)TradeCurve.QuarterHourShares(Of(d))).ToList();
        }

        return new PlanPageModel
        {
            Stores = r.Stores,
            SelectedStore = r.SelectedStore,
            StoreRow = storeRow,
            Peer = peer,
            IsReadOnly = isReadOnly,
            IsStoreTier = r.IsStoreTier,
            PeriodStart = r.PeriodStart,
            SelectedDate = r.SelectedDate,
            IsPastPeriod = r.IsPastPeriod,
            OverduePeriods = peer ? [] : r.OverduePeriods,
            ReviewState = r.ReviewState,
            Locked = locked,
            HasSnapshot = hasSnapshot,
            GeneratedAt = r.Snapshot?.GeneratedAt,
            GeneratorName = peer ? null : r.GeneratorName,
            Figures = figures,
            DayChips = dayChips,
            IsPublicHolidayDay = !peer && r.IsPublicHolidayDay,
            PhReallocated = r.PhReallocated,
            NightFill = r.NightFill,
            Day = dayForRender is null ? null : new PlanDayView(dayForRender.Result, dayForRender.Plan),
            Cover = peer ? null : r.Cover,
            TradeCurve = tradeCurve,
            TradeQuarterHours = quarterHours,
        };
    }

    // result: { ...result, flags: [] } - a copy with no flags (Stage1Computation is a
    // class, so the copy is made through its JSON, as the snapshot holds it).
    private static Stage1Computation Stripped(Stage1Computation s1)
    {
        var copy = JsonSerializer.Deserialize<Stage1Computation>(JsonSerializer.Serialize(s1, RosterJson.Options), RosterJson.Options)!;
        copy.Flags = [];
        return copy;
    }
}
