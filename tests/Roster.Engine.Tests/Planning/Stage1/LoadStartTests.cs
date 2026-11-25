// Port of src/lib/stage1/loadStart.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Stage1;

public class LoadStartTests
{
    private const double H = 60;
    private static readonly LoadStartRules R = LoadStart.DefaultRules;

    // ── recommendLoadWindow - the dashboard model, exactly ──

    [Fact]
    public void Sizes_the_start_from_load_hours()
    {
        // The reference dashboard's boundaries are asymmetric on purpose:
        // hours<=12 is inclusive, hours<15 is exclusive.
        Assert.Equal(7 * H, LoadStart.RecommendLoadWindow(12, 50, R).Start);
        Assert.Equal(6 * H + 30, LoadStart.RecommendLoadWindow(12.01, 50, R).Start);
        Assert.Equal(6 * H + 30, LoadStart.RecommendLoadWindow(14.99, 50, R).Start);
        Assert.Equal(6 * H, LoadStart.RecommendLoadWindow(15, 50, R).Start);
        Assert.Equal(6 * H, LoadStart.RecommendLoadWindow(33, 50, R).Start);
    }

    [Fact]
    public void Sets_the_finish_from_customers()
    {
        Assert.Equal(10 * H, LoadStart.RecommendLoadWindow(10, 90, R).Finish);
        Assert.Equal(9 * H + 30, LoadStart.RecommendLoadWindow(10, 91, R).Finish);
        Assert.Equal(9 * H + 30, LoadStart.RecommendLoadWindow(10, 120, R).Finish);
        Assert.Equal(9 * H, LoadStart.RecommendLoadWindow(10, 121, R).Finish);
    }

    [Fact]
    public void Guarantees_busy_stores_3_hours_before_the_0900_finish()
    {
        // Small load would say 07:00, but 09:00 - 3h = 06:00 wins.
        var rec = LoadStart.RecommendLoadWindow(10, 200, R);
        Assert.Equal(180, rec.MinWindow);
        Assert.Equal(6 * H, rec.Start);
        Assert.Equal(9 * H, rec.Finish);
    }

    [Fact]
    public void Guarantees_everyone_else_2_5_hours()
    {
        // Small load + mid customers: 09:30 - 2.5h = 07:00 - the ceiling holds.
        var rec = LoadStart.RecommendLoadWindow(10, 100, R);
        Assert.Equal(150, rec.MinWindow);
        Assert.Equal(7 * H, rec.Start);
    }

    [Fact]
    public void Never_starts_later_than_0700() => Assert.Equal(7 * H, LoadStart.RecommendLoadWindow(5.56, 37, R).Start);

    [Fact]
    public void Falls_to_the_quiet_band_when_a_store_has_no_POS_data_yet()
    {
        var rec = LoadStart.RecommendLoadWindow(16, null, R);
        Assert.Equal(10 * H, rec.Finish); // relaxed deadline
        Assert.Equal(6 * H, rec.Start); // load-size half still applies
        Assert.Null(rec.Customers);
    }

    [Fact]
    public void Reproduces_the_reference_dashboards_split_on_its_own_maths()
    {
        // Spot checks lifted from the dashboard's data: a big Saturday load and a
        // quiet Monday.
        var sat = LoadStart.RecommendLoadWindow(12.7, 75, R);
        Assert.Equal(6 * H + 30, sat.Start);
        Assert.Equal(10 * H, sat.Finish);
        var mon = LoadStart.RecommendLoadWindow(10.14, 42, R);
        Assert.Equal(7 * H, mon.Start);
    }

    // ── loadStartRulesFrom ──

    [Fact]
    public void LoadStartRulesFrom_defaults_when_no_keys_are_stored_and_overrides_when_they_are()
    {
        Assert.Equal(R, LoadStart.LoadStartRulesFrom(new Dictionary<string, double>()));
        var tuned = LoadStart.LoadStartRulesFrom(new Dictionary<string, double>
        {
            [LoadStart.Keys.Enabled] = 0,
            [LoadStart.Keys.QuietCustomersMax] = 80,
        });
        Assert.False(tuned.Enabled);
        Assert.Equal(80, tuned.QuietCustomersMax);
        Assert.Equal(15, tuned.LargeLoadMinHours);
    }

    // ── morningCustomers ──
    // Two Mondays and a Tuesday; open 08:30, 60-minute sample = 08:30-09:30.
    private static readonly List<PosCustomerRow> Rows =
    [
        // Mon 2026-08-03: 30 customers in the sample window, 10 outside it.
        new(new DateOnly(2026, 8, 3), 8 * H + 30, 12),
        new(new DateOnly(2026, 8, 3), 9 * H, 18),
        new(new DateOnly(2026, 8, 3), 11 * H, 10),
        // Mon 2026-08-10: 50 in window.
        new(new DateOnly(2026, 8, 10), 8 * H + 45, 50),
        // Tue: must not contaminate Monday.
        new(new DateOnly(2026, 8, 4), 8 * H + 30, 500),
    ];

    [Fact]
    public void MorningCustomers_averages_the_sample_window_across_that_weekdays_usable_dates() =>
        Assert.Equal(40, LoadStart.MorningCustomers(Rows, 0, 8 * H + 30, 60, new HashSet<DateOnly>())); // (30+50)/2

    [Fact]
    public void MorningCustomers_excludes_public_holidays_from_the_sample() =>
        Assert.Equal(30, LoadStart.MorningCustomers(Rows, 0, 8 * H + 30, 60, new HashSet<DateOnly> { new(2026, 8, 10) }));

    [Fact]
    public void MorningCustomers_excludes_zero_trade_dates_and_returns_null_with_nothing_usable()
    {
        List<PosCustomerRow> closed = [new(new DateOnly(2026, 8, 3), 9 * H, 0)];
        Assert.Null(LoadStart.MorningCustomers(closed, 0, 8 * H + 30, 60, new HashSet<DateOnly>()));
        Assert.Null(LoadStart.MorningCustomers([], 0, 8 * H + 30, 60, new HashSet<DateOnly>()));
    }
}

/// <summary>
/// Port of the pure half of src/lib/stage1/eveningCleaning.test.ts (Core's
/// CloseRules). Evening Cleaning runs to the end of the close-down on late
/// closes (owner, 14/11/2026). The national evening window ended at 21:00, so on
/// a 21:00 close the second person kept in for the close-down (decision 2) had
/// nothing they were allowed to do. The quoted morning hour (decisions 10 and
/// 12) is not touched. (The file's scenario half - a G999 Thursday through
/// computeStage1 and computeDaySlots - runs on the data layer's loaders.)
/// </summary>
public class EveningCleaningToCloseDownTests
{
    private static double T(double h, double m = 0) => h * 60 + m;

    private static readonly WindowRule Evening = new("cleaning", T(18), T(21));

    private static WindowRule CloseDown(double from, double to) =>
        new() { Group = "cleaning", Start = from, End = to, MakeUpOnly = true, CloseDownFrom = from };

    [Fact]
    public void Runs_evening_Cleaning_on_to_the_end_of_the_close_down_after_a_2100_close_make_up_only() =>
        Assert.Equal([Evening, CloseDown(T(21), T(21, 30))], CloseRules.EveningCleaningToCloseDown(Evening, T(21), T(21, 30)));

    [Fact]
    public void Runs_it_on_past_a_later_close_too() =>
        Assert.Equal([Evening, CloseDown(T(21), T(22, 30))], CloseRules.EveningCleaningToCloseDown(Evening, T(22), T(22, 30)));

    [Fact]
    public void Adds_nothing_where_the_window_already_reaches_the_end_of_the_staffed_day()
    {
        Assert.Equal([Evening], CloseRules.EveningCleaningToCloseDown(Evening, T(20), T(20, 30)));
        var afternoon = Evening with { Start = T(15) };
        Assert.Equal([afternoon], CloseRules.EveningCleaningToCloseDown(afternoon, T(17), T(17, 30)));
    }

    [Fact]
    public void Never_touches_the_quoted_morning_hour_or_another_group()
    {
        var morning = new WindowRule("cleaning", T(9), T(10), 1);
        Assert.Equal([morning], CloseRules.EveningCleaningToCloseDown(morning, T(21), T(21, 30)));
        var specials = new WindowRule("specials", T(18), T(21));
        Assert.Equal([specials], CloseRules.EveningCleaningToCloseDown(specials, T(21), T(21, 30)));
    }

    [Fact]
    public void Leaves_a_Cleaning_window_that_shuts_well_before_the_close()
    {
        var midday = new WindowRule("cleaning", T(12), T(14));
        Assert.Equal([midday], CloseRules.EveningCleaningToCloseDown(midday, T(21), T(21, 30)));
    }
}
