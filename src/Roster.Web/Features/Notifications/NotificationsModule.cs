// New: registers the Notifications page's service (see CONVENTIONS.md, "Pages").

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Notifications;

public sealed class NotificationsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        // The stand-in until the data layer's notification reads land.
        services.TryAddSingleton<FakeNotificationStore>();
        services.TryAddScoped<INotificationService, FakeNotificationService>();
    }
}
