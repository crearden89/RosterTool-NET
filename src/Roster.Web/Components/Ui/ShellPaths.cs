namespace Roster.Web.Components.Ui;

/// <summary>
/// App addresses. The TypeScript app writes them from the site root ("/plan/sales");
/// here they are written relative to the page's base address, so the app also
/// works under a virtual directory (IIS) without a change.
/// </summary>
public static class ShellPaths
{
    /// <summary>"/plan/sales" becomes "plan/sales", resolved against the base address. Full URLs are left alone.</summary>
    public static string Href(string href) =>
        href.StartsWith("//", StringComparison.Ordinal) || href.Contains("://", StringComparison.Ordinal) ? href : href.TrimStart('/');

    /// <summary>The page's path from the base address, with a leading slash and no query or fragment ("/plan/sales").</summary>
    public static string PathOf(NavigationManager navigation)
    {
        var relative = navigation.ToBaseRelativePath(navigation.Uri);
        var cut = relative.IndexOfAny(['?', '#']);
        if (cut >= 0) relative = relative[..cut];
        return "/" + relative.TrimEnd('/');
    }
}
