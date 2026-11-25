// New: lets the dotnet-ef tool build the model and the migrations without the web app.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Roster.Data.Scope;

namespace Roster.Data;

/// <summary>
/// Used only by dotnet-ef (migrations add / script / database update). The connection string
/// comes from the environment variable ConnectionStrings__Roster or the user secrets of this
/// project; generating a migration or a script needs no database at all, so a local
/// placeholder is used when neither is set.
/// </summary>
public sealed class RosterDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RosterDbContext>
{
    public RosterDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<RosterDbContextDesignTimeFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("Roster")
            ?? "Server=localhost;Database=Roster;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<RosterDbContext>().UseRosterSqlServer(connectionString).Options;
        return new RosterDbContext(options, AccessContext.System("dotnet-ef design time"));
    }
}
