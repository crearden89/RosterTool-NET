using Roster.Web.Auth;

namespace Roster.Web.Notifications;

/// <summary>One notification for the signed-in person.</summary>
public sealed record NotificationItem(Guid Id, string Kind, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>
/// The signed-in person's notifications: the unread count in the menu and the
/// Notifications page. The data layer provides the real one; until then
/// <see cref="NoNotifications"/> has none.
/// </summary>
public interface INotificationService
{
    /// <summary>How many of the person's notifications are unread.</summary>
    Task<int> UnreadCountAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>The person's most recent notifications, newest first (at most 50).</summary>
    Task<IReadOnlyList<NotificationItem>> ListAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>Marks all of the person's notifications read (opening the page does).</summary>
    Task MarkAllReadAsync(AppUser user, CancellationToken cancellationToken = default);
}

/// <summary>
/// The stand-in: no notifications, or, for trying the shell, the unread count in
/// <c>Notifications:PlaceholderUnreadCount</c>.
/// </summary>
public sealed class NoNotifications(IConfiguration configuration) : INotificationService
{
    public Task<int> UnreadCountAsync(AppUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult(Math.Max(0, configuration.GetValue("Notifications:PlaceholderUnreadCount", 0)));

    public Task<IReadOnlyList<NotificationItem>> ListAsync(AppUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationItem>>([]);

    public Task MarkAllReadAsync(AppUser user, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
