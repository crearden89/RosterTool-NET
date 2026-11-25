// Port of src/app/(app)/plan/roster/page.tsx: what the page works out from its reads
// (the reads themselves are RosterService's).

using System.Text.RegularExpressions;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Review;
using Roster.Web.Features.RosterPage.Grid;
using RosterDrift = Roster.Data.Services.Approval.RosterDrift;

namespace Roster.Web.Features.RosterPage;

/// <summary>A day_plans row as the page reads it.</summary>
public sealed record RosterDayStatusRow(DateOnly Date, string RosterStatus, DateTimeOffset? RosterGeneratedAt);

/// <summary>An employee as the page reads them (getEmployees).</summary>
public sealed record RosterEmployeeRow(Guid Id, string Name, string Role, bool Active);

/// <summary>The fortnight's roster_reviews row.</summary>
public sealed record RosterReviewRow(string? Status, string? SnapshotKey, DateTimeOffset? DecidedAt);

/// <summary>Everything page.tsx has read before it decides anything.</summary>
public sealed record RosterPageInputs
{
    public IReadOnlyList<StoreOption> Stores { get; init; } = [];
    public Guid? SelectedStore { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsStoreTier { get; init; }
    public DateOnly PeriodStart { get; init; }
    public IReadOnlyList<DateOnly> PlanDates { get; init; } = [];
    public DateOnly SelectedDate { get; init; }
    public IReadOnlyList<DateOnly> OverduePeriods { get; init; } = [];

    /// <summary>currentPeriodStart(): a fortnight before it is a record, read-only.</summary>
    public DateOnly CurrentPeriodStart { get; init; }

    /// <summary>The held plan's version; null when no plan is held.</summary>
    public int? PlanVersion { get; init; }

    /// <summary>The held roster's days; null when no roster is held.</summary>
    public JsMap<DateOnly, RosterComputation>? RosterByDate { get; init; }

    public IReadOnlyList<string> FortnightFlags { get; init; } = [];

    /// <summary>The engine's contract outcomes held with the roster; null on rosters held before them.</summary>
    public IReadOnlyList<ContractOutcome>? Outcomes { get; init; }

    public DateTimeOffset? RosterGeneratedAt { get; init; }
    public IReadOnlyList<RosterDayStatusRow> StatusRows { get; init; } = [];
    public IReadOnlyList<RosterEmployeeRow> Employees { get; init; } = [];
    public RosterReviewRow? Review { get; init; }

    /// <summary>The selected day's rows and costs (computeRosterRows); null without a held day.</summary>
    public RosterRows? DayRows { get; init; }

    public CostDelta? FortnightCost { get; init; }
    public RosterDrift Drift { get; init; } = RosterDrift.None;
}

public static partial class RosterPageBuilder
{
    // The engine's one-line explanation under a day's open shifts.
    [GeneratedRegex(@"^Why:\s*", RegexOptions.ECMAScript)]
    private static partial Regex Why();

    public static RosterPageModel Build(RosterPageInputs i)
    {
        var fortnight = i.RosterByDate;
        var dates = i.PlanDates;
        // A day's pill is red only for an ERROR - an open shift or a cover gap, graded as
        // the Review grades them; warnings and notes leave it green, as on the Plan page.
        var statuses = fortnight is null ? [] : dates.Select(d => (Date: d, Ok: DayStatus.RosterDayOk(fortnight.Get(d)))).ToList();
        // Those same days need a note on the attempts to cover them before the roster is
        // published (§6.4); warnings alone never do.
        var coverNoteDays = fortnight is null ? [] : DayStatus.DaysNeedingCoverNote(fortnight, dates);
        var heldDay = fortnight?.Get(i.SelectedDate);

        // Roster approval is all-or-nothing across the whole fortnight, so "approved" means
        // every day in the period is. A deliberately closed day has nothing to roster - it
        // counts as approved, otherwise one closed day would hide the Print button forever.
        var statusByDate = new Dictionary<DateOnly, RosterDayStatusRow>();
        foreach (var r in i.StatusRows) statusByDate[r.Date] = r;
        var gate = RosterGates.GateFrom(new RosterGateInput
        {
            ReviewStatus = i.Review?.Status,
            ReviewKey = i.Review?.SnapshotKey,
            PlanVersion = i.PlanVersion,
            Dates = dates,
            ApprovedDates = i.StatusRows.Where(r => r.RosterStatus == "approved").Select(r => r.Date).ToHashSet(),
            DecidedAt = i.Review?.DecidedAt,
            RosterGeneratedAt = i.RosterGeneratedAt,
        });
        var rosterApproved = dates.Count > 0 && dates.All(d =>
            (statusByDate.TryGetValue(d, out var s) && s.RosterStatus == "approved") || fortnight?.Get(d)?.Closed == true);
        // When the fortnight went onto the floor: publishing stamps every day.
        var publishedAt = i.StatusRows
            .Where(r => r.RosterStatus == "approved" && r.RosterGeneratedAt is not null)
            .Select(r => r.RosterGeneratedAt!.Value)
            .DefaultIfEmpty()
            .Max();

        // The contract figures: the engine's outcomes, held with the roster since it was
        // built (lib/roster/contracts); a roster held before has none.
        var figures = i.Outcomes is { } outcomes ? Contracts.ContractFigures(outcomes) : null;

        var chips = statuses.Select(d =>
        {
            var day = fortnight!.Get(d.Date);
            var n = day?.Shifts.Count ?? 0;
            var shifts = $"{n} {(n == 1 ? "shift" : "shifts")}";
            var closed = day?.Closed == true;
            return new DayChip
            {
                Date = d.Date,
                Href = $"plan/roster?store={i.SelectedStore}&date={RosterPageModel.Iso(d.Date)}",
                Tone = closed ? DayTone.Neutral : d.Ok ? DayTone.Ok : DayTone.Error,
                Note = closed ? "Closed" : day?.HasData == true ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "No roster",
                NoteWide = closed || day?.HasData != true ? null : shifts,
                Status = closed ? "closed" : $"{(d.Ok ? "OK" : "has an error")}, {shifts}",
            };
        }).ToList();

        // The day's checks, graded as dayStatus grades them: errors (an open shift, a
        // cover gap), then warnings and notes; the open shifts' "Why:" as a note.
        var checks = new List<RosterCheck>();
        if (i.DayRows is { HasData: true } rows)
        {
            var split = DayStatus.SplitRosterFlags(rows.Flags);
            for (var k = 0; k < split.Errors.Count; k++)
                checks.Add(new($"e{k}", Why().IsMatch(split.Errors[k]) ? CheckTone.Note : CheckTone.Error, split.Errors[k]));
            for (var k = 0; k < split.Warnings.Count; k++)
                checks.Add(new($"w{k}", Why().IsMatch(split.Warnings[k]) ? CheckTone.Note : CheckTone.Warning, split.Warnings[k]));
            if (checks.Count == 0)
                checks.Add(new("none", CheckTone.Ok, "No coverage or compliance flags - demand met with a manager on the floor every block"));
        }

        return new RosterPageModel
        {
            Stores = i.Stores,
            SelectedStore = i.SelectedStore,
            PeriodStart = i.PeriodStart,
            SelectedDate = i.SelectedDate,
            PlanDates = dates,
            IsPastPeriod = i.PeriodStart < i.CurrentPeriodStart,
            IsReadOnly = i.IsReadOnly,
            IsStoreTier = i.IsStoreTier,
            OverduePeriods = i.OverduePeriods,
            HasPlan = i.PlanVersion is not null,
            HasSnapshot = fortnight is not null,
            GeneratedAt = i.RosterGeneratedAt,
            ReviewState = PlanReviewStatus.PlanReviewState(i.Review?.Status, i.Review?.SnapshotKey, i.PlanVersion),
            Gate = gate,
            RosterApproved = rosterApproved,
            PublishedAt = publishedAt == default ? null : publishedAt,
            Drift = i.Drift,
            CoverNoteDays = coverNoteDays,
            CoverNoteReason = coverNoteDays.Count > 0 ? DayStatus.CoverNoteReason(coverNoteDays) : null,
            FortnightCost = i.FortnightCost is { } c ? new RosterFortnightCost(c.Ideal, c.Actual, c.Delta) : null,
            Contracts = figures is null ? null : new RosterContracts
            {
                People = figures.People.Select(p => new RosterContractGap(p.EmployeeId, p.Name, p.Role, p.Kind == "over", p.Hours)).ToList(),
                Under = (int)figures.Under,
                Over = (int)figures.Over,
                UnderHours = figures.UnderHours,
                OverHours = figures.OverHours,
                Team = figures.Team,
            },
            ContractFlags = figures is not null ? Contracts.OtherFortnightFlags(i.FortnightFlags) : i.FortnightFlags,
            DayChips = chips,
            Day = i.DayRows is { } dr ? DayView(dr) : null,
            HeldDayClosed = heldDay?.Closed == true,
            DayChecks = checks,
            // Active team members for the manual "add shift" picker in the roster editor.
            ActiveEmployees = Js.Sorted(i.Employees.Where(e => e.Active), (a, b) => Js.LocaleCompare(a.Name, b.Name))
                .Select(e => new RosterEmployee(e.Id, e.Name, e.Role)).ToList(),
        };
    }

    private static RosterDayView DayView(RosterRows r) => new()
    {
        HasData = r.HasData,
        Rows = r.Rows.Select(RosterGridRow.From).ToList(),
        Flags = r.Flags,
        CostByKey = r.CostByKey.ToDictionary(kv => kv.Key, kv => kv.Value),
        DayTotalCost = r.DayTotalCost,
        IdealCost = r.IdealCost,
        LockRows = r.LockRows
            .Where(l => Guid.TryParse(l.EmployeeId, out _))
            .Select(l => new RosterLockRow(Guid.Parse(l.EmployeeId), l.Name, l.Role, l.Start, l.End, l.Locked)).ToList(),
    };
}
