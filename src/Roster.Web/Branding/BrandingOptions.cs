namespace Roster.Web.Branding;

/// <summary>
/// The <c>Branding</c> section of appsettings: the app's name and logo. The code
/// carries no company name or logo; these default to "Roster" and a neutral R
/// mark (wwwroot/brand/mark.svg), and an installation sets its own.
/// </summary>
public sealed class BrandingOptions
{
    public const string Section = "Branding";

    /// <summary>The name beside the logo, on the sign-in pages and in the browser tab.</summary>
    public string AppName { get; set; } = "Roster";

    /// <summary>The line under the name on the sign-in pages.</summary>
    public string Tagline { get; set; } = "Fortnightly planning & execution";

    /// <summary>The logo, as a path under wwwroot or a full URL. Shown at 28 to 56 px high.</summary>
    public string LogoUrl { get; set; } = "brand/mark.svg";

    /// <summary>The browser tab icon, as a path under wwwroot or a full URL.</summary>
    public string IconUrl { get; set; } = "brand/mark.svg";
}
