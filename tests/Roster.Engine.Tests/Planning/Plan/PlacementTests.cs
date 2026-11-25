// Port of src/lib/plan/placement.test.ts.

using Roster.Engine.Planning.Plan;

namespace Roster.Engine.Tests.Planning.Plan;

public class PlacementTests
{
    private const double H = 60;
    private static readonly DayBounds Bounds = new(8 * H, 20 * H, 6 * H, 21 * H);

    private static PlacementRule Rule(Func<PlacementRule, PlacementRule>? over = null)
    {
        var r = new PlacementRule
        {
            Group = "reset",
            Strategy = "backfill",
            AnchorKind = "none",
            AnchorTime = null,
            AnchorOffsetMin = 0,
            PrePeakPct = null,
            PostPeakPct = null,
            AvoidStrength = null,
            NightTraderStrategy = null,
        };
        return over is null ? r : over(r);
    }

    private static List<string> Sorted(IEnumerable<string> xs) => xs.Order(StringComparer.Ordinal).ToList();

    // ── the shipped default ──

    [Fact]
    public void Covers_every_labour_group_exactly_once() =>
        Assert.Equal(Sorted(Placement.PlacementGroups), Sorted(Placement.DefaultRules.Select(r => r.Group)));

    [Fact]
    public void Is_inert_until_the_placement_pass_is_wired_in() => Assert.False(Placement.DefaultSettings.Enabled);

    [Fact]
    public void Leaves_Reset_and_Load_Running_to_the_models_that_really_place_them()
    {
        // Both were on this page and neither was ever read for a store with a
        // catalogue. Stage 1 places Reset TASK-FIRST, so the group directive only
        // reached the benchmark the named tasks did not consume - and the shipped
        // shares total 100%, leaving that at zero. Load Running's window is set
        // per store-day by the start rule from load size and morning customers, so
        // an anchor here could only restate it, badly. They now live with the
        // things that define them, on Rules -> Labour Group Rules.
        Assert.DoesNotContain("reset", Placement.PlacementGroups);
        Assert.DoesNotContain("load_running", Placement.PlacementGroups);
        Assert.DoesNotContain(Placement.DefaultRules, r => r.Group == "reset");
        Assert.DoesNotContain(Placement.DefaultRules, r => r.Group == "load_running");
        // ...and the page still says where they went.
        Assert.Equal(["load_running", "reset"], Sorted(Placement.Elsewhere.Select(e => e.Group)));

        // Cleaning is still this page's to decide: as late as the day allows.
        var cleaning = Placement.DefaultRules.First(r => r.Group == "cleaning");
        Assert.Equal("backfill", cleaning.Strategy);
        // Two hours before THIS store's close would be 18:00; cleaning anchors at
        // close itself, and follows the store rather than a fixed clock time.
        Assert.Equal(Bounds.TradingClose, Placement.ResolveAnchor(cleaning, Bounds));
    }

    // ── anchors ──

    [Fact]
    public void An_open_close_offset_travels_with_the_stores_hours()
    {
        var r = Rule(x => x with { AnchorKind = "close", AnchorOffsetMin = -2 * H });
        Assert.Equal(18 * H, Placement.ResolveAnchor(r, Bounds));
        // A store closing at 21:00 anchors an hour later - the point of the offset.
        Assert.Equal(19 * H, Placement.ResolveAnchor(r, Bounds with { TradingClose = 21 * H }));
    }

    [Fact]
    public void A_fixed_clock_anchor_ignores_the_stores_hours()
    {
        var r = Rule(x => x with { AnchorKind = "clock", AnchorTime = 10 * H });
        Assert.Equal(10 * H, Placement.ResolveAnchor(r, Bounds));
        Assert.Equal(10 * H, Placement.ResolveAnchor(r, Bounds with { TradingClose = 21 * H }));
    }

    [Fact]
    public void Falls_back_to_the_end_of_the_day_going_backwards_the_start_going_forwards()
    {
        Assert.Equal(Bounds.StaffClose, Placement.ResolveAnchor(Rule(x => x with { Strategy = "backfill" }), Bounds));
        Assert.Equal(Bounds.StaffOpen, Placement.ResolveAnchor(Rule(x => x with { Strategy = "forward_fill" }), Bounds));
    }

    // ── night trader classification ──

    private static List<CurveShare> Curve(double lateShare) => [new(12 * H, 1 - lateShare), new(18 * H, lateShare)];

    [Fact]
    public void Classifies_on_the_share_of_trade_after_the_configured_time()
    {
        Assert.True(Placement.IsNightTrader(Curve(0.4), Placement.DefaultSettings));
        Assert.False(Placement.IsNightTrader(Curve(0.2), Placement.DefaultSettings));
    }

    [Fact]
    public void Treats_a_store_with_no_curve_as_a_normal_trader() =>
        Assert.False(Placement.IsNightTrader([], Placement.DefaultSettings));

    [Fact]
    public void Swaps_in_the_override_strategy_only_for_night_traders()
    {
        var r = Rule(x => x with { Strategy = "peak_split", NightTraderStrategy = "backfill" });
        Assert.Equal("peak_split", Placement.EffectiveStrategy(r, false));
        Assert.Equal("backfill", Placement.EffectiveStrategy(r, true));
        // No override configured - the normal strategy stands either way.
        Assert.Equal("spread", Placement.EffectiveStrategy(Rule(x => x with { Strategy = "spread" }), true));
    }

    // ── normalising an untrusted configuration ──

    [Fact]
    public void Appends_any_group_the_config_forgot_so_none_is_left_unplaced()
    {
        var out_ = Placement.NormaliseRules([Rule(x => x with { Group = "cleaning", Strategy = "spread" })]);
        Assert.Equal(Sorted(Placement.PlacementGroups), Sorted(out_.Select(r => r.Group)));
        Assert.Equal("cleaning", out_[0].Group); // configured order preserved first
    }

    [Fact]
    public void Drops_unknown_groups_unknown_strategies_and_duplicates()
    {
        var out_ = Placement.NormaliseRules(
        [
            Rule(x => x with { Group = "nonsense" }),
            Rule(x => x with { Group = "specials", Strategy = "made_up" }),
            Rule(x => x with { Group = "cleaning" }),
            Rule(x => x with { Group = "cleaning", Strategy = "spread" }),
        ]);
        Assert.Single(out_.Where(r => r.Group == "cleaning"));
        Assert.Equal(Placement.PlacementGroups.Count, out_.Count);
    }

    [Fact]
    public void Returns_the_default_when_given_nothing()
    {
        Assert.Equal(Placement.DefaultRules, Placement.NormaliseRules([]));
        Assert.Equal(Placement.DefaultRules, Placement.NormaliseRules(null));
    }

    // ── configuration warnings ──

    [Fact]
    public void Catches_peak_shares_that_exceed_the_groups_hours()
    {
        var w = Placement.ValidatePlacement([Rule(x => x with { Strategy = "peak_split", PrePeakPct = 70, PostPeakPct = 50 })], Placement.DefaultSettings);
        Assert.Contains(w, m => m.Contains("120%"));
    }

    [Fact]
    public void Points_out_settings_that_quietly_do_nothing()
    {
        var noop = Placement.ValidatePlacement([Rule(x => x with { Strategy = "avoid_peak", AvoidStrength = 0 })], Placement.DefaultSettings);
        Assert.Contains(noop, m => m.Contains("even spread"));

        var allSpread = Placement.ValidatePlacement(
            Placement.PlacementGroups.Select(g => Rule(x => x with { Group = g, Strategy = "spread" })).ToList(),
            Placement.DefaultSettings);
        Assert.Contains(allSpread, m => m.Contains("identical to how plans are generated today"));
    }

    [Fact]
    public void Says_so_when_checkout_is_ordered_as_though_its_position_mattered()
    {
        var w = Placement.ValidatePlacement(
            [Rule(x => x with { Group = "reset" }), Rule(x => x with { Group = "checkout", Strategy = "follow_demand" })],
            Placement.DefaultSettings);
        Assert.Contains(w, m => m.Contains("Checkout is not first"));
    }

    [Fact]
    public void Passes_the_shipped_default_without_complaint() =>
        Assert.Empty(Placement.ValidatePlacement(Placement.DefaultRules, Placement.DefaultSettings));
}
