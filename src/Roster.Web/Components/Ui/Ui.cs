namespace Roster.Web.Components.Ui;

/// <summary>The kinds of button (<see cref="Ui.ButtonClass"/>).</summary>
public enum ButtonKind
{
    /// <summary>Navy: the save inside a form.</summary>
    Primary,

    /// <summary>White outlined: page actions and row actions.</summary>
    Secondary,

    /// <summary>Text only: Close, Cancel and the like.</summary>
    Quiet,

    /// <summary>Red outlined: removing.</summary>
    Danger,

    /// <summary>The warm orange: the save that changes the plan's inputs, and the settings save bar.</summary>
    Accent,
}

public enum ButtonSize
{
    Sm,
    Md,
}

/// <summary>
/// The class strings shared with plain elements, for the places a component
/// would get in the way (a link, a label wrapping its input, a button inside a
/// table cell): the TypeScript app's <c>buttonClass</c>, <c>fieldClass</c>,
/// <c>fieldLabelClass</c>, <c>linkClass</c>, <c>focusRing</c>, <c>table</c> and
/// <c>compactField</c>, class for class.
/// </summary>
/// <remarks>
/// <para>How to port a page: keep the TypeScript page's class strings as they
/// are (Tailwind compiles them, see Styles/app.css), rename <c>--aldi-</c> to
/// <c>--brand-</c>, and call these where it calls the helpers:
/// <c>class="@Ui.ButtonClass(ButtonKind.Primary)"</c>,
/// <c>class="@($"w-full {Ui.FieldClass}")"</c>.</para>
/// <para>Class names must be written out whole, never built from pieces
/// (<c>$"bg-{tone}"</c>), or the Tailwind CLI cannot see them.</para>
/// </remarks>
public static class Ui
{
    /// <summary>The violet outline every interactive piece shows on keyboard focus.</summary>
    public const string FocusRing =
        "focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--ui-violet)]";

    /// <summary>A button's classes: primary navy, secondary white outlined, quiet, danger, accent orange.</summary>
    public static string ButtonClass(ButtonKind kind = ButtonKind.Secondary, ButtonSize size = ButtonSize.Md)
    {
        const string @base = "inline-flex items-center justify-center gap-1.5 rounded-xl font-semibold whitespace-nowrap transition-colors disabled:cursor-not-allowed disabled:opacity-50 " + FocusRing;
        var sizes = size == ButtonSize.Sm ? "px-3 py-1.5 text-xs" : "px-4 py-2 text-sm";
        var kinds = kind switch
        {
            ButtonKind.Primary => "bg-[var(--ui-ink)] text-white hover:bg-[#14147a]",
            ButtonKind.Secondary => "border border-[var(--ui-control)] bg-white text-[var(--ui-ink)] hover:border-[var(--ui-ink)]/40 hover:bg-[var(--ui-lavender)]",
            ButtonKind.Quiet => "text-[var(--ui-ink)] hover:bg-[var(--ui-lavender)]",
            ButtonKind.Danger => "border border-[var(--ui-bad)]/40 bg-white text-[var(--ui-bad)] hover:bg-[var(--brand-red-tint)]",
            ButtonKind.Accent => "bg-[var(--brand-orange)] text-white hover:brightness-95",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return $"{@base} {sizes} {kinds}";
    }

    /// <summary>Text inputs, selects and date pickers.</summary>
    public const string FieldClass =
        "rounded-xl border border-[var(--ui-control)] bg-white px-3 py-2 text-sm text-[var(--ui-ink)] placeholder:text-[var(--ui-faint)] disabled:bg-[var(--ui-canvas)] disabled:text-[var(--ui-muted)] " + FocusRing;

    /// <summary>The small label above a field.</summary>
    public const string FieldLabelClass = "mb-1 block text-xs font-semibold text-[var(--ui-muted)]";

    /// <summary>A link inside running text.</summary>
    public const string LinkClass =
        "rounded-sm font-semibold text-[var(--ui-violet)] underline-offset-2 hover:underline " + FocusRing;

    /// <summary>A smaller field for inputs inside a table row or a dense form (SettingsShell's <c>compactField</c>).</summary>
    public const string CompactField =
        "rounded-lg border border-[var(--ui-control)] bg-white px-2 py-1.5 text-sm text-[var(--ui-ink)] placeholder:text-[var(--ui-faint)] disabled:bg-[var(--ui-canvas)] disabled:text-[var(--ui-muted)] " + FocusRing;

    /// <summary>
    /// Tables for the pages that stay tabular: a white rounded frame, a light head
    /// of small grey capitals, quiet row rules.
    /// </summary>
    public static class Table
    {
        public const string Frame = "overflow-x-auto rounded-2xl border border-[var(--ui-line)] bg-white";

        /// <summary>The table element itself (the TypeScript app's <c>table.table</c>).</summary>
        public const string Element = "w-full text-sm text-[var(--ui-ink)]";

        /// <summary>The head row group: capitals and colour come from .ui-thead in app.css.</summary>
        public const string Head = "ui-thead";

        public const string Th = "px-3 py-2.5 whitespace-nowrap";
        public const string Row = "border-t border-[var(--ui-line)]";
        public const string Td = "px-3 py-2";
        public const string Empty = "px-3 py-8 text-center text-sm text-[var(--ui-muted)]";
    }

    /// <summary>Joins class strings, skipping empty ones.</summary>
    public static string Cx(params string?[] parts) =>
        string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}
