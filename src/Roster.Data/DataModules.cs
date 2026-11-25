// New: registers the data layer (the DbContext, the store-scope rules, users, snapshots)
// and every data area's module, so adding an area never touches Program.cs. Mirrors
// src/Roster.Web/Features/FeatureModules.cs.

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data.Scope;
using Roster.Data.Snapshots;
using Roster.Data.Users;

namespace Roster.Data;

/// <summary>
/// One per data area (Services/, Feeds/, Seed/, Jobs/), found by reflection at start-up.
/// Needs a public parameterless constructor.
/// </summary>
public interface IDataModule
{
    void Register(IServiceCollection services, IConfiguration configuration);
}

/// <summary>Settings for the data layer (section "Roster:Data").</summary>
public sealed class RosterDataOptions
{
    public const string Section = "Roster:Data";

    /// <summary>The name under ConnectionStrings that holds the SQL Server connection string.</summary>
    public string ConnectionStringName { get; set; } = "Roster";

    /// <summary>Seconds before a command times out (uploads write large batches).</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public static class DataModules
{
    /// <summary>Every module in the assembly, in a fixed order (by full type name).</summary>
    public static IReadOnlyList<IDataModule> Discover(Assembly? assembly = null) =>
        (assembly ?? typeof(DataModules).Assembly)
            .GetTypes()
            .Where(t => typeof(IDataModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IDataModule)Activator.CreateInstance(t)!)
            .ToList();

    /// <summary>
    /// Call once in Program.cs. The connection string comes from ConnectionStrings:Roster
    /// (user secrets in development, an environment variable such as
    /// ConnectionStrings__Roster on a server); appsettings.json holds only a placeholder.
    /// </summary>
    public static IServiceCollection AddRosterData(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RosterDataOptions.Section).Get<RosterDataOptions>() ?? new RosterDataOptions();
        services.Configure<RosterDataOptions>(configuration.GetSection(RosterDataOptions.Section));
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

        return services.AddRosterData(builder =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"No connection string 'ConnectionStrings:{options.ConnectionStringName}'. Set it in user secrets " +
                    $"(dotnet user-secrets set \"ConnectionStrings:{options.ConnectionStringName}\" \"...\") or the environment " +
                    $"variable ConnectionStrings__{options.ConnectionStringName}.");
            builder.UseRosterSqlServer(connectionString, options.CommandTimeoutSeconds);
        }, configuration);
    }

    /// <summary>
    /// The same registrations with the database chosen by the caller (tests use SQLite).
    /// </summary>
    public static IServiceCollection AddRosterData(
        this IServiceCollection services, Action<DbContextOptionsBuilder<RosterDbContext>> configureDatabase, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            var builder = new DbContextOptionsBuilder<RosterDbContext>();
            configureDatabase(builder);
            return builder.Options;
        });

        // The current identity: the web layer sets it once it knows who is signed in (or
        // which kiosk token opened the page). Scoped: one per request / circuit.
        services.TryAddScoped<AccessContextAccessor>();
        services.TryAddScoped<IAccessContextAccessor>(sp => sp.GetRequiredService<AccessContextAccessor>());

        // Contexts: one per unit of work. Blazor components use the factory; request-bound
        // code may inject RosterDbContext (scoped) directly.
        services.TryAddScoped<IRosterDbContextFactory, RosterDbContextFactory>();
        services.TryAddScoped(sp => sp.GetRequiredService<IRosterDbContextFactory>().Create());

        services.TryAddScoped<IStoreScope, StoreScope>();
        services.TryAddScoped<IStoreDirectory, StoreDirectory>();

        services.Configure<UserDirectoryOptions>(configuration.GetSection(UserDirectoryOptions.Section));
        services.TryAddScoped<IUserDirectory, UserDirectory>();
        services.TryAddScoped<IUserAdmin, UserAdmin>();

        services.TryAddScoped<IPlanSnapshotStore, PlanSnapshotStore>();

        foreach (var module in Discover())
            module.Register(services, configuration);
        return services;
    }

    /// <summary>SQL Server, with the settings every environment uses.</summary>
    public static DbContextOptionsBuilder<RosterDbContext> UseRosterSqlServer(
        this DbContextOptionsBuilder<RosterDbContext> builder, string connectionString, int commandTimeoutSeconds = 120) =>
        builder.UseSqlServer(connectionString, sql =>
        {
            sql.CommandTimeout(commandTimeoutSeconds);
            // No retrying execution strategy: services open their own transactions
            // (BeginTransaction), which a retrying strategy refuses.
            sql.MigrationsHistoryTable("__ef_migrations_history");
        });
}

/// <summary>Creates contexts for the current identity, or for an explicit one.</summary>
public interface IRosterDbContextFactory
{
    /// <summary>A context for whoever is acting now (<see cref="IAccessContextAccessor"/>).</summary>
    RosterDbContext Create();

    /// <summary>A context for an explicit identity: a kiosk, a data feed, or a scheduled job.</summary>
    RosterDbContext Create(AccessContext access);

    /// <summary>
    /// Server code with no user behind it (the Supabase service role). Every use says why;
    /// prefer <see cref="Create()"/>.
    /// </summary>
    RosterDbContext CreateSystem(string reason);
}

internal sealed class RosterDbContextFactory(
    DbContextOptions<RosterDbContext> options, IAccessContextAccessor accessor, TimeProvider clock) : IRosterDbContextFactory
{
    public RosterDbContext Create() => new(options, accessor.Current, clock);

    public RosterDbContext Create(AccessContext access) => new(options, access, clock);

    public RosterDbContext CreateSystem(string reason) => new(options, AccessContext.System(reason), clock);
}
