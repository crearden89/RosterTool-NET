// Port of the public surface of src/lib/notifications.ts.

namespace Roster.Data.Services.Notifications;

public sealed record NotificationRow(Guid Id, string Kind, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>The signed-in user's bell (the store scope limits it to their own rows).</summary>
public interface INotificationReader
{
    /// <summary>getUnreadCount: how many of the signed-in user's notifications are unread.</summary>
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);

    /// <summary>listMyNotifications: the signed-in user's 50 most recent notifications, newest first.</summary>
    Task<List<NotificationRow>> ListMyNotificationsAsync(CancellationToken ct = default);
}
