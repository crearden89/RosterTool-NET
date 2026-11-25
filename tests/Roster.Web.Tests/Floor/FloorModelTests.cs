// Port of src/app/floor/[token]/floorModel.test.ts.

using Roster.Engine.Core;
using Roster.Web.Features.Floor;
using static Roster.Web.Features.Floor.FloorModel;

namespace Roster.Web.Tests.Floor;

public class FloorModelTests
{
    private static double Hm(string s) => double.Parse(s[..2]) * 60 + double.Parse(s[3..]);

    // One letter per quarter-hour from `start`, as the roster replays write tiles.
    private static readonly Dictionary<char, string> Letter = new()
    {
        ['C'] = "checkout", ['R'] = "registers", ['S'] = "scos", ['L'] = "load_running", ['E'] = "reset", ['A'] = "admin",
        ['P'] = "specials", ['N'] = "cleaning", ['O'] = "non_op", ['b'] = "break", ['f'] = "flex",
    };

    private sealed record TestShift(double Start, double End, List<DisplayBlock> Blocks)
    {
        public NowState At(double now, int max = 2) => NowNext(Start, End, Blocks, now, max);
    }

    private static TestShift Shift(string start, string tiles)
    {
        var s = Hm(start);
        var blocks = tiles.Select((c, i) => (c, i))
            .Where(x => Letter.ContainsKey(x.c))
            .Select(x => new DisplayBlock(s + x.i * 15, Letter[x.c]))
            .ToList();
        return new TestShift(s, s + tiles.Length * 15, blocks);
    }

    private static IReadOnlyList<NextChange> NextOf(NowState st) => st switch
    {
        NowState.Before b => b.Next,
        NowState.On o => o.Next,
        _ => [],
    };

    // A NowState written out, so two can be compared as the TypeScript's toEqual does.
    private static string Show(NowState st) => st switch
    {
        NowState.Before b => $"before {b.Starts} [{Changes(b.Next)}]",
        NowState.On o => $"on {o.Group ?? "null"} [{Changes(o.Next)}]",
        NowState.After a => $"after {a.Finished}",
        _ => "?",
    };

    private static string Changes(IEnumerable<NextChange> next) => string.Join(", ", next.Select(c => $"{c.Time} {c.What}"));

    private static NextChange Change(string time, string what) => new(Hm(time), what);

    [Fact]
    public void FloorGroup_draws_registers_and_SCOs_as_Checkout_and_leaves_flex_blank()
    {
        Assert.Equal("checkout", FloorGroup("registers"));
        Assert.Equal("checkout", FloorGroup("scos"));
        Assert.Equal("load_running", FloorGroup("load_running"));
        Assert.Null(FloorGroup("flex"));
        Assert.Null(FloorGroup("nonsense"));
    }

    [Fact]
    public void TaskRuns_joins_unbroken_quarter_hours_of_one_task_into_one_bar()
    {
        var s = Shift("07:00", "LLLLbCCRSE");
        Assert.Equal(
            [
                new TaskRun("load_running", Hm("07:00"), Hm("08:00")),
                new TaskRun("break", Hm("08:00"), Hm("08:15")),
                new TaskRun("checkout", Hm("08:15"), Hm("09:15")),
                new TaskRun("reset", Hm("09:15"), Hm("09:30")),
            ],
            TaskRuns(s.Blocks));
    }

    [Fact]
    public void TaskRuns_breaks_a_bar_at_a_gap_and_at_a_blank_quarter_hour_in_time_order()
    {
        var blocks = new[]
        {
            new DisplayBlock(Hm("10:30"), "admin"),
            new DisplayBlock(Hm("10:00"), "admin"),
            new DisplayBlock(Hm("10:15"), "flex"),
        };
        Assert.Equal(
            [new TaskRun("admin", Hm("10:00"), Hm("10:15")), new TaskRun("admin", Hm("10:30"), Hm("10:45"))],
            TaskRuns(blocks));
    }

    // 07:00-13:45: Load Running to 10:00, Checkout, a break at 10:30, Reset.
    private static readonly TestShift S = Shift("07:00", "LLLLLLLLLLLLCCbbEEEEEEEEEEE");

    [Fact]
    public void NowNext_names_the_task_of_the_current_quarter_hour_and_the_next_two_changes()
    {
        Assert.Equal(
            Show(new NowState.On("load_running", [Change("10:00", "checkout"), Change("10:30", "break")])),
            Show(S.At(Hm("09:37"))));
    }

    [Fact]
    public void NowNext_looks_two_hours_ahead_and_no_further()
    {
        Assert.Empty(NextOf(S.At(Hm("07:00"))));
        Assert.Equal([Change("10:00", "checkout")], NextOf(S.At(Hm("08:01"))));
    }

    [Fact]
    public void NowNext_ends_with_the_finish_when_the_shift_ends_inside_the_window()
    {
        Assert.Equal(Show(new NowState.On("reset", [Change("13:45", "finish")])), Show(S.At(Hm("12:00"))));
        // Exactly two hours ahead still counts.
        Assert.Equal([Change("13:45", "finish")], NextOf(S.At(Hm("11:45"))));
    }

    [Fact]
    public void NowNext_before_the_start_when_it_starts_and_the_first_task_if_it_is_inside_the_window()
    {
        var late = Shift("10:30", "CCCCPPPP");
        Assert.Equal(Show(new NowState.Before(Hm("10:30"), [Change("10:30", "checkout")])), Show(late.At(Hm("09:30"))));
        Assert.Equal(Show(new NowState.Before(Hm("10:30"), [])), Show(late.At(Hm("08:00"))));
        Assert.Equal(
            [Change("10:00", "checkout"), Change("10:30", "break")],
            NextOf(Shift("10:00", "CCbbPP").At(Hm("09:30"))));
    }

    [Fact]
    public void NowNext_after_the_end_when_it_finished()
    {
        Assert.Equal(Show(new NowState.After(Hm("13:45"))), Show(S.At(Hm("13:45"))));
    }

    [Fact]
    public void NowNext_on_shift_with_nothing_painted_no_task_and_a_change_when_one_starts()
    {
        var gap = Shift("09:00", "ffCC");
        Assert.Equal(
            Show(new NowState.On(null, [Change("09:30", "checkout"), Change("10:00", "finish")])),
            Show(gap.At(Hm("09:10"))));
    }

    [Fact]
    public void NowNext_does_not_count_registers_to_SCOs_as_a_change()
    {
        var tills = Shift("09:00", "RRSSCCEE");
        Assert.Equal(Change("10:30", "reset"), NextOf(tills.At(Hm("09:00")))[0]);
    }

    [Fact]
    public void DaySpan_runs_from_the_first_start_to_the_last_finish_in_whole_hours()
    {
        var a = Shift("07:00", "CC");
        Assert.Equal(new DayWindow(Hm("07:00"), Hm("21:00")), DaySpan([(a.Start, a.End), (Hm("12:45"), Hm("20:30"))]));
        Assert.Equal(new DayWindow(360, 420), DaySpan([(Hm("06:30"), Hm("06:45"))]));
        Assert.Null(DaySpan([]));
    }

    // ── rowScale ──

    private const double Area = CanvasH - ChromeH;
    private static double NowStack(RowScale s) => s.Pill + s.Gap + s.Next * NextLine;

    [Fact]
    public void RowScale_gives_a_quiet_day_roomy_rows_at_the_largest_text()
    {
        var s = RowScaleFor(8);
        Assert.Equal(MaxRow, s.Row);
        Assert.Equal(20, s.Name);
        Assert.Equal(28, s.Pill);
        Assert.Equal(15, s.PillFont);
        Assert.Equal(20, s.Bar);
        Assert.False(s.Scrolls);
    }

    [Fact]
    public void RowScale_shares_the_height_among_more_people_with_smaller_text_never_below_the_floor()
    {
        var sizes = new[] { 11, 15, 19, 23, 27 }.Select(n => RowScaleFor(n)).ToList();
        for (var i = 1; i < sizes.Count; i++)
        {
            Assert.True(sizes[i].Row <= sizes[i - 1].Row);
            Assert.True(sizes[i].Name <= sizes[i - 1].Name);
            Assert.True(sizes[i].Pill <= sizes[i - 1].Pill);
        }
        foreach (var s in sizes)
        {
            Assert.True(s.Row >= MinRow);
            Assert.True(s.Name >= 14);
            // Name over shift times fits the row.
            Assert.True(s.Name * 1.1 + s.Time * 1.1 <= s.Row - 4);
        }
    }

    [Fact]
    public void RowScale_keeps_the_pill_and_the_next_changes_apart_and_clear_of_the_row_lines()
    {
        for (var n = 1; n <= 40; n++)
        {
            foreach (var banner in new[] { false, true })
            {
                var s = RowScaleFor(n, banner);
                Assert.True(s.Gap >= 1.5);
                // About 2 px above and below the pair, inside the row's 1 px rule.
                Assert.True((s.Row - 1 - NowStack(s)) / 2 >= 1.9);
            }
        }
        // The space around them grows with the row.
        Assert.True(RowScaleFor(11).Row - NowStack(RowScaleFor(11)) > RowScaleFor(21).Row - NowStack(RowScaleFor(21)));
    }

    [Fact]
    public void RowScale_fits_the_largest_stores_busiest_days_on_one_screen()
    {
        foreach (var (n, banner) in new[] { (21, false), (23, false), (21, true) })
        {
            var s = RowScaleFor(n, banner);
            Assert.False(s.Scrolls);
            Assert.True(n * s.Row <= Area - (banner ? BannerH : 0) + 1);
        }
    }

    [Fact]
    public void RowScale_scrolls_the_rows_when_even_the_floor_does_not_fit()
    {
        var n = (int)Math.Floor(Area / MinRow) + 1;
        Assert.True(RowScaleFor(n).Scrolls);
        Assert.Equal(MinRow, RowScaleFor(n).Row);
    }

    [Fact]
    public void RowScale_leaves_room_for_the_out_of_date_banner()
    {
        Assert.True(RowScaleFor(21, true).Row < RowScaleFor(21).Row);
    }

    [Fact]
    public void RowScale_keeps_bars_thick_on_a_busy_day()
    {
        Assert.True(RowScaleFor(21).Bar >= 16);
        Assert.True(RowScaleFor(40).Bar >= 12);
    }

    [Fact]
    public void Header_words_write_the_clock_and_the_date_as_the_screen_shows_them()
    {
        Assert.Equal("9:30 am", Clock12(Hm("09:30")));
        Assert.Equal("12:05 pm", Clock12(Hm("12:05")));
        Assert.Equal("12:15 am", Clock12(Hm("00:15")));
        Assert.Equal("6:00 pm", Clock12(Hm("18:00")));
        Assert.Equal("Fri 20 Nov", ShortDate(new DateOnly(2026, 11, 20)));
    }

    [Fact]
    public void SquareSize_fits_every_pallet_one_line_for_a_normal_truck_two_for_a_big_one()
    {
        Assert.Equal(new PalletSquares(22, 1), SquareSize(36, 7));
        static bool Fits(double n, double t)
        {
            var (size, lines) = SquareSize(n, t);
            return (n + lines) * (size + 4) + (t - 1) * 12 <= lines * SquaresW;
        }
        foreach (var (n, t) in new[] { (36, 7), (70, 8), (85, 7), (120, 8), (200, 8) }) Assert.True(Fits(n, t));
        Assert.Equal(new PalletSquares(16, 2), SquareSize(85, 7));
        Assert.Equal(1, SquareSize(50, 8).Lines);
        Assert.Equal(new PalletSquares(16, 2), SquareSize(70, 8));
        Assert.Equal(5, SquareSize(400, 8).Size);
    }
}
