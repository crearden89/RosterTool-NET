namespace Roster.Web.Components.Ui;

/// <summary>Plain text as a render fragment, for the data records whose parts the TypeScript app types as ReactNode.</summary>
public static class Fragments
{
    public static RenderFragment Text(string text) => b => b.AddContent(0, text);

    public static RenderFragment? TextOrNull(string? text) => text is null ? null : Text(text);
}
