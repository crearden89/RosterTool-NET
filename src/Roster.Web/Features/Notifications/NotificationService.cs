// Port of src/app/(app)/notifications/actions.ts (markAllRead), over the data layer's
// reads of src/lib/notifications.ts (getUnreadCount, listMyNotifications).

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Scope;
using Roster.Data.Services.Notifications;

namespace Roster.Web.Features.Notifications;

/// <summary>
/// <see cref="INotificationService"/> as the signed-in person. Their reads and writes go
/// through the store scope's notification rule, as row-level security scoped them: their
/// own rows (a super user's reach every row, as the notifications policies allow).
/// </summary>
public sealed class NotificationService(
    INotificationReader reader,
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    TimeProvider clock,
    ILogger<NotificationService> log) : INotificationService
{
    public async Task<IReadOnlyList<NotificationItem>> ListMineAsync(CancellationToken cancellationToken = default) =>
        [.. (await reader.ListMyNotificationsAsync(cancellationToken))
            .Select(n => new NotificationItem(n.Id, n.Kind, n.Body, n.Link, n.CreatedAt, n.ReadAt))];

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) =>
        scope.User is null ? Task.FromResult(0) : reader.GetUnreadCountAsync(cancellationToken);

    // Mark all of the signed-in user's unread notifications as read. Called when they
    // open the Notifications screen.
    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (scope.User is null) return; // requireUser: the page itself needs a sign-in
        try
        {
            await using var db = contexts.Create();
            var now = clock.GetUtcNow();
            var unread = await db.Notifications.Where(n => n.ReadAt == null).ToListAsync(cancellationToken);
            foreach (var n in unread) n.ReadAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is DbUpdateException or ScopeViolationException)
        {
            // Leave the badge; the next visit retries.
            log.LogWarning(e, "Marking notifications read failed");
        }
    }
}
