// New: the tile map's colours and codes, which the Floor Display reads too.

using Roster.Web.Features.RosterPage.Grid;

namespace Roster.Web.Tests.RosterPage;

public class TilesTests
{
    [Fact]
    public void Tile_MapsEveryDisplayGroup_WithCheckoutAliases()
    {
        Assert.Equal(new TileStyle("#8CC63F", "CO", "var(--brand-navy)"), Tiles.Tile["checkout"]);
        Assert.Equal(Tiles.Tile["checkout"], Tiles.Tile["registers"]);
        Assert.Equal(Tiles.Tile["checkout"], Tiles.Tile["scos"]);
        Assert.Equal(new TileStyle("#00005F", "LR"), Tiles.Tile["load_running"]);
        Assert.Equal(new TileStyle("var(--brand-red)", "BR"), Tiles.Tile["break"]);
        Assert.False(Tiles.Tile.ContainsKey("flex"));
    }

    [Fact]
    public void LegendItems_InTheTypeScriptOrder()
    {
        Assert.Equal(
            ["checkout:Checkout", "load_running:Load Running", "reset:Reset", "admin:Admin", "specials:Specials", "cleaning:Cleaning", "non_op:Non-Op", "break:Break"],
            Tiles.LegendItems.Select(i => $"{i.Key}:{i.Label}"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("flex", null)]
    [InlineData("nonsense", null)]
    [InlineData("scos", "Checkout")]
    public void TileView_ShowsNothingForFlexOrAnUnknownGroup_AndNamesAliasesByTheirLabel(string? group, string? title) =>
        Assert.Equal(title, TileView.For(group)?.Title);

    [Fact]
    public void TileView_AnErrorOnASubtaskTile_NamesBoth()
    {
        Assert.Equal("Load Running · Bread: off", TileView.For("load_running", "off", "bread")!.Value.Title);
        Assert.Equal("off", TileView.For("load_running", "off")!.Value.Title);
    }

    [Theory]
    [InlineData(1234.5, "$1,234.50", "$1,235")]
    [InlineData(1.005, "$1.01", "$1")]
    [InlineData(-0.6, "$-0.60", "$-1")]
    public void GridFormat_MoneyAndDollars(double n, string money, string dollars)
    {
        Assert.Equal(money, GridFormat.Money(n));
        Assert.Equal(dollars, GridFormat.Dollars(n));
    }

    [Theory]
    [InlineData(450, "07:30")]
    [InlineData(1305, "21:45")]
    public void RowEditFormat_HhmmAndBack(double min, string text)
    {
        Assert.Equal(text, RowEditFormat.Hhmm(min));
        Assert.Equal(min, RowEditFormat.ToMin(text));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("7", 420)]
    [InlineData("x:15", 15)]
    public void RowEditFormat_ToMin_CountsANonNumberAsZero(string text, double min) =>
        Assert.Equal(min, RowEditFormat.ToMin(text));
}
