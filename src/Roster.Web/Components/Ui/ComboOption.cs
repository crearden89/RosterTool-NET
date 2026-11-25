namespace Roster.Web.Components.Ui;

/// <summary>One name in a <see cref="NameCombobox"/>.</summary>
/// <param name="Hint">Shown dimmed after the label (e.g. the role).</param>
public sealed record ComboOption(string Id, string Label, string? Hint = null, bool Disabled = false, string? DisabledReason = null);
