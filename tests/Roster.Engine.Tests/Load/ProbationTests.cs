// Port of src/lib/load/probation.test.ts.

using Roster.Engine.Load;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Load;

public class ProbationTests
{
    private static readonly DateOnly Today = ParseIso("2026-08-08");

    // Ambient, as seeded: 19 minutes for a trained team member, stretching to 26.6
    // for someone in their first month.
    private static readonly PalletMinutes Ambient = new(19, 20.9, 23.8, 26.6);

    [Fact]
    public void Counts_the_start_date_as_day_1()
    {
        Assert.Equal(1, Probation.DaysOfService(ParseIso("2026-08-08"), Today));
        Assert.Equal(2, Probation.DaysOfService(ParseIso("2026-08-07"), Today));
    }

    [Fact]
    public void Is_null_when_there_is_no_start_date()
    {
        // Most of the current team predates this field. Treating them as new
        // starters would slow down every store's chart overnight.
        Assert.Null(Probation.DaysOfService((DateOnly?)null, Today));
        Assert.Null(Probation.DaysOfService((string?)null, Today));
        Assert.Null(Probation.DaysOfService("", Today));
    }

    [Fact]
    public void Is_null_for_a_start_date_in_the_future()
    {
        Assert.Null(Probation.DaysOfService(ParseIso("2026-09-01"), Today));
    }

    [Fact]
    public void Puts_each_tenure_in_the_band_its_label_promises()
    {
        Assert.Equal("lt30", Probation.TenureBandFor(1));
        Assert.Equal("lt30", Probation.TenureBandFor(30)); // inclusive upper bound
        Assert.Equal("lt60", Probation.TenureBandFor(31));
        Assert.Equal("lt60", Probation.TenureBandFor(60));
        Assert.Equal("lt90", Probation.TenureBandFor(61));
        Assert.Equal("lt90", Probation.TenureBandFor(90));
        Assert.Equal("gt90", Probation.TenureBandFor(91));
        Assert.Equal("gt90", Probation.TenureBandFor(5000));
        Assert.Equal("gt90", Probation.TenureBandFor(null));
    }

    [Fact]
    public void Resolves_a_start_date_straight_to_a_band()
    {
        Assert.Equal("lt30", Probation.BandForStartDate(ParseIso("2026-08-01"), Today));
        Assert.Equal("lt60", Probation.BandForStartDate(ParseIso("2026-07-01"), Today));
        Assert.Equal("lt90", Probation.BandForStartDate(ParseIso("2026-06-01"), Today));
        Assert.Equal("gt90", Probation.BandForStartDate(ParseIso("2025-01-01"), Today));
        Assert.Equal("gt90", Probation.BandForStartDate((DateOnly?)null, Today));
    }

    [Fact]
    public void Reads_the_bands_own_figure()
    {
        Assert.Equal(19, Probation.MinutesForBand(Ambient, "gt90"));
        Assert.Equal(20.9, Probation.MinutesForBand(Ambient, "lt90"));
        Assert.Equal(23.8, Probation.MinutesForBand(Ambient, "lt60"));
        Assert.Equal(26.6, Probation.MinutesForBand(Ambient, "lt30"));
    }

    [Fact]
    public void Falls_back_to_the_trained_figure_when_a_band_is_unset()
    {
        // A blank cell means "no extra allowance for this load type" - it must
        // never be read as zero minutes, which would make the pallet free.
        var sparse = new PalletMinutes(19);
        Assert.Equal(19, Probation.MinutesForBand(sparse, "lt30"));
        Assert.Equal(19, Probation.MinutesForBand(sparse, "lt60"));
        Assert.Equal(19, Probation.MinutesForBand(sparse, "lt90"));
    }

    [Fact]
    public void Ignores_a_nonsensical_zero_or_negative_and_uses_the_trained_figure()
    {
        var bad = new PalletMinutes(19, 0, -4);
        Assert.Equal(19, Probation.MinutesForBand(bad, "lt90"));
        Assert.Equal(19, Probation.MinutesForBand(bad, "lt60"));
    }

    [Fact]
    public void Costs_a_new_starter_more_than_a_trained_one_on_the_same_pallet()
    {
        Assert.True(Probation.MinutesForBand(Ambient, "lt30") > Probation.MinutesForBand(Ambient, "gt90"));
    }

    [Fact]
    public void Tenure_label_shows_only_while_someone_is_still_inside_a_band()
    {
        Assert.Equal("8d · <30d", Probation.TenureLabel(8));
        Assert.Equal("45d · <60d", Probation.TenureLabel(45));
        Assert.Null(Probation.TenureLabel(200));
        Assert.Null(Probation.TenureLabel(null));
    }
}
