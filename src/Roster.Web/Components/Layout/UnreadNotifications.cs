// New: the unread count for the menu's Notifications item and the phone bar (the
// TypeScript app's getUnreadCount, src/lib/notifications.ts), read through the
// Notifications page area's service (owner P3).

using Roster.Web.Auth;
using Roster.Web.Features.Notifications;

namespace Roster.Web.Components.Layout;

internal interface IUnreadNotifications
{
    /// <summary>How many of the person's notifications are unread.</summary>
    Task<int> CountAsync(SignedInUser user, CancellationToken cancellationToken = default);
}

/// <summary>The signed-in person's unread count, from the notifications service.</summary>
internal sealed class ServiceUnreadNotifications(INotificationService notifications) : IUnreadNotifications
{
    public Task<int> CountAsync(SignedInUser user, CancellationToken cancellationToken = default) =>
        notifications.UnreadCountAsync(cancellationToken);
}
