// Port of src/lib/notifications.test.ts and src/app/(app)/notifications/actions.test.ts (the
// TypeScript app's tests for the owner's decision of 26/11/2026): a super user's bell, list and
// mark-all-read are their own, with the TypeScript tests' own rows. Their fake database applied
// no row-level security, which is how the notifications table reads for a super user.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Review;
using Roster.Web.Tests.Review;
using static Roster.Web.Tests.Review.ReviewTestDb;

namespace Roster.Web.Tests.Notifications;

public sealed class NotificationsOwnTests : IDisposable
{
    private static readonly DateOnly PS = new(2026, 11, 23);
    private static readonly string Review = ReviewLinks.ReviewLink(St1, PS);
    private static readonly DateTimeOffset AlreadyRead = DateTimeOffset.Parse("2026-11-20T02:00:00Z");
    private readonly ReviewTestDb _t = new();
    private readonly Dictionary<string, Guid> _id = [];

    public void Dispose() => _t.Dispose();

    private void Seed(params (string Name, Guid UserId, string Kind, string Body, string? Link, string CreatedAt, DateTimeOffset? ReadAt)[] rows)
    {
        using var db = _t.System();
        foreach (var r in rows)
        {
            var id = _id[r.Name] = Guid.NewGuid();
            db.Notifications.Add(new Notification
            {
                Id = id, UserId = r.UserId, Kind = r.Kind, Body = r.Body, Link = r.Link,
                CreatedAt = DateTimeOffset.Parse(r.CreatedAt), ReadAt = r.ReadAt,
            });
        }
        db.SaveChanges();
    }

    private DateTimeOffset? ReadAt(string name)
    {
        using var db = _t.System();
        return db.Notifications.AsNoTracking().Single(n => n.Id == _id[name]).ReadAt;
    }

    // ─── notifications.test.ts ──────────────────────────────────────────────
    private void SeedBell() => Seed(
        ("n1", Admin.Id, "review_submitted", "Sample Store submitted its plan", Review, "2026-11-25T22:30:00Z", null),
        ("n2", Admin.Id, "emso_roster_check", "Two stores have no roster yet", null, "2026-11-20T01:00:00Z", AlreadyRead),
        ("n3", Sm.Id, "review_returned", "Your plan came back with a query", Review, "2026-11-26T00:00:00Z", null),
        ("n4", Sm.Id, "review_reminder", "Please submit your plan", null, "2026-11-24T00:00:00Z", null));

    [Fact]
    public async Task Counts_only_their_own_unread_notices_on_the_bell()
    {
        SeedBell();
        _t.As(Admin);
        Assert.Equal(1, await _t.Notifications.UnreadCountAsync());
        _t.As(Sm);
        Assert.Equal(2, await _t.Notifications.UnreadCountAsync());
    }

    [Fact]
    public async Task Counts_only_their_own_unread_notices_for_one_review()
    {
        SeedBell();
        _t.As(Admin);
        Assert.Equal(1, (await _t.Service.LoadPageAsync(St1, PS)).UnreadNotices);
        Assert.Equal(0, (await _t.Service.LoadPageAsync(St1, PS.AddDays(14))).UnreadNotices);
    }

    [Fact]
    public async Task Lists_only_their_own_notifications_newest_first()
    {
        SeedBell();
        _t.As(Admin);
        Assert.Equal([_id["n1"], _id["n2"]], (await _t.Notifications.ListMineAsync()).Select(n => n.Id));
        _t.As(Sm);
        Assert.Equal([_id["n3"], _id["n4"]], (await _t.Notifications.ListMineAsync()).Select(n => n.Id));
    }

    // ─── notifications/actions.test.ts ──────────────────────────────────────
    private void SeedMarks() => Seed(
        ("n1", Admin.Id, "review_submitted", "", null, "2026-11-25T00:00:00Z", null),
        ("n2", Admin.Id, "emso_roster_check", "", null, "2026-11-19T00:00:00Z", AlreadyRead),
        ("n3", Sm.Id, "review_returned", "", null, "2026-11-25T01:00:00Z", null),
        ("n4", Emso.Id, "review_submitted", "", null, "2026-11-25T02:00:00Z", null));

    [Fact]
    public async Task Mark_all_read_marks_only_a_super_users_own_notifications_read()
    {
        SeedMarks();
        _t.As(Admin);
        await _t.Notifications.MarkAllReadAsync();
        Assert.NotNull(ReadAt("n1"));
        Assert.Equal(AlreadyRead, ReadAt("n2")); // already read: unchanged
        Assert.Null(ReadAt("n3"));
        Assert.Null(ReadAt("n4"));
    }

    [Fact]
    public async Task Mark_all_read_marks_a_store_logins_own_notifications_read()
    {
        SeedMarks();
        _t.As(Sm);
        await _t.Notifications.MarkAllReadAsync();
        Assert.NotNull(ReadAt("n3"));
        Assert.Null(ReadAt("n1"));
        Assert.Null(ReadAt("n4"));
    }
}
