// New: the Notifications page's service, and the menu's unread count - what
// src/app/(app)/notifications/page.tsx and actions.ts (markAllRead) do, with
// src/lib/notifications.ts's reads (getUnreadCount, listMyNotifications) behind them.
// Every call is for the signed-in person: their own notifications, nobody else's.

namespace Roster.Web.Features.Notifications;

/// <summary>One notification for the signed-in person (NotificationRow).</summary>
/// <param name="Link">Where "Open" goes (a review's page), when it has somewhere to go.</param>
/// <param name="ReadAt">Null while unread.</param>
public sealed record NotificationItem(Guid Id, string Kind, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    public bool IsRead => ReadAt is not null;
}

public interface INotificationService
{
    /// <summary>listMyNotifications: the person's 50 most recent notifications, newest first.</summary>
    Task<IReadOnlyList<NotificationItem>> ListMineAsync(CancellationToken cancellationToken = default);

    /// <summary>getUnreadCount: how many of the person's notifications are unread (the menu's badge).</summary>
    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// markAllRead: marks all of the person's unread notifications read - opening the
    /// Notifications page does. A failure leaves them unread; the next visit retries.
    /// </summary>
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);
}
