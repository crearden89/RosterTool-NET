// New: registers the Review page's service (see CONVENTIONS.md, "Pages").

using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Web.Features.Notifications;

namespace Roster.Web.Features.Review;

public sealed class ReviewModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        // The stand-in until the data layer's review loader, notifications and plan
        // approval land (Review:FakeTier picks who it is signed in as).
        services.TryAddSingleton<FakeNotificationStore>();
        services.TryAddSingleton<FakeReviewStore>();
        services.TryAddScoped<IReviewService, FakeReviewService>();
    }
}
