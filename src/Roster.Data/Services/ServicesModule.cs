// New: registers the data services (Services/) with the data layer, as CONVENTIONS asks
// of every data area.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Data.Services.Notifications;
using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Services.Review;

namespace Roster.Data.Services;

/// <summary>
/// The database functions, the loaders around the engine and the review and approval
/// workflows. Scoped, like the contexts they open: one per request or circuit.
/// </summary>
public sealed class ServicesModule : IDataModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IDatabaseFunctions, DatabaseFunctionsService>();
        services.TryAddScoped<IRefData, RefDataReader>();
        services.TryAddScoped<IPayData, PayDataReader>();
        services.TryAddScoped<IPayCosts, PayCostsLoader>();
        services.TryAddScoped<IFreshRosterShifts, PendingFreshRosterShifts>();

        services.TryAddScoped<IStoreDayLoader, StoreDayLoader>();
        services.TryAddScoped<IStage1Feeds, PendingStage1Feeds>();
        services.TryAddScoped<IStage1Loader, Stage1Loader>();
        services.TryAddScoped<IDayCheckOptionsLoader, PendingDayCheckOptionsLoader>();
        services.TryAddScoped<IPlanShiftsLoader, PendingPlanShiftsLoader>();
        services.TryAddScoped<IPlanPeriods, PlanPeriods>();
        services.TryAddScoped<IHeldPlans, HeldPlans>();

        services.TryAddScoped<IReviewNotices, ReviewNoticesService>();
        services.TryAddScoped<IReviewGate, ReviewGateService>();
        services.TryAddScoped<IReviewReader, ReviewReaderService>();
        services.TryAddScoped<IPlanApproval, PlanApprovalService>();
        services.TryAddScoped<IRosterApproval, RosterApprovalService>();
        services.TryAddScoped<IRosterDrift, RosterDriftService>();
        services.TryAddScoped<INotificationReader, NotificationReaderService>();
    }
}
