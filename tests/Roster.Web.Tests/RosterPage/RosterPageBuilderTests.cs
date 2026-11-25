// New: page.tsx's decisions (RosterPageBuilder) on an invented fortnight.

using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Review;
using Roster.Web.Features.RosterPage;

namespace Roster.Web.Tests.RosterPage;

public class RosterPageBuilderTests
{
    private static readonly Guid Store = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly DateOnly Start = new(2026, 11, 23);
    private static readonly List<DateOnly> Dates = Period.PeriodDatesFrom(Start);
    private const string Alex = "10000000-0000-4000-8000-000000000001";

    private static Shift S(string id, string name, bool unfilled = false) =>
        new() { EmployeeId = id, Name = name, Role = Roles.SA, Start = 360, End = 840, Unfilled = unfilled, Blocks = [new ShiftBlock(360, "checkout")] };

    private static RosterComputation Day(params Shift[] shifts) => new() { HasData = true, Shifts = [.. shifts] };

    private static RosterPageInputs Inputs(Action<JsMap<DateOnly, RosterComputation>>? days = null)
    {
        var byDate = new JsMap<DateOnly, RosterComputation>();
        foreach (var d in Dates) byDate.Set(d, Day(S(Alex, "Alex Hart")));
        days?.Invoke(byDate);
        return new RosterPageInputs
        {
            Stores = [new StoreOption(Store, "G101", "Riverside")],
            SelectedStore = Store,
            PeriodStart = Start,
            PlanDates = Dates,
            SelectedDate = Start,
            CurrentPeriodStart = Start,
            PlanVersion = 3,
            RosterByDate = byDate,
            RosterGeneratedAt = DateTimeOffset.Parse("2026-11-20T09:00:00+00:00"),
            Review = new RosterReviewRow("approved", ReviewFormat.PlanKey(3), DateTimeOffset.Parse("2026-11-19T09:00:00+00:00")),
            Employees =
            [
                new(Guid.NewGuid(), "Zoe Lim", "SA", true),
                new(Guid.NewGuid(), "Ben Ode", "SA", false),
                new(Guid.Parse(Alex), "Alex Hart", "SA", true),
            ],
        };
    }

    [Fact]
    public void Chips_GreenForOkDays_RedForAnOpenShift_GreyForClosed_AndNoRosterWithoutData()
    {
        var m = RosterPageBuilder.Build(Inputs(byDate =>
        {
            byDate.Set(Dates[1], Day(S(Alex, "Alex Hart"), S("open-1", "Open shift", unfilled: true)));
            byDate.Set(Dates[2], new RosterComputation { Closed = true, HasData = true });
            byDate.Set(Dates[3], new RosterComputation { HasData = false });
        }));
        var c = m.DayChips;
        Assert.Equal((DayTone.Ok, "1", "1 shift", "OK, 1 shift"), (c[0].Tone, c[0].Note, c[0].NoteWide, c[0].Status));
        Assert.Equal((DayTone.Error, "2", "has an error, 2 shifts"), (c[1].Tone, c[1].Note, c[1].Status));
        Assert.Equal((DayTone.Neutral, "Closed", null, "closed"), (c[2].Tone, c[2].Note, c[2].NoteWide, c[2].Status));
        Assert.Equal((DayTone.Error, "No roster", null), (c[3].Tone, c[3].Note, c[3].NoteWide));
        Assert.Equal($"plan/roster?store={Store}&date=2026-11-23", c[0].Href);
        Assert.Equal([Dates[1]], m.CoverNoteDays);
        Assert.Equal("1 day has an error (open shifts or cover gaps): 24/11", m.CoverNoteReason);
    }

    [Fact]
    public void Approved_WhenEveryDayIsApprovedOrClosed_PublishedAtTheLatestStamp()
    {
        var inputs = Inputs(byDate => byDate.Set(Dates[5], new RosterComputation { Closed = true, HasData = true })) with
        {
            StatusRows = Dates.Where(d => d != Dates[5])
                .Select((d, k) => new RosterDayStatusRow(d, "approved", DateTimeOffset.Parse("2026-11-21T08:00:00+00:00").AddMinutes(k)))
                .ToList(),
        };
        var m = RosterPageBuilder.Build(inputs);
        Assert.True(m.RosterApproved);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T08:12:00+00:00"), m.PublishedAt);
        Assert.False(m.Gate.Published); // the gate counts approved days only
        var notAll = RosterPageBuilder.Build(inputs with { StatusRows = inputs.StatusRows.Skip(1).ToList() });
        Assert.False(notAll.RosterApproved);
    }

    [Fact]
    public void Gate_UnlocksOnTheApprovedPlan_AndTheEditorFollowsIt()
    {
        var m = RosterPageBuilder.Build(Inputs());
        Assert.Equal(PlanReviewState.Approved, m.ReviewState);
        Assert.True(m.Gate.Unlocked);
        Assert.True(m.CanGenerate && m.CanPublish && !m.EditorReadOnly && !m.Locked);
        var changed = RosterPageBuilder.Build(Inputs() with { PlanVersion = 4 });
        Assert.Equal(PlanReviewState.NotSubmitted, changed.ReviewState);
        Assert.True(changed.Locked);
        Assert.True(changed.EditorReadOnly);
        var past = RosterPageBuilder.Build(Inputs() with { CurrentPeriodStart = Start.AddDays(14) });
        Assert.True(past.IsPastPeriod && !past.CanGenerate && past.EditorReadOnly);
    }

    [Fact]
    public void Checks_ErrorsThenWarnings_TheOpenShiftsWhyAsANote()
    {
        var rows = new RosterRows(true, [], ["Fri 09:00 has nobody on it", "Alex Hart works 6 days in a row", "Why: nobody else is free"],
            new JsMap<string, double>(), 0, 0, []);
        var m = RosterPageBuilder.Build(Inputs() with { DayRows = rows });
        Assert.Equal(
            [("e0", CheckTone.Error), ("e1", CheckTone.Note), ("w0", CheckTone.Warning)],
            m.DayChecks.Select(c => (c.Key, c.Tone)));
        Assert.True(m.DayHasError);
        var clean = RosterPageBuilder.Build(Inputs() with { DayRows = rows with { Flags = [] } });
        Assert.Equal("No coverage or compliance flags - demand met with a manager on the floor every block", clean.DayChecks.Single().Message);
    }

    [Fact]
    public void Contracts_FromTheOutcomes_ReplaceTheirFlags_AndEmployeesAreActiveByName()
    {
        var outcomes = new List<ContractOutcome>
        {
            new() { EmployeeId = Alex, Name = "Alex Hart", Role = Roles.SA, ContractPeriodHours = 76, PaidHours = 70, ShortfallHours = 6 },
        };
        var flags = new[] { "Alex Hart is 6.00h under contract this fortnight", "Zoe Lim works 7 days in a row" };
        var m = RosterPageBuilder.Build(Inputs() with { Outcomes = outcomes, FortnightFlags = flags });
        Assert.Equal((1, 6.0), (m.Contracts!.Under, m.Contracts.UnderHours));
        Assert.Equal(["Zoe Lim works 7 days in a row"], m.ContractFlags);
        var old = RosterPageBuilder.Build(Inputs() with { FortnightFlags = flags });
        Assert.Null(old.Contracts);
        Assert.Equal(flags, old.ContractFlags);
        Assert.Equal(["Alex Hart", "Zoe Lim"], m.ActiveEmployees.Select(e => e.Name));
    }
}
