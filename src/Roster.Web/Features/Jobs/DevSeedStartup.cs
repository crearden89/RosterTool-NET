// New: the switch that runs the development seeder. scripts/seed.mjs was run by hand
// (node scripts/seed.mjs); here `dotnet run --project src/Roster.Web -- --seed` seeds the
// database before the app starts serving, and `-- --reseed` drops and rebuilds it first.
using Roster.Data.Seed;

namespace Roster.Web.Features.Jobs;

/// <summary>Settings for the seeder switch (section "Roster:Seed").</summary>
public sealed class DevSeedSettings
{
    public const string Section = "Roster:Seed";

    /// <summary>Seed at start-up without the command-line switch (for a launch profile).</summary>
    public bool OnStartup { get; set; }

    /// <summary>The seeder refuses to run outside the Development environment unless this is set.</summary>
    public bool AllowOutsideDevelopment { get; set; }

    /// <summary>Build the data around this day instead of today (yyyy-MM-dd).</summary>
    public DateOnly? Today { get; set; }
}

/// <summary>What the command line asked for.</summary>
public enum DevSeedRequest
{
    None,
    /// <summary>--seed: seed an empty database (migrating it first).</summary>
    Seed,
    /// <summary>--reseed: drop the database, rebuild it from the migrations, seed it.</summary>
    Reseed,
}

/// <summary>Runs the seeder at start-up when asked; does nothing otherwise.</summary>
public sealed class DevSeedStartup(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevSeedStartup> logger) : IHostedService
{
    /// <summary>The request in the process's command line or the settings.</summary>
    public static DevSeedRequest RequestFrom(IReadOnlyList<string> args, DevSeedSettings settings)
    {
        static bool Has(IReadOnlyList<string> a, string flag) =>
            a.Any(x => string.Equals(x, flag, StringComparison.OrdinalIgnoreCase));
        if (Has(args, "--reseed")) return DevSeedRequest.Reseed;
        if (Has(args, "--seed") || settings.OnStartup) return DevSeedRequest.Seed;
        return DevSeedRequest.None;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = configuration.GetSection(DevSeedSettings.Section).Get<DevSeedSettings>() ?? new DevSeedSettings();
        var request = RequestFrom(Environment.GetCommandLineArgs(), settings);
        if (request == DevSeedRequest.None) return;

        if (!environment.IsDevelopment() && !settings.AllowOutsideDevelopment)
        {
            logger.LogWarning(
                "Seeding skipped: the development seeder only runs in the Development environment (this is {Environment}).",
                environment.EnvironmentName);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IDevSeeder>();
        var report = await seeder.SeedAsync(
            new DevSeedOptions { Reset = request == DevSeedRequest.Reseed, Today = settings.Today },
            cancellationToken);

        logger.LogInformation("{Message}", report.Message);
        if (!report.Seeded) return;
        foreach (var login in report.Logins)
            logger.LogInformation("Seeded login {Username}: {Tier} {Scope}", login.Username, login.Tier, login.Scope);
        foreach (var link in report.KioskLinks)
            logger.LogInformation("Kiosk pages for {Store}: {Floor} and {Load}", link.StoreCode, link.FloorPath, link.LoadPath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
