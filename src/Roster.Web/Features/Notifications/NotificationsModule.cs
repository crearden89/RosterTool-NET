// New: registers the Notifications page's service, which the menu's unread count also
// reads (see CONVENTIONS.md, "Pages").

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Notifications;

public sealed class NotificationsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.TryAddScoped<INotificationService, NotificationService>();
}
