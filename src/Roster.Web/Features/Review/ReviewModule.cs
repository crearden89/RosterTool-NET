// New: registers the Review page's service (see CONVENTIONS.md, "Pages").

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Review;

public sealed class ReviewModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.TryAddScoped<IReviewService, ReviewService>();
}
