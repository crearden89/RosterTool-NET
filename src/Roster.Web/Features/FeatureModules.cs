// New: lets each page area register its own services and endpoints, so adding an area
// never touches Program.cs (owned by W; see CONVENTIONS.md).

using System.Reflection;

namespace Roster.Web.Features;

/// <summary>
/// One per page area, in <c>Features/&lt;Area&gt;/&lt;Area&gt;Module.cs</c>. Found by reflection at
/// start-up. Needs a public parameterless constructor.
/// </summary>
public interface IFeatureModule
{
    /// <summary>Register the area's services (page services, options, hosted jobs).</summary>
    void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the area's HTTP endpoints, if it has any (exports, kiosk feeds).</summary>
    void Map(IEndpointRouteBuilder endpoints) { }
}

public static class FeatureModules
{
    /// <summary>Every module in the assembly, in a fixed order (by full type name).</summary>
    public static IReadOnlyList<IFeatureModule> Discover(Assembly? assembly = null) =>
        (assembly ?? typeof(FeatureModules).Assembly)
            .GetTypes()
            .Where(t => typeof(IFeatureModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IFeatureModule)Activator.CreateInstance(t)!)
            .ToList();

    /// <summary>Call once in Program.cs, after the shared services are registered.</summary>
    public static IServiceCollection AddFeatureModules(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var module in Discover())
            module.Register(services, configuration);
        return services;
    }

    /// <summary>Call once in Program.cs, after routing and authorization are set up.</summary>
    public static IEndpointRouteBuilder MapFeatureModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var module in Discover())
            module.Map(endpoints);
        return endpoints;
    }
}
