// Port of src/lib/review/notify.ts (the reads and writes).

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Roster.Data.Entities;

namespace Roster.Data.Services.Review;

public sealed class ReviewNoticesService(IRosterDbContextFactory contexts, TimeProvider clock, ILogger<ReviewNoticesService> log) : IReviewNotices
{
    // Who reviews a store's fortnight: its area's EMSOs - or, where the area has
    // none set up yet, the super users, so a submission never goes unseen.
    // app_users is self-read under the store scope: other people need the system read.
    public async Task<List<Guid>> ReviewerIdsAsync(Guid? areaId, CancellationToken ct = default)
    {
        await using var admin = contexts.CreateSystem("review notices: who reviews a store's fortnight");
        if (areaId is not null)
        {
            var emsos = await admin.AppUsers.AsNoTracking().Where(u => u.Tier == Tier.Emso && u.AreaId == areaId)
                .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
            if (emsos.Count > 0) return emsos;
        }
        return await admin.AppUsers.AsNoTracking().Where(u => u.Tier == Tier.SuperUser)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
    }

    // The store's own logins.
    public async Task<List<Guid>> StoreUserIdsAsync(Guid storeId, CancellationToken ct = default)
    {
        await using var admin = contexts.CreateSystem("review notices: the store's own logins");
        return await admin.AppUsers.AsNoTracking().Where(u => u.Tier == Tier.Store && u.StoreId == storeId)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
    }

    // Never throws: a failed notice must not undo the review step it reports -
    // the step is recorded, and the review page shows the state either way.
    public async Task NotifyReviewAsync(IEnumerable<Guid> userIds, Guid actorId, string kind, string body, string link, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().Where(id => id != actorId).ToList();
        if (ids.Count == 0) return;
        try
        {
            await using var admin = contexts.CreateSystem("review notices: the bell");
            var kinds = ReviewNoticeKinds.All.ToList();
            // One notice per person per review: a newer one replaces their unread older one.
            await admin.Notifications
                .Where(n => ids.Contains(n.UserId) && n.Link == link && kinds.Contains(n.Kind) && n.ReadAt == null)
                .ExecuteDeleteAsync(ct);
            var now = clock.GetUtcNow();
            var text = body.Length > 500 ? body[..500] : body;
            admin.Notifications.AddRange(ids.Select(userId => new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Kind = kind,
                Body = text,
                Link = link,
                CreatedAt = now,
            }));
            await admin.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "review notification ({Kind}) failed: {Message}", kind, e.Message);
        }
    }
}
