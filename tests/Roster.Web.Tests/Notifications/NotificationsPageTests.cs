// New: the Notifications page rendered (bUnit): the person's notices, newest first, marked
// read once the screen is open (MarkRead.tsx), with the menu's badge told to read again.

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Components.Layout;
using Roster.Web.Features.Notifications;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Notifications;

public sealed class NotificationsPageTests : UiTestContext
{
    public NotificationsPageTests()
    {
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<FakeNotificationStore>();
        Services.AddScoped<INotificationService, FakeNotificationService>();
    }

    [Fact]
    public void Lists_the_notices_newest_first_and_marks_them_read_once_open()
    {
        var shell = Services.GetRequiredService<ShellEvents>();
        var told = 0;
        shell.UnreadChanged += () => told++;

        var cut = Render<NotificationsPage>();
        cut.WaitForAssertion(() => Assert.Contains("0 unread · alerts for you, newest first", cut.Markup));
        Assert.Equal(1, told);
        var items = cut.FindAll("div.max-w-2xl > div");
        Assert.Equal(3, items.Count);
        Assert.StartsWith("G102 Riverside", items[0].QuerySelector("p")!.TextContent.Trim());
        Assert.Equal(2, cut.FindAll("a").Count(a => a.TextContent == "Open"));
        Assert.Empty(cut.FindAll(".sr-only")); // nothing reads as unread now
    }

    [Fact]
    public void Says_so_when_there_is_nothing()
    {
        Services.AddScoped<INotificationService, EmptyNotifications>();
        var cut = Render<NotificationsPage>();
        cut.WaitForAssertion(() => Assert.Contains("No notifications.", cut.Markup));
        Assert.Contains("0 unread", cut.Markup);
    }

    [Theory]
    [InlineData("2026-01-05T06:07:08Z", "UTC", "05/01/2026, 6:07:08 am")]
    [InlineData("2026-11-25T00:00:09Z", "UTC", "25/11/2026, 12:00:09 am")]
    [InlineData("2026-11-25T12:30:00Z", "UTC", "25/11/2026, 12:30:00 pm")]
    [InlineData("2026-11-25T06:17:05Z", "Australia/Sydney", "25/11/2026, 5:17:05 pm")]
    public void Writes_the_time_as_en_AU_toLocaleString_does(string at, string zone, string expected) =>
        Assert.Equal(expected, NotificationFormat.LocaleString(
            DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(zone)));

    [Fact]
    public void Writes_the_time_in_Sydney_whatever_the_servers_zone() =>
        Assert.Equal("25/11/2026, 5:17:05 pm", NotificationFormat.LocaleString(
            DateTimeOffset.Parse("2026-11-25T06:17:05Z", System.Globalization.CultureInfo.InvariantCulture)));

    private sealed class EmptyNotifications : INotificationService
    {
        public Task<IReadOnlyList<NotificationItem>> ListMineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NotificationItem>>([]);
        public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task MarkAllReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
