// Port of src/lib/notifications.ts.

using Microsoft.EntityFrameworkCore;

namespace Roster.Data.Services.Notifications;

/// <summary>
/// <see cref="INotificationReader"/> as the signed-in user: their own rows only. The rows are
/// picked by user id, not left to the store scope's notifications rule, which (as the
/// TypeScript app's notif_sel policy did) lets a super user read everyone's: the bell and
/// the Notifications page are the person's own, whoever they are (owner's correction of the
/// TypeScript app).
/// </summary>
public sealed class NotificationReaderService(IRosterDbContextFactory contexts) : INotificationReader
{
    // Count of the signed-in user's unread notifications.
    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        if (db.Access.User?.Id is not { } me) return 0;
        return await db.Notifications.AsNoTracking().CountAsync(n => n.UserId == me && n.ReadAt == null, ct);
    }

    // The signed-in user's most recent notifications, newest first.
    public async Task<List<NotificationRow>> ListMyNotificationsAsync(CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        if (db.Access.User?.Id is not { } me) return [];
        return await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == me)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(50)
            .Select(n => new NotificationRow(n.Id, n.Kind, n.Body, n.Link, n.CreatedAt, n.ReadAt))
            .ToListAsync(ct);
    }
}
