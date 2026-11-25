// Port of src/lib/till/engine.test.ts.

using Roster.Engine.Till;
using static Roster.Engine.Tests.Till.TestNumbers;

namespace Roster.Engine.Tests.Till;

public class EngineTests
{
    // Reference store (matches the seeded G001): 6 MCO registers, 4 SCO lanes,
    // efficiency 0.45, first step 1.2, SCO share 0.66 → laneCapacity = 0.45×4 = 1.8.
    private static readonly CheckoutAttrs Store = new()
    {
        HasSco = true,
        IsNewScoModel = true,
        McoRegisters = 6,
        ScoLanes = 4,
        ScoEfficiency = 0.45,
        FirstStep = 1.2,
        ScoShare = 0.66,
    };

    // G248's live settings - the store the tier explainer was built for.
    private static readonly CheckoutAttrs G248 = new()
    {
        HasSco = true,
        IsNewScoModel = true,
        McoRegisters = 4,
        ScoLanes = 6,
        ScoEfficiency = 0.39,
        FirstStep = 1.2,
        ScoShare = 0.59,
    };

    [Fact]
    public void Bank_is_closed_below_the_first_step_no_credit()
    {
        var r = TillEngine.ComputeBlock(1.19, Store);
        Assert.Equal(0, r.ScoOn);
        Assert.Equal(0, r.Credit);
    }

    [Fact]
    public void Bank_opens_at_exactly_the_first_step()
    {
        var r = TillEngine.ComputeBlock(1.2, Store);
        Assert.Equal(1, r.ScoOn);
        // routedDemand 0.66×1.2 = 0.792 < laneCapacity 1.8 → credit = 0.792
        Close(0.792, r.Credit, 6);
    }

    [Fact]
    public void Credit_uses_routed_demand_when_it_is_the_smaller()
    {
        Close(0.99, TillEngine.ComputeBlock(1.5, Store).Credit, 6); // routed 0.99 < 1.8
    }

    [Fact]
    public void Credit_caps_at_lane_capacity_when_routed_demand_is_larger()
    {
        Close(1.8, TillEngine.ComputeBlock(3, Store).Credit, 6); // routed 1.98 > 1.8 → credit 1.8
    }

    // With credit 1.8 the thresholds become 3.0 / 3.9 / 4.9 / 5.9 / 6.9.
    [Theory]
    [InlineData(1.0, 1)] // below first step, credit 0 → tier 1
    [InlineData(1.2, 1)]
    [InlineData(3.0, 2)] // 3.0 not < 3.0, but < 3.9 → 2
    [InlineData(3.9, 3)] // = 2.1+credit → 3
    [InlineData(5.0, 4)] // < 5.9 → 4
    [InlineData(6.0, 5)] // < 6.9 → 5
    [InlineData(7.0, 6)] // ≥ 6.9 → 6
    public void Mco_tier_with_credit_shift(double seg, double mco) => Assert.Equal(mco, TillEngine.ComputeBlock(seg, Store).Mco);

    [Fact]
    public void Sco_tier_equals_the_bank_open_flag_capped_by_lanes()
    {
        Assert.Equal(1, TillEngine.ComputeBlock(3, Store).Sco);
        Assert.Equal(0, TillEngine.ComputeBlock(0.5, Store).Sco);
    }

    [Fact]
    public void Combined_total_is_mco_plus_sco()
    {
        var r = TillEngine.ComputeBlock(3, Store);
        Assert.Equal(r.Mco + r.Sco, r.Total);
        Assert.Equal(3, r.Total);
    }

    [Fact]
    public void Legacy_path_ceils_when_remainder_at_least_0_1_and_floors_otherwise()
    {
        var legacy = Store with { IsNewScoModel = false };
        Assert.Equal(3, TillEngine.ComputeBlock(2.1, legacy).Mco);
        Assert.Equal(3, TillEngine.ComputeBlock(2.5, legacy).Mco);
        Assert.Equal(2, TillEngine.ComputeBlock(2.05, legacy).Mco);
        Assert.Equal(3, TillEngine.ComputeBlock(3.0, legacy).Mco);
    }

    [Fact]
    public void Legacy_path_sco_tier_is_always_0()
    {
        Assert.Equal(0, TillEngine.ComputeBlock(5, Store with { IsNewScoModel = false }).Sco);
        Assert.Equal(0, TillEngine.ComputeBlock(5, Store with { HasSco = false }).Sco);
    }

    [Fact]
    public void Segment_at_or_below_0_is_nothing()
    {
        var r = TillEngine.ComputeBlock(0, Store);
        Assert.Equal((0.0, 0.0, 0.0, 0.0), (r.Mco, r.Sco, r.Total, r.ScoOn));
        Assert.Equal(0, TillEngine.ComputeBlock(-3, Store).Total);
    }

    [Fact]
    public void Mco_is_capped_at_physical_register_count()
    {
        var r = TillEngine.ComputeBlock(9, Store with { McoRegisters = 2, ScoLanes = 1 }); // raw MCO 6 → cap 2; SCO 1 → cap 1
        Assert.Equal(2, r.Mco);
        Assert.Equal(1, r.Sco);
        Assert.Equal(3, r.Total);
    }

    [Fact]
    public void Zero_sco_lanes_means_no_credit_and_sco_capped_to_0()
    {
        var r = TillEngine.ComputeBlock(5, Store with { ScoLanes = 0 });
        Assert.Equal(0, r.Credit); // laneCapacity 0 → MIN(0, routed) = 0
        Assert.Equal(0, r.Sco);
    }

    [Fact]
    public void Daily_hours_sum_combined_till_counts_times_a_quarter()
    {
        var blocks = TillEngine.ComputeDay([1, 3, 6], Store); // totals 1 + 3 + 6 = 10 tills
        Close(2.5, TillEngine.DailyCheckoutHours(blocks), 6); // 10 × 0.25
    }

    [Fact]
    public void Puts_a_till_in_every_trading_quarter_hour_even_when_demand_rounds_to_none()
    {
        var legacy = Store with { IsNewScoModel = false };
        var none = TillEngine.ComputeBlock(0.05, legacy); // under the 0.1 remainder: no till
        Assert.Equal(0, none.Total);
        var lifted = TillEngine.LiftToOneTill(none, legacy);
        Assert.Equal((1.0, 1.0), (lifted.Mco, lifted.Total));
        // A store with lanes and no registers gets the SCO attendant.
        var sco = TillEngine.LiftToOneTill(TillEngine.ComputeBlock(0, Store with { McoRegisters = 0 }), 0, 4);
        Assert.Equal((1.0, 0.0, 1.0), (sco.Sco, sco.Mco, sco.Total));
        // Demand that already asks for a till is untouched.
        var busy = TillEngine.ComputeBlock(3, Store);
        Assert.Same(busy, TillEngine.LiftToOneTill(busy, Store));
    }

    private static BlockResult Block(double segment, double mco, double sco) =>
        new(segment, sco > 0 ? 1 : 0, 0, mco, mco, sco, mco + sco);

    [Fact]
    public void CapToBudget_trims_above_floor_tills_from_the_quietest_blocks()
    {
        // 4 busy blocks (3 tills) + 4 quiet blocks (1 till) = 4h raw; budget 3h.
        var blocks = Enumerable.Range(0, 4).Select(_ => Block(3.0, 2, 1))
            .Concat(Enumerable.Range(0, 4).Select(_ => Block(0.3, 1, 0))).ToList();
        TillEngine.CapToBudget(blocks, 3);
        Close(3, blocks.Sum(b => b.Total) * 0.25, 6);
        // The quiet blocks kept their single till; trimming came off the busy ones
        // (SCO attendant released first).
        foreach (var b in blocks.Where(x => x.Segment < 1)) Assert.Equal(1, b.Total);
        foreach (var b in blocks.Where(x => x.Segment >= 1))
        {
            Assert.Equal(2, b.Total);
            Assert.Equal(0, b.Sco);
        }
    }

    [Fact]
    public void CapToBudget_never_trims_a_block_below_1_till()
    {
        var blocks = Enumerable.Range(0, 8).Select(_ => Block(0.5, 1, 0)).ToList(); // 2h raw
        TillEngine.CapToBudget(blocks, 1); // budget below the floor
        foreach (var b in blocks) Assert.Equal(1, b.Total);
    }

    [Fact]
    public void CapToBudget_leaves_an_already_within_budget_day_untouched()
    {
        var blocks = Enumerable.Range(0, 4).Select(_ => Block(2.0, 2, 1)).ToList();
        TillEngine.CapToBudget(blocks, 3);
        foreach (var b in blocks) Assert.Equal(3, b.Total);
    }

    [Fact]
    public void TierBrackets_reproduce_the_ladder_the_engine_walks()
    {
        var b = TillEngine.TierBrackets(G248).Where(x => x.Total > 0).ToList();
        Assert.Equal([1, 2, 3, 4, 5], b.Select(x => x.Total));
        // 1 till from just above zero; the SCO bank opens at the first step.
        Close(1.2, b[1].From, 3);
        // The stretched rung: first_step / (1 - sco_share) = 1.2 / 0.41.
        Close(1.2 / 0.41, b[2].From, 2);
        // Above the credit cap the rungs settle exactly 1.00 apart.
        Close(2.1 + 0.39 * 6, b[3].From, 2);
        Close(1.0, b[4].From - b[3].From, 2);
    }

    [Fact]
    public void TierBrackets_agree_with_ComputeBlock_at_every_boundary()
    {
        foreach (var br in TillEngine.TierBrackets(G248))
        {
            Assert.Equal(br.Total, TillEngine.ComputeBlock(br.From, G248).Total);
            if (br.To is { } to)
            {
                // Just inside the top of the rung is still this many tills…
                Assert.Equal(br.Total, TillEngine.ComputeBlock(to - 0.002, G248).Total);
                // …and the boundary itself has already stepped up.
                Assert.True(TillEngine.ComputeBlock(to, G248).Total > br.Total);
            }
        }
    }

    [Fact]
    public void TierBrackets_cap_the_ladder_at_the_registers_the_store_has()
    {
        Assert.Equal(2, TillEngine.TierBrackets(G248 with { McoRegisters = 2 }).Max(b => b.Mco));
    }

    [Fact]
    public void TierBrackets_give_the_legacy_path_evenly_spaced_rungs()
    {
        var legacy = TillEngine.TierBrackets(G248 with { IsNewScoModel = false }).Where(b => b.Total > 0).ToList();
        // ceil-on-0.1 means each till opens 1.00 apart from x.1.
        Close(1.0, legacy[1].From - legacy[0].From, 2);
        Assert.All(legacy, b => Assert.Equal(0, b.Sco));
    }

    [Fact]
    public void CreditCapSegment_is_where_routed_demand_overtakes_lane_capacity()
    {
        var cap = TillEngine.CreditCapSegment(G248);
        Assert.NotNull(cap);
        Close((0.39 * 6) / 0.59, cap.Value, 4);
        // Below it the credit still grows; at it the cap binds.
        Assert.True(TillEngine.ComputeBlock(cap.Value - 0.5, G248).Credit < 0.39 * 6 - 1e-9);
        Close(0.39 * 6, TillEngine.ComputeBlock(cap.Value + 0.5, G248).Credit, 6);
    }

    [Fact]
    public void CreditCapSegment_is_null_with_no_sco_bank()
    {
        Assert.Null(TillEngine.CreditCapSegment(new CheckoutAttrs
        {
            HasSco = false, IsNewScoModel = false, McoRegisters = 4, ScoLanes = 0,
            ScoEfficiency = 0.45, FirstStep = 1.2, ScoShare = 0.66,
        }));
    }
}
