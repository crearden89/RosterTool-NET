// Port of src/lib/plan/pageFigures.test.ts.

using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

public class PageFiguresTests
{
    [Fact]
    public void Writes_a_signed_figure_and_a_days_long_label()
    {
        Assert.Equal(["+0.09", "−0.13", "+0.00", "+1.26"], new[] { PageFigures.Signed(0.0912), PageFigures.Signed(-0.13), PageFigures.Signed(-0.001), PageFigures.Signed(1.257) });
        Assert.Equal("Tuesday 24 November", PageFigures.LongDayLabel(new DateOnly(2026, 11, 24)));
        Assert.Equal("Sunday 6 December", PageFigures.LongDayLabel(new DateOnly(2026, 12, 6)));
    }
}
