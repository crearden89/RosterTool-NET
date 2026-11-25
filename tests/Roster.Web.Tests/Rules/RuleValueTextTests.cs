// New: the Rostering Rules page's value helpers (RulesClient.tsx has no tests of its own).

using Roster.Engine.Core;
using Roster.Web.Features.Rules.Rostering;

namespace Roster.Web.Tests.Rules;

public class RuleValueTextTests
{
    private static RuleMeta Meta(string key) => RuleMetas.All.Single(m => m.Key == key);

    [Theory]
    [InlineData(360, "06:00")]
    [InlineData(1290, "21:30")]
    [InlineData(1440, "24:00")]
    [InlineData(0, "00:00")]
    // The page's own: hours floored, minutes rounded on their own.
    [InlineData(59.6, "00:00")]
    public void MinToTime_writes_hours_and_minutes(double minutes, string expected) =>
        Assert.Equal(expected, RuleValueText.MinToTime(minutes));

    [Theory]
    [InlineData("09:30", 570)]
    [InlineData("06:00", 360)]
    [InlineData("", 0)]
    [InlineData("7", 420)]
    public void TimeToMinutes_reads_a_blank_part_as_zero(string text, double expected) =>
        Assert.Equal(expected, RuleValueText.TimeToMinutes(text));

    [Fact]
    public void FormatValue_writes_each_unit_as_the_page_does()
    {
        Assert.Equal("180 min", RuleValueText.FormatValue(Meta("minPieceMin"), 180));
        Assert.Equal("06:00", RuleValueText.FormatValue(Meta("jsaEarliestMin"), 360));
        Assert.Equal("5 h", RuleValueText.FormatValue(Meta("jsaTermMaxShiftH"), 5));
        Assert.Equal("6 days", RuleValueText.FormatValue(Meta("maxConsecDays"), 6));
        Assert.Equal("2 people", RuleValueText.FormatValue(Meta("minPeopleOnFloor"), 2));
        Assert.Equal("25 %", RuleValueText.FormatValue(Meta("subCleaningFrontEndPct"), 25));
        Assert.Equal("0.5 h", RuleValueText.FormatValue(Meta("jsaTermMaxShiftH"), 0.5));
    }

    [Fact]
    public void RangeMessage_names_both_ends()
    {
        Assert.Equal("Allowed range: 60 min – 480 min.", RuleValueText.RangeMessage(Meta("minPieceMin")));
        Assert.Equal("Allowed range: 00:00 – 24:00.", RuleValueText.RangeMessage(Meta("jsaLatestMin")));
        Assert.Equal("Allowed range: 04:00 – 09:00.", RuleValueText.RangeMessage(Meta("lrEarliestStartMin")));
    }

    [Fact]
    public void A_box_is_dirty_only_when_its_value_moved()
    {
        Assert.False(RuleValueText.IsDirty(Meta("minPieceMin"), "180", 180));
        Assert.True(RuleValueText.IsDirty(Meta("minPieceMin"), "195", 180));
        // Number(""): a cleared box reads 0.
        Assert.True(RuleValueText.IsDirty(Meta("minPieceMin"), "", 180));
        Assert.False(RuleValueText.IsDirty(Meta("restGapMin"), "", 0));
        Assert.False(RuleValueText.IsDirty(Meta("jsaEarliestMin"), "06:00", 360));
        Assert.Equal(1000, RuleValueText.ValueOf(Meta("minPieceMin"), "1e3"));
    }

    [Fact]
    public void TextOf_shows_times_as_clock_and_numbers_as_typed()
    {
        Assert.Equal("16:00", RuleValueText.TextOf(Meta("jsaSchoolDayStartMin"), 960));
        Assert.Equal("660", RuleValueText.TextOf(Meta("restGapMin"), 660));
    }

    [Fact]
    public void Steps_follow_the_unit()
    {
        Assert.Equal(15, RuleValueText.Step(Meta("minPieceMin")));
        Assert.Equal(0.5, RuleValueText.Step(Meta("jsaTermWeekCapH")));
        Assert.Equal(1, RuleValueText.Step(Meta("maxConsecDays")));
    }

    [Fact]
    public void Categories_are_in_first_appearance_order_and_sections_follow_them()
    {
        Assert.Equal(
            ["Shift limits", "Weekly limits", "Floor safety", "Juniors (JSA)", "Manager pair", "Plan settings", "Sub-task shares"],
            RuleValueText.Categories);
        Assert.Equal(
            ["shift-limits", "weekly-limits", "floor-safety", "juniors-jsa", "manager-pair", "plan-settings", "sub-task-shares", "pay-rules", "custom-rules"],
            RuleValueText.Sections.Select(s => s.Id));
        Assert.All(RuleValueText.Categories, c => Assert.True(RuleValueText.Purpose.ContainsKey(c)));
    }
}
