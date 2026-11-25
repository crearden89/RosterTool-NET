// New: the Core helpers with no TypeScript test of their own (parseInt and
// parseFloat semantics, the JSON shapes, the store-day resolution).

using System.Text.Json;
using Roster.Engine.Core;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Core;

public class ParseTests
{
    [Fact]
    public void ParseInt_follows_JavaScript()
    {
        Assert.Equal(42, Js.ParseInt("42px"));
        Assert.Equal(-7, Js.ParseInt("  -7.9"));
        Assert.Equal(255, Js.ParseInt("0xff"));
        Assert.Equal(255, Js.ParseInt("ff", 16));
        Assert.Equal(5, Js.ParseInt("101", 2));
        Assert.True(double.IsNaN(Js.ParseInt("px42")));
        Assert.True(double.IsNaN(Js.ParseInt("")));
    }

    [Fact]
    public void ParseFloat_follows_JavaScript()
    {
        Assert.Equal(3.5, Js.ParseFloat("3.5h"));
        Assert.Equal(0.25, Js.ParseFloat(" .25"));
        Assert.Equal(1e3, Js.ParseFloat("1e3x"));
        Assert.Equal(1, Js.ParseFloat("1e"));
        Assert.Equal(double.NegativeInfinity, Js.ParseFloat("-Infinityx"));
        Assert.True(double.IsNaN(Js.ParseFloat("abc")));
        Assert.True(double.IsNaN(Js.ParseFloat(".")));
    }
}

public class JsonSupportTests
{
    private sealed record Holder(JsMap<string, double> Groups, JsMap<double, string> ByTime, DateOnly Date);

    [Fact]
    public void A_string_keyed_map_is_an_object_in_insertion_order_and_others_are_pairs()
    {
        var h = new Holder(new JsMap<string, double>().Set("reset", 2).Set("admin", 1), new JsMap<double, string>().Set(615, "b").Set(600, "a"), ParseIso("2026-11-09"));
        var json = JsonSerializer.Serialize(h, RosterJson.Options);
        Assert.Equal("""{"groups":{"reset":2,"admin":1},"byTime":[[615,"b"],[600,"a"]],"date":"2026-11-09"}""", json);
        var back = JsonSerializer.Deserialize<Holder>(json, RosterJson.Options)!;
        Assert.Equal(["reset", "admin"], back.Groups.Keys().ToArray());
        Assert.Equal(new double[] { 615, 600 }, back.ByTime.Keys().ToArray());
        Assert.Equal(h.Date, back.Date);
    }
}

public class StoreDayTests
{
    private static readonly IReadOnlyList<TradingDayRow> Week =
        Enumerable.Range(0, 7).Select(d => new TradingDayRow(d, "08:30:00", d == 6 ? "17:00:00" : "20:00:00", false)).ToList();

    private static readonly Dictionary<string, double> NoOverrides = new();

    [Fact]
    public void The_weekly_pattern_gives_trading_hours_and_a_30_minute_close_down()
    {
        var day = StoreDays.ResolveStoreDay(Week, [], NoOverrides, ParseIso("2026-11-09")); // Monday
        Assert.Equal(new StoreDay(false, 510, 1200, 360, 1230), day);
    }

    [Fact]
    public void Special_hours_win_and_the_latest_starting_range_applies()
    {
        SpecialHoursRow[] specials =
        [
            new(ParseIso("2026-11-01"), ParseIso("2026-11-30"), false, "09:00:00", "18:00:00"),
            new(ParseIso("2026-11-08"), ParseIso("2026-11-10"), false, "10:00:00", "16:00:00"),
        ];
        var day = StoreDays.ResolveStoreDay(Week, specials, NoOverrides, ParseIso("2026-11-09"));
        Assert.Equal(new StoreDay(false, 600, 960, 360, 990), day);
    }

    [Fact]
    public void A_closed_day_or_an_inverted_one_is_closed()
    {
        Assert.True(StoreDays.ResolveStoreDay(Week, [new(ParseIso("2026-12-25"), ParseIso("2026-12-25"), true, null, null)], NoOverrides, ParseIso("2026-12-25")).Closed);
        Assert.True(StoreDays.ResolveStoreDay([new TradingDayRow(0, "20:00", "08:00", false)], [], NoOverrides, ParseIso("2026-11-09")).Closed);
        Assert.True(StoreDays.ResolveStoreDay([], [], NoOverrides, ParseIso("2026-11-09")).Closed);
    }

    [Fact]
    public void The_close_down_is_a_setting()
    {
        var day = StoreDays.ResolveStoreDay(Week, [], new Dictionary<string, double> { ["closeDownMin"] = 45 }, ParseIso("2026-11-09"));
        Assert.Equal(1245, day.StaffClose);
    }

    [Fact]
    public void A_late_opening_starts_the_staffed_day_at_six()
    {
        var day = StoreDays.ResolveStoreDay([new TradingDayRow(0, "05:30", "12:00", false)], [], NoOverrides, ParseIso("2026-11-09"));
        Assert.Equal(330, day.EarliestStaffStart);
    }
}

public class CloseRulesTests
{
    [Fact]
    public void FollowClose_opens_a_late_window_two_hours_before_close()
    {
        Assert.Equal(900, CloseRules.FollowClose(17 * 60, 17 * 60));
        Assert.Equal(17 * 60, CloseRules.FollowClose(17 * 60, 21 * 60));
    }

    [Fact]
    public void Evening_cleaning_runs_on_into_the_close_down_as_make_up_only()
    {
        var w = new WindowRule("cleaning", 18 * 60, 21 * 60);
        var out_ = CloseRules.EveningCleaningToCloseDown(w, 21 * 60, 21 * 60 + 30);
        Assert.Equal(2, out_.Count);
        Assert.Equal(new WindowRule { Group = "cleaning", Start = 21 * 60, End = 21 * 60 + 30, MakeUpOnly = true, CloseDownFrom = 21 * 60 }, out_[1]);
        // The quoted morning hour, and a window already reaching the end of the day, are left alone.
        Assert.Single(CloseRules.EveningCleaningToCloseDown(new WindowRule("cleaning", 9 * 60, 10 * 60, 1), 21 * 60, 21 * 60 + 30));
        Assert.Single(CloseRules.EveningCleaningToCloseDown(new WindowRule("cleaning", 18 * 60, 21 * 60), 20 * 60, 20 * 60 + 30));
    }
}
