// Generated from lucide-static 1.25.0 (ISC licence, https://lucide.dev): the icons the
// TypeScript app imports from lucide-react, drawn as lucide-react draws them. To add one,
// copy its child elements from the lucide-static icon-nodes.json.
namespace Roster.Web.Components.Ui;

/// <summary>The icons <see cref="Icon"/> can draw (lucide names, in PascalCase).</summary>
public enum IconName
{
    Plus,
    ChevronRight,
    ChevronLeft,
    ChevronDown,
    X,
    Check,
    Lock,
    TriangleAlert,
    Info,
    ArrowUp,
    ArrowDown,
    ArrowRight,
    CircleAlert,
    CircleCheck,
    OctagonAlert,
    CalendarDays,
    UserRound,
    Undo2,
    Trash2,
    RefreshCw,
    Printer,
    MessageSquare,
    FileSpreadsheet,
    Download,
    Columns3,
    ClipboardCheck,
    Bell,
    Menu,
}

internal static class LucideIcons
{
    /// <summary>The lucide class name ("lucide-check") and the elements inside the 24 x 24 svg.</summary>
    public static (string Slug, string Body) For(IconName name) => name switch
    {
        IconName.Plus => ("plus", @"<path d=""M5 12h14"" /><path d=""M12 5v14"" />"),
        IconName.ChevronRight => ("chevron-right", @"<path d=""m9 18 6-6-6-6"" />"),
        IconName.ChevronLeft => ("chevron-left", @"<path d=""m15 18-6-6 6-6"" />"),
        IconName.ChevronDown => ("chevron-down", @"<path d=""m6 9 6 6 6-6"" />"),
        IconName.X => ("x", @"<path d=""M18 6 6 18"" /><path d=""m6 6 12 12"" />"),
        IconName.Check => ("check", @"<path d=""M20 6 9 17l-5-5"" />"),
        IconName.Lock => ("lock", @"<rect width=""18"" height=""11"" x=""3"" y=""11"" rx=""2"" ry=""2"" /><path d=""M7 11V7a5 5 0 0 1 10 0v4"" />"),
        IconName.TriangleAlert => ("triangle-alert", @"<path d=""m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"" /><path d=""M12 9v4"" /><path d=""M12 17h.01"" />"),
        IconName.Info => ("info", @"<circle cx=""12"" cy=""12"" r=""10"" /><path d=""M12 16v-4"" /><path d=""M12 8h.01"" />"),
        IconName.ArrowUp => ("arrow-up", @"<path d=""m5 12 7-7 7 7"" /><path d=""M12 19V5"" />"),
        IconName.ArrowDown => ("arrow-down", @"<path d=""M12 5v14"" /><path d=""m19 12-7 7-7-7"" />"),
        IconName.ArrowRight => ("arrow-right", @"<path d=""M5 12h14"" /><path d=""m12 5 7 7-7 7"" />"),
        IconName.CircleAlert => ("circle-alert", @"<circle cx=""12"" cy=""12"" r=""10"" /><line x1=""12"" x2=""12"" y1=""8"" y2=""12"" /><line x1=""12"" x2=""12.01"" y1=""16"" y2=""16"" />"),
        IconName.CircleCheck => ("circle-check", @"<circle cx=""12"" cy=""12"" r=""10"" /><path d=""m9 12 2 2 4-4"" />"),
        IconName.OctagonAlert => ("octagon-alert", @"<path d=""M12 16h.01"" /><path d=""M12 8v4"" /><path d=""M15.312 2a2 2 0 0 1 1.414.586l4.688 4.688A2 2 0 0 1 22 8.688v6.624a2 2 0 0 1-.586 1.414l-4.688 4.688a2 2 0 0 1-1.414.586H8.688a2 2 0 0 1-1.414-.586l-4.688-4.688A2 2 0 0 1 2 15.312V8.688a2 2 0 0 1 .586-1.414l4.688-4.688A2 2 0 0 1 8.688 2z"" />"),
        IconName.CalendarDays => ("calendar-days", @"<path d=""M8 2v4"" /><path d=""M16 2v4"" /><rect width=""18"" height=""18"" x=""3"" y=""4"" rx=""2"" /><path d=""M3 10h18"" /><path d=""M8 14h.01"" /><path d=""M12 14h.01"" /><path d=""M16 14h.01"" /><path d=""M8 18h.01"" /><path d=""M12 18h.01"" /><path d=""M16 18h.01"" />"),
        IconName.UserRound => ("user-round", @"<circle cx=""12"" cy=""8"" r=""5"" /><path d=""M20 21a8 8 0 0 0-16 0"" />"),
        IconName.Undo2 => ("undo-2", @"<path d=""M9 14 4 9l5-5"" /><path d=""M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"" />"),
        IconName.Trash2 => ("trash-2", @"<path d=""M10 11v6"" /><path d=""M14 11v6"" /><path d=""M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"" /><path d=""M3 6h18"" /><path d=""M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"" />"),
        IconName.RefreshCw => ("refresh-cw", @"<path d=""M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"" /><path d=""M21 3v5h-5"" /><path d=""M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"" /><path d=""M8 16H3v5"" />"),
        IconName.Printer => ("printer", @"<path d=""M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"" /><path d=""M6 9V3a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6"" /><rect x=""6"" y=""14"" width=""12"" height=""8"" rx=""1"" />"),
        IconName.MessageSquare => ("message-square", @"<path d=""M22 17a2 2 0 0 1-2 2H6.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 2 21.286V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2z"" />"),
        IconName.FileSpreadsheet => ("file-spreadsheet", @"<path d=""M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z"" /><path d=""M14 2v5a1 1 0 0 0 1 1h5"" /><path d=""M8 13h2"" /><path d=""M14 13h2"" /><path d=""M8 17h2"" /><path d=""M14 17h2"" />"),
        IconName.Download => ("download", @"<path d=""M12 15V3"" /><path d=""M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"" /><path d=""m7 10 5 5 5-5"" />"),
        IconName.Columns3 => ("columns-3", @"<rect width=""18"" height=""18"" x=""3"" y=""3"" rx=""2"" /><path d=""M9 3v18"" /><path d=""M15 3v18"" />"),
        IconName.ClipboardCheck => ("clipboard-check", @"<rect width=""8"" height=""4"" x=""8"" y=""2"" rx=""1"" ry=""1"" /><path d=""M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"" /><path d=""m9 14 2 2 4-4"" />"),
        IconName.Bell => ("bell", @"<path d=""M10.268 21a2 2 0 0 0 3.464 0"" /><path d=""M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"" />"),
        IconName.Menu => ("menu", @"<path d=""M4 5h16"" /><path d=""M4 12h16"" /><path d=""M4 19h16"" />"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
