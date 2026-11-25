// New: the web app's start-up - the services and the request pipeline the TypeScript
// app got from Next.js and Supabase (src/app/layout.tsx, src/middleware.ts, src/lib/supabase).

using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Web.Auth;
using Roster.Web.Components;
using Roster.Web.Components.Layout;
using Roster.Web.Components.Ui;
using Roster.Web.Features;

namespace Roster.Web;

public static class ShellSetup
{
    /// <summary>The shell's services: Blazor, sign-in, the shared components' helpers, every page area.</summary>
    public static WebApplicationBuilder AddRosterWeb(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.Section));

        builder.AddRosterAuth();

        services.AddScoped<RosterJs>();
        services.AddScoped<PortalService>();
        services.AddScoped<ShellEvents>();
        services.AddScoped<StoreSelection>();
        services.TryAddScoped<IUnreadNotifications, PlaceholderUnreadNotifications>();

        // Every page area registers its own services (Features/<Area>/<Area>Module.cs).
        services.AddFeatureModules(builder.Configuration);
        return builder;
    }

    /// <summary>The request pipeline and the endpoints.</summary>
    public static WebApplication UseRosterWeb(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        // Australian formats everywhere (dates, numbers, currency), whatever the server's own culture.
        app.UseRequestLocalization(new RequestLocalizationOptions()
            .SetDefaultCulture("en-AU")
            .AddSupportedCultures("en-AU")
            .AddSupportedUICultures("en-AU"));

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapAccountEndpoints();
        if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.Mode == AuthMode.Development)
            app.MapDevSignIn();
        app.MapFeatureModules();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
