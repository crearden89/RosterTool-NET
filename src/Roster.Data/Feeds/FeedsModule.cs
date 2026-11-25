// New: registers the file feeds with the data layer (DataModules finds it at start-up).

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Data.Feeds;

public sealed class FeedsModule : IDataModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // One parser per feed (ASSUMPTIONS.md #7): replace a registration to feed that data
        // from an internal system instead of a file.
        services.TryAddSingleton<IFeedParser<BenchmarkRow>, BenchmarkCsvFeed>();
        services.TryAddSingleton<IFeedParser<ActualsRow>, ActualsCsvFeed>();
        services.TryAddSingleton<IFeedParser<PosQuarterHourRow>, PosQuarterHourCsvFeed>();
        services.TryAddSingleton<IFeedParser<ParsedLiabilityRow>, LeaveLiabilityCsvFeed>();
        services.TryAddSingleton<IFeedParser<ParsedRequestRow>, LeaveRequestsCsvFeed>();
        services.TryAddSingleton<IFeedParser<ContactRow>, ContactsCsvFeed>();

        services.TryAddScoped<IFeedStore, EfFeedStore>();
        services.TryAddScoped<IUploadIngest, UploadIngest>();
        services.TryAddScoped<ICurveSource, CurveSource>();
    }
}
