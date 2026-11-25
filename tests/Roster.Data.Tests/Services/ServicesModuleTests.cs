// New: the data services' registrations build - every service can be made, with no
// dependency cycle - as the web host's start-up validation demands.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roster.Data.Services;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Data.Services.Planning;
using Roster.Data.Services.Review;
using Roster.Data.Services.SubTasks;
using Microsoft.EntityFrameworkCore;

namespace Roster.Data.Tests.Services;

public class ServicesModuleTests
{
    [Fact]
    public void Every_data_service_resolves_with_no_cycle()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        // Building the services opens no connection.
        services.AddRosterData(b => b.UseSqlite("Data Source=:memory:"), new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = sp.CreateScope();
        foreach (var t in new[]
        {
            typeof(IDatabaseFunctions), typeof(IHeldPlans), typeof(IPlanShiftsLoader), typeof(IStage1Loader), typeof(ITradeCurveSource),
            typeof(ISubtaskLabels), typeof(IPublishedSubtasks), typeof(IReviewGate), typeof(IReviewReader), typeof(IRosterApproval),
            typeof(IPlanApproval), typeof(IRosterDrift),
        })
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(t));
        Assert.Contains(DataModules.Discover(), m => m is ServicesModule);
    }
}
