// New: maps the file-feed endpoints (the services are registered by Roster.Data.Feeds).

namespace Roster.Web.Features.Feeds;

public sealed class FeedsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) { }

    public void Map(IEndpointRouteBuilder endpoints)
    {
        // Both read their own form or body and answer for themselves (403/401 as JSON), as
        // the TypeScript routes did; the feed is authenticated by its bearer token.
        endpoints.MapPost(FeedEndpoints.UploadsPath, FeedEndpoints.UploadAsync).DisableAntiforgery();
        endpoints.MapPost(FeedEndpoints.IngestPath, FeedEndpoints.IngestAsync).DisableAntiforgery();
        endpoints.MapGet(FeedEndpoints.ExportUkgPath, FeedEndpoints.ExportUkgAsync);
        endpoints.MapGet(FeedEndpoints.PrintUkgRosterPath, FeedEndpoints.PrintUkgRosterAsync);
    }
}
