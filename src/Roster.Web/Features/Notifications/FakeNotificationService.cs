// New: a stand-in for the notifications until the data layer's reads land: a few
// invented notices held in memory for the life of the app, so the page and the menu's
// badge can be built and tried. Registered by NotificationsModule.

namespace Roster.Web.Features.Notifications;

/// <summary>The stand-in's notices, shared by every connection (one person's worth).</summary>
public sealed class FakeNotificationStore
{
    private readonly object _gate = new();
    private readonly List<NotificationItem> _items;

    public FakeNotificationStore(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        _items =
        [
            new(Guid.Parse("8b0f1c4e-0000-4000-8000-000000000001"), "review_submitted",
                "G102 Riverside (G102) submitted its plan for Mon 7 Dec – Sun 20 Dec 2026. It's ready for your review.",
                "/plan/review?period=2026-12-07", now.AddMinutes(-35), null),
            new(Guid.Parse("8b0f1c4e-0000-4000-8000-000000000002"), "review_reminder",
                "Reminder from j.morgan: please check and submit your plan for Mon 7 Dec – Sun 20 Dec 2026 (due Wed 2 Dec, 6:00 pm).",
                "/plan/review?period=2026-12-07", now.AddHours(-5), null),
            new(Guid.Parse("8b0f1c4e-0000-4000-8000-000000000003"), "emso_unapproved",
                "2 stores in your area have rosters still to approve for next fortnight.",
                null, now.AddDays(-2), now.AddDays(-2).AddHours(1)),
        ];
    }

    public IReadOnlyList<NotificationItem> Snapshot()
    {
        lock (_gate) return [.. _items.OrderByDescending(n => n.CreatedAt).Take(50)];
    }

    public void MarkAllRead(DateTimeOffset at)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
                if (_items[i].ReadAt is null) _items[i] = _items[i] with { ReadAt = at };
        }
    }

    public void Add(NotificationItem item)
    {
        lock (_gate) _items.Add(item);
    }

    /// <summary>Marks read the unread notices that link to <paramref name="link"/>.</summary>
    public void MarkRead(string link, DateTimeOffset at)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
                if (_items[i].ReadAt is null && _items[i].Link == link) _items[i] = _items[i] with { ReadAt = at };
        }
    }
}

/// <summary><see cref="INotificationService"/> over <see cref="FakeNotificationStore"/>.</summary>
public sealed class FakeNotificationService(FakeNotificationStore store, TimeProvider clock) : INotificationService
{
    public Task<IReadOnlyList<NotificationItem>> ListMineAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(store.Snapshot());

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(store.Snapshot().Count(n => n.ReadAt is null));

    public Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        store.MarkAllRead(clock.GetUtcNow());
        return Task.CompletedTask;
    }
}
