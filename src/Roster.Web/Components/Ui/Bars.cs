// Port of src/components/ui/Bars.tsx.

namespace Roster.Web.Components.Ui;

/// <summary>Which half of a <see cref="CentredBar"/> a difference is drawn in.</summary>
public enum BarSide { Above, Below, None }

/// <summary>Text colour inside a <see cref="SplitBar"/> block: white on a dark fill, ink on a light one.</summary>
public enum SplitInk { White, Ink }

/// <param name="Share">Its share of the whole, 0-1.</param>
/// <param name="Title">Hover text: the part's name, amount and share.</param>
public sealed record SplitPart(string Key, string Label, double Share, string Colour, SplitInk Ink, string Title);
