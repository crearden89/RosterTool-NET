// New: registers the development seeder (the switch that runs it is in
// src/Roster.Web/Features/Jobs/DevSeedStartup.cs).
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Data.Seed;

public sealed class SeedModule : IDataModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IDevSeeder, DevSeeder>();
    }
}
