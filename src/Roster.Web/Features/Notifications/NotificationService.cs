// Port of src/app/(app)/notifications/actions.ts (markAllRead), over the data layer's
// reads of src/lib/notifications.ts (getUnreadCount, listMyNotifications).

using System.Data.Common;
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
/// <remarks>
/// The menu's badge reads this on every page, so the data layer is reached per call, and a
/// database that fails (or isn't configured) reads as nothing unread and no notifications -
/// as the TypeScript app's reads did (<c>count ?? 0</c>, <c>data ?? []</c>) - rather than
/// breaking the page around the badge.
/// </remarks>
public sealed class NotificationService(
    IServiceProvider services,
    IAccessContextAccessor access,
    TimeProvider clock,
    ILogger<NotificationService> log) : INotificationService
{
    public async Task<IReadOnlyList<NotificationItem>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        if (access.Current.User is null) return [];
        try
        {
            var rows = await services.GetRequiredService<INotificationReader>().ListMyNotificationsAsync(cancellationToken);
            return [.. rows.Select(n => new NotificationItem(n.Id, n.Kind, n.Body, n.Link, n.CreatedAt, n.ReadAt))];
        }
        catch (Exception e) when (Unreachable(e))
        {
            log.LogWarning(e, "Reading notifications failed");
            return [];
        }
    }

    public async Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        if (access.Current.User is null) return 0;
        try
        {
            return await services.GetRequiredService<INotificationReader>().GetUnreadCountAsync(cancellationToken);
        }
        catch (Exception e) when (Unreachable(e))
        {
            log.LogWarning(e, "Counting unread notifications failed");
            return 0;
        }
    }

    // Mark all of the signed-in user's unread notifications as read. Called when they
    // open the Notifications screen.
    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (access.Current.User is null) return; // requireUser: the page itself needs a sign-in
        try
        {
            await using var db = services.GetRequiredService<IRosterDbContextFactory>().Create();
            var now = clock.GetUtcNow();
            var unread = await db.Notifications.Where(n => n.ReadAt == null).ToListAsync(cancellationToken);
            foreach (var n in unread) n.ReadAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (Unreachable(e) || e is ScopeViolationException)
        {
            // Leave the badge; the next visit retries.
            log.LogWarning(e, "Marking notifications read failed");
        }
    }

    // The database failed, or there is none configured (the data layer's options throw).
    private static bool Unreachable(Exception e) =>
        e is DbException or DbUpdateException || (e is InvalidOperationException && e is not ScopeViolationException);
}
