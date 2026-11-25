// Port of src/lib/grid.ts.
// Shared 15-minute time-grid helpers. Times are handled as minutes-from-midnight
// internally; the roster grids everywhere align to the same :00/:15/:30/:45 grid.

namespace Roster.Engine.Core;

public static class Grid
{
    public const double BlockMin = 15;

    // Display grid for the planning/roster tables (spec 11.8): 5:00am → 9:45pm.
    public const double DisplayStart = 5 * 60;
    public const double DisplayEnd = 21 * 60 + 45;

    /// <summary>"HH:MM" or "HH:MM:SS" → minutes from midnight (the seconds are ignored).</summary>
    public static double TimeToMin(string t)
    {
        var parts = t.Split(':');
        return Js.Number(parts[0]) * 60 + Js.Number(parts.Length > 1 ? parts[1] : null);
    }

    // Rounded to the nearest minute first: a time worked out from decimal minutes
    // (a new starter's pallets take 26.6 minutes) would otherwise print as
    // "09:4.800000000000068". Whole minutes print as they always have.
    public static string MinToTime(double min)
    {
        var whole = Js.Round(min);
        var h = Math.Floor(whole / 60);
        var m = whole % 60;
        return $"{Js.PadStart(Js.NumberToString(h), 2, '0')}:{Js.PadStart(Js.NumberToString(m), 2, '0')}";
    }

    // SQL TIME value (HH:MM:SS) for a minutes value.
    public static string MinToSqlTime(double min) => $"{MinToTime(min)}:00";

    // All 15-min block start-times (minutes) in [start, end).
    public static List<double> BlocksBetween(double start, double end)
    {
        var out_ = new List<double>();
        for (var m = start; m < end; m += BlockMin) out_.Add(m);
        return out_;
    }

    // The fixed display columns for the planning/roster grid.
    public static List<double> DisplayBlocks() => BlocksBetween(DisplayStart, DisplayEnd + BlockMin);
}
