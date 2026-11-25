// Port of src/components/ui/primitives.tsx, src/components/ui/PageChips.tsx and src/components/ui/CheckList.tsx (the tone tables).

namespace Roster.Web.Components.Ui;

/// <summary>
/// A <see cref="Pill"/>'s colour: navy for strong facts (restricted tasks), soft
/// for neutral ones, gold for an adjustment, amber for leave, green and red for
/// status.
/// </summary>
public enum PillTone { Navy, Soft, Violet, Gold, Amber, Green, Red, Grey }

/// <summary>A <see cref="Badge"/>'s colour (the roster grid's role badges).</summary>
public enum BadgeTone { Navy, Blue, Green, Amber, Red, Grey }

/// <summary>A <see cref="StatusChip"/>'s colour.</summary>
public enum StatusTone { Green, Navy, Soft, Amber, Red }

/// <summary>The icon in a <see cref="StatusChip"/>.</summary>
public enum StatusIcon { Check, Lock, None }

/// <summary>A <see cref="FigureChip"/>'s figure: plain, good (green) or bad (red).</summary>
public enum FigureTone { Plain, Good, Bad }

/// <summary>A check's severity (<see cref="CheckList"/>), graded by the caller.</summary>
public enum CheckTone { Error, Warning, Ok, Note }

/// <summary>The size of a <see cref="SegmentedControl{T}"/>.</summary>
public enum ControlSize { Sm, Md }

internal static class ToneClasses
{
    public static string Pill(PillTone tone) => tone switch
    {
        PillTone.Navy => "bg-[var(--ui-ink)] text-white",
        PillTone.Soft => "bg-[var(--ui-lavender)] text-[var(--ui-ink)]",
        PillTone.Violet => "bg-[var(--ui-violet)] text-white",
        PillTone.Gold => "bg-[var(--ui-gold)] text-[var(--ui-ink)]",
        PillTone.Amber => "bg-[var(--ui-amber-bg)] text-[var(--ui-amber-ink)]",
        PillTone.Green => "bg-green-100 text-green-800",
        PillTone.Red => "bg-[var(--brand-red-tint)] text-[var(--ui-bad)]",
        PillTone.Grey => "bg-gray-100 text-gray-700",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    public static string Badge(BadgeTone tone) => tone switch
    {
        BadgeTone.Navy => "bg-[var(--brand-navy-tint)] text-[var(--brand-navy)]",
        BadgeTone.Blue => "bg-[var(--brand-blue-tint)] text-[var(--brand-navy)]",
        BadgeTone.Green => "bg-green-100 text-green-800",
        BadgeTone.Amber => "bg-[var(--brand-orange-tint)] text-[var(--brand-orange)]",
        BadgeTone.Red => "bg-[var(--brand-red-tint)] text-[var(--brand-red)]",
        BadgeTone.Grey => "bg-gray-100 text-gray-700",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    public static string Status(StatusTone tone) => tone switch
    {
        StatusTone.Green => "bg-green-100 text-green-800",
        StatusTone.Navy => "bg-[var(--ui-ink)] text-white",
        StatusTone.Soft => "bg-[var(--ui-lavender)] text-[var(--ui-ink)]",
        StatusTone.Amber => "bg-[var(--ui-amber-bg)] text-[var(--ui-amber-ink)]",
        StatusTone.Red => "bg-[var(--brand-red-tint)] text-[var(--ui-bad)]",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };
}

/// <summary>A <see cref="FormMessage"/>: a confirmation, or the reason a save was refused.</summary>
public enum FormMessageTone { Ok, Error }
