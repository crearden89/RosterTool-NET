// Port of src/lib/stage2/handoverCut.test.ts.

using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Shaper;

// The managers' handover hour at a one-till store (owner, 14/11/2026): an
// assistant beside it leaves earlier and a colleague comes in to meet the
// gap, so the managers carry the till instead of standing idle. Shaped on
// G351 11/11: the managers overlap 14:30-15:30, an assistant works
// 08:00-16:00 and the next starts 15:45.
public class HandoverCutTests
{
    private static double T(double h, double m = 0) => h * 60 + m;

    private static readonly HandoverCutLimits Lim = new()
    {
        StaffOpen = T(6),
        StaffClose = T(20, 30),
        MaxClock = new Dictionary<string, double> { ["MGR"] = 750, ["SA"] = 570, ["JSA"] = 360 },
        IsSchoolDay = true,
        MinShiftMin = 180,
    };

    private static List<PlanSlot> Day() =>
    [
        new("mgr-open", "MGR", T(7), T(15, 30)),
        new("mgr-close", "MGR", T(14, 30), T(20, 30)),
        new("sa-3", "SA", T(8), T(16)),
        new("sa-4", "SA", T(15, 45), T(18, 45)),
        new("sa-5", "SA", T(16, 45), T(20, 30)),
    ];

    private static readonly List<SlotTime> Idle =
    [
        new("mgr-close", T(14, 30)),
        new("mgr-close", T(14, 45)),
        new("mgr-open", T(15, 15)),
    ];

    private static HandoverCutScore Score(IReadOnlyList<SlotTime> blankAt, double lost = 0, double errors = 0) =>
        new(lost, errors, blankAt.Count, blankAt);

    private static (string, string, double, double) Of(PlanSlot s) => (s.Id, s.Role, s.Start, s.End);

    private static PlanSlot Find(IEnumerable<PlanSlot> slots, string id) => slots.First(s => s.Id == id);

    [Fact]
    public void Is_where_the_opening_and_closing_managers_overlap()
    {
        Assert.Equal(new ShiftTimes(T(14, 30), T(15, 30)), HandoverCut.HandoverOf(Day()));
        Assert.Equal(
            [T(14, 30), T(14, 45), T(15, 15)],
            HandoverCut.IdleInHandover(Day(), [.. Idle, new SlotTime("sa-3", T(15, 15))]));
    }

    [Fact]
    public void Tries_cuts_that_never_touch_the_managers_or_shorten_a_shift_below_three_hours()
    {
        var cands = HandoverCut.HandoverCandidates(Day(), Lim, 3);
        Assert.NotEmpty(cands);
        var dayLength = Day().Sum(s => s.End - s.Start);
        foreach (var c in cands)
        {
            Assert.Equal(Day().Where(s => s.Role == "MGR").Select(Of), c.Where(s => s.Role == "MGR").Select(Of));
            foreach (var s in c) Assert.True(s.End - s.Start >= 180);
            // Shift time only ever goes.
            Assert.True(c.Sum(s => s.End - s.Start) < dayLength);
        }
        // The cut found by hand: the assistant leaves at 15:00, the next comes in at 15:30.
        Assert.Contains(cands, c => Find(c, "sa-3").End == T(15) && Find(c, "sa-4").Start == T(15, 30));
    }

    // A stand-in painter: the managers carry the till in the hour once the
    // assistant leaves at 15:00 and the next comes in at 15:30; any other
    // cut loses work or leaves someone alone.
    private static ScoredPaint<string, HandoverCutScore> HandPaint(List<PlanSlot> slots)
    {
        var sa3 = Find(slots, "sa-3");
        var sa4 = Find(slots, "sa-4");
        if (sa3.End == T(16) && sa4.Start == T(15, 45)) return new("as is", Score(Idle));
        if (sa3.End == T(15) && sa4.Start == T(15, 30)) return new("cut", Score([]));
        return new("worse", Score([], lost: 1));
    }

    private static ScoredPaint<string, HandoverCutScore> AsIs() => new("as is", Score(Idle));

    [Fact]
    public void Keeps_the_cut_the_painter_says_is_better_on_every_count()
    {
        var out_ = HandoverCut.CutBesideHandover(Day(), Lim, HandPaint, AsIs());
        Assert.Equal("cut", out_.Painted);
        Assert.Equal(
            [
                new HandoverCutChange("sa-3", "SA", new ShiftTimes(T(8), T(16)), new ShiftTimes(T(8), T(15))),
                new HandoverCutChange("sa-4", "SA", new ShiftTimes(T(15, 45), T(18, 45)), new ShiftTimes(T(15, 30), T(18, 45))),
            ],
            out_.Changes);
        Assert.True(HandoverCut.SlotHours(out_.Slots).Worked < HandoverCut.SlotHours(Day()).Worked);
    }

    [Fact]
    public void Is_asked_for_cheaply_before_a_day_is_scored()
    {
        Assert.True(HandoverCut.WantsHandoverCut(Day(), Idle, _ => 1));
        Assert.False(HandoverCut.WantsHandoverCut(Day(), Idle, _ => 2));
        Assert.False(HandoverCut.WantsHandoverCut(Day(), [new SlotTime("sa-3", T(9))], _ => 1));
    }

    [Fact]
    public void Is_only_for_a_handover_hour_with_one_till_wanted()
    {
        var oneTill = HandoverCut.CutBesideHandover(Day(), Lim with { TillsAt = _ => 1 }, HandPaint, AsIs());
        Assert.Equal("cut", oneTill.Painted);
        var twoTills = HandoverCut.CutBesideHandover(Day(), Lim with { TillsAt = m => m == T(15) ? 2 : 1 }, HandPaint, AsIs());
        Assert.Empty(twoTills.Changes);
    }

    [Fact]
    public void Is_refused_when_it_would_make_the_day_lose_work_with_a_manager_owed_a_store_assistants_breaks()
    {
        // The roster may put an RSM (an hour of breaks on a long shift, not a
        // manager's flat half-hour) in a manager slot; with the hour cut to no
        // slack, that cost the closing manager's End of Day on a test store.
        var holds = HandoverCut.CutBesideHandover(Day(), Lim, HandPaint, AsIs(), _ => 0);
        Assert.Equal("cut", holds.Painted);
        var breaks = HandoverCut.CutBesideHandover(Day(), Lim, HandPaint, AsIs(), slots => Find(slots, "sa-3").End == T(16) ? 0 : 2);
        Assert.Empty(breaks.Changes);
        // No more fragile than the day already was: kept.
        var asBefore = HandoverCut.CutBesideHandover(Day(), Lim, HandPaint, AsIs(), _ => 2);
        Assert.Equal("cut", asBefore.Painted);
    }

    [Fact]
    public void Changes_nothing_when_no_manager_is_idle_in_the_handover_or_nothing_is_better()
    {
        var painted = 0;
        ScoredPaint<string, HandoverCutScore> Paint(List<PlanSlot> _)
        {
            painted++;
            return new("x", Score([], errors: 1));
        }
        var quiet = HandoverCut.CutBesideHandover(Day(), Lim, Paint, new ScoredPaint<string, HandoverCutScore>("as is", Score([new SlotTime("sa-3", T(9))])));
        Assert.Empty(quiet.Changes);
        Assert.Equal(0, painted);
        var worse = HandoverCut.CutBesideHandover(Day(), Lim, Paint, AsIs());
        Assert.Empty(worse.Changes);
        Assert.Equal(Day().Select(Of), worse.Slots.Select(Of));
    }

    [Fact]
    public void Never_moves_a_fixed_shift_the_crews_break_cover_or_a_junior_outside_their_hours()
    {
        List<PlanSlot> slots =
        [
            .. Day().Where(s => s.Id != "sa-3" && s.Id != "sa-4"),
            new("fixed-sa-1", "SA", T(8), T(16)),
            new("sa-cover-1", "SA", T(13), T(16)),
            new("jsa-1", "JSA", T(16), T(20)),
        ];
        var cands = HandoverCut.HandoverCandidates(slots, Lim, 3);
        foreach (var c in cands)
        {
            Assert.Equal(Of(Find(slots, "fixed-sa-1")), Of(Find(c, "fixed-sa-1")));
            Assert.Equal(Of(Find(slots, "sa-cover-1")), Of(Find(c, "sa-cover-1")));
            Assert.True(Find(c, "jsa-1").Start >= T(16));
        }
    }
}
