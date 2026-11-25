namespace Roster.Web.Components.Ui;

/// <summary>One of <see cref="FilterChips{TKey}"/>'s toggles: "Errors" with its count.</summary>
public sealed record FilterChip<TKey>(TKey Key, string Label, int? Count = null);

/// <summary>One segment of a <see cref="SegmentedControl{T}"/>: its value and its words.</summary>
public sealed record SegmentOption<T>(T Value, string Label);
