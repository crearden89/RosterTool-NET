// Port of src/lib/notifications.ts.

using Microsoft.EntityFrameworkCore;

namespace Roster.Data.Services.Notifications;

/// <summary><see cref="INotificationReader"/> as the signed-in user (the store scope keeps it to their own rows).</summary>
public sealed class NotificationReaderService(IRosterDbContextFactory contexts) : INotificationReader
{
    // Count of the signed-in user's unread notifications.
    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        return await db.Notifications.AsNoTracking().CountAsync(n => n.ReadAt == null, ct);
    }

    // The signed-in user's most recent notifications, newest first.
    public async Task<List<NotificationRow>> ListMyNotificationsAsync(CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        return await db.Notifications.AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(50)
            .Select(n => new NotificationRow(n.Id, n.Kind, n.Body, n.Link, n.CreatedAt, n.ReadAt))
            .ToListAsync(ct);
    }
}
