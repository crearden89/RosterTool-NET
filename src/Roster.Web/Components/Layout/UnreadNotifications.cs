// New: the unread count for the menu's Notifications item and the phone bar (the
// TypeScript app's getUnreadCount, src/lib/notifications.ts). A stand-in until the
// data layer's notifications service (owner D2) is on main: then the shell calls that.

using Roster.Web.Auth;

namespace Roster.Web.Components.Layout;

internal interface IUnreadNotifications
{
    /// <summary>How many of the person's notifications are unread.</summary>
    Task<int> CountAsync(SignedInUser user, CancellationToken cancellationToken = default);
}

/// <summary>No notifications; for trying the shell, the count in <c>Notifications:PlaceholderUnreadCount</c>.</summary>
internal sealed class PlaceholderUnreadNotifications(IConfiguration configuration) : IUnreadNotifications
{
    public Task<int> CountAsync(SignedInUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult(Math.Max(0, configuration.GetValue("Notifications:PlaceholderUnreadCount", 0)));
}
