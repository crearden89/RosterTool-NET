namespace Roster.Web.Components.Ui;

/// <summary>One check in a <see cref="CheckList"/>: its severity, the check in words and any explanation under it.</summary>
public sealed record CheckRow(string Key, CheckTone Tone, RenderFragment Message, RenderFragment? Detail = null)
{
    /// <summary>A check written as plain text.</summary>
    public static CheckRow Of(string key, CheckTone tone, string message, string? detail = null) =>
        new(key, tone, Fragments.Text(message), Fragments.TextOrNull(detail));
}
