// New: each person's notifications are their own - a super user's too. The TypeScript app
// left this to row-level security, whose notifications policies also let a super user
// count, list and mark read everyone's; the owner had that corrected.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Review;
using Roster.Web.Tests.Review;
using static Roster.Web.Tests.Review.ReviewTestDb;

namespace Roster.Web.Tests.Notifications;

public sealed class NotificationScopeTests : IDisposable
{
    private static readonly DateOnly PS = new(2026, 11, 23);
    private static readonly string Link = ReviewLinks.ReviewLink(St1, PS);
    private static readonly DateTimeOffset At = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private readonly ReviewTestDb _t = new();

    public NotificationScopeTests()
    {
        using var db = _t.System();
        void Add(ScopedUser u, string body, int minutes, bool read = false, string? link = null) =>
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(), UserId = u.Id, Kind = "review_submitted", Body = body, Link = link,
                CreatedAt = At.AddMinutes(minutes), ReadAt = read ? At : null,
            });
        Add(Admin, "admin 1", 1, read: true);
        Add(Admin, "admin 2", 2);
        Add(Admin, "admin 3", 3, link: Link);
        Add(Sm, "sm 1", 4);
        Add(Sm, "sm 2", 5, link: Link);
        Add(Emso, "emso 1", 6, link: Link);
        db.SaveChanges();
    }

    public void Dispose() => _t.Dispose();

    private List<(string Body, bool Read)> All()
    {
        using var db = _t.System();
        return [.. db.Notifications.AsNoTracking().OrderBy(n => n.CreatedAt).Select(n => new { n.Body, n.ReadAt }).AsEnumerable().Select(n => (n.Body, n.ReadAt != null))];
    }

    [Fact]
    public async Task A_super_users_count_and_list_are_their_own()
    {
        _t.As(Admin);
        Assert.Equal(2, await _t.Notifications.UnreadCountAsync());
        Assert.Equal(["admin 3", "admin 2", "admin 1"], (await _t.Notifications.ListMineAsync()).Select(n => n.Body));
        _t.As(Sm);
        Assert.Equal(2, await _t.Notifications.UnreadCountAsync());
        Assert.Equal(["sm 2", "sm 1"], (await _t.Notifications.ListMineAsync()).Select(n => n.Body));
    }

    [Fact]
    public async Task A_super_users_mark_all_read_leaves_everyone_elses_unread()
    {
        _t.As(Admin);
        await _t.Notifications.MarkAllReadAsync();
        Assert.Equal(0, await _t.Notifications.UnreadCountAsync());
        Assert.Equal(
            [("admin 1", true), ("admin 2", true), ("admin 3", true), ("sm 1", false), ("sm 2", false), ("emso 1", false)],
            All());
    }

    [Fact]
    public async Task A_super_user_opening_a_review_clears_only_their_own_notices_for_it()
    {
        _t.As(Admin);
        Assert.Equal(1, (await _t.Service.LoadPageAsync(St1, PS)).UnreadNotices);
        await _t.Service.MarkReviewNoticesReadAsync(St1, PS);
        Assert.Equal(0, (await _t.Service.LoadPageAsync(St1, PS)).UnreadNotices);
        Assert.Equal(
            [("admin 1", true), ("admin 2", false), ("admin 3", true), ("sm 1", false), ("sm 2", false), ("emso 1", false)],
            All());
        // The store's own visit clears its own.
        _t.As(Sm);
        Assert.Equal(1, (await _t.Service.LoadPageAsync(St1, PS)).UnreadNotices);
        await _t.Service.MarkReviewNoticesReadAsync(St1, PS);
        Assert.Equal([("sm 1", false), ("sm 2", true), ("emso 1", false)], All().Skip(3));
    }
}
