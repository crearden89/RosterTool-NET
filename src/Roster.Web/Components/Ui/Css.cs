// Port of src/components/ui/Bars.tsx (clamp01).

using System.Globalization;

namespace Roster.Web.Components.Ui;

/// <summary>Numbers written into inline styles: always with a full stop, whatever the server's culture.</summary>
internal static class Css
{
    public static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A fraction held to 0..1 (non-finite reads as 0): the bars' <c>clamp01</c>.</summary>
    public static double Clamp01(double n) => double.IsFinite(n) ? Math.Min(1, Math.Max(0, n)) : 0;
}
