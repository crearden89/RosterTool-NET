// Port of src/lib/csv/parse.ts.

using System.Globalization;
using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Data.Feeds;

/// <summary>A CSV's header row and its data rows.</summary>
public sealed record ParsedCsv(IReadOnlyList<string> Headers, IReadOnlyList<CsvRow> Rows);

public static partial class CsvParse
{
    // Thin papaparse wrapper: parses to string cells, header row required. Trims
    // header + cell whitespace so column maps match regardless of stray spaces.
    public static ParsedCsv ParseCsv(string text)
    {
        var result = Papa.Parse(text, new Papa.Config(TransformHeader: Js.Trim, Transform: Js.Trim));
        return new ParsedCsv(result.Fields, result.Data);
    }

    // Check that all required headers are present (case-insensitive). Returns the
    // list of missing headers (empty when all present).
    public static List<string> MissingHeaders(IReadOnlyList<string> headers, IReadOnlyList<string> required)
    {
        var lower = new HashSet<string>(headers.Select(JsText.ToLower), StringComparer.Ordinal);
        return required.Where(r => !lower.Contains(JsText.ToLower(r))).ToList();
    }

    // Case-insensitive cell lookup helper (headers may vary in case).
    public static string? Cell(CsvRow row, string header)
    {
        if (row.Has(header)) return row[header];
        var wanted = JsText.ToLower(header);
        foreach (var k in row.Keys)
            if (JsText.ToLower(k) == wanted) return row[k];
        return null;
    }

    // A decimal as exports write one: optional sign, digits (thousands may be
    // grouped with commas), optional fraction, optional exponent ("1.2E-05", which
    // reporting tools emit for tiny values). JavaScript's Number() is far looser:
    // it reads "0x10" as 16 and "Infinity" as a number, and stripping every comma
    // first turned "1,5" into 15 - all of which loaded silently as hours.
    [GeneratedRegex(@"^[+-]?(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)?(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z")]
    private static partial Regex DecimalPattern();

    /// <summary>Parse a decimal string strictly; NaN for anything that is not one.</summary>
    public static double ParseDecimal(string raw)
    {
        var s = Js.Trim(raw);
        if (!s.Any(char.IsAsciiDigit) || !DecimalPattern().IsMatch(s)) return double.NaN;
        var n = Js.Number(s.Replace(",", "", StringComparison.Ordinal));
        return double.IsFinite(n) ? n : double.NaN;
    }

    // Parse a numeric cell; returns NaN for blank/invalid so callers can flag it.
    public static double Num(CsvRow row, string header)
    {
        var raw = Cell(row, header);
        if (string.IsNullOrEmpty(raw)) return double.NaN;
        return ParseDecimal(raw);
    }

    /// <summary>
    /// ISO yyyy-MM-dd for a REAL calendar date, else null. "31/02/2026" used to
    /// pass validation and then be refused by Postgres partway through the write.
    /// Years outside 1900-2100 are typos in every file this app reads.
    /// </summary>
    public static string? IsoDate(int y, int m, int d)
    {
        if (y < 1900 || y > 2100) return null;
        if (m < 1 || m > 12) return null;
        var daysInMonth = DateTime.DaysInMonth(y, m);
        if (d < 1 || d > daysInMonth) return null;
        return $"{y.ToString(CultureInfo.InvariantCulture)}-{m:00}-{d:00}";
    }

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})\z")]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"^([0-9]{1,2})/([0-9]{1,2})/([0-9]{4})\z")]
    private static partial Regex DmyPattern();

    // Normalise a date cell to ISO yyyy-MM-dd; returns null if unparseable or not
    // a real date. Accepts yyyy-MM-dd, dd/MM/yyyy, and d/M/yyyy.
    public static string? ToIsoDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var s = Js.Trim(raw);
        var iso = IsoPattern().Match(s);
        if (iso.Success) return IsoDate(Int(iso.Groups[1].Value), Int(iso.Groups[2].Value), Int(iso.Groups[3].Value));
        var m = DmyPattern().Match(s);
        if (m.Success) return IsoDate(Int(m.Groups[3].Value), Int(m.Groups[2].Value), Int(m.Groups[1].Value));
        return null;
    }

    // Parse a boolean-ish cell (1/0, true/false, yes/no, y/n).
    public static bool ToBool(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return IsYes(Js.Trim(raw));
    }

    // OPTIONAL boolean columns carried in the export alongside the required set.
    // Tri-state: null when the column is absent or the cell blank - an old file
    // simply doesn't carry the flag, which must not read as FALSE.
    public static bool? OptionalBool(CsvRow row, IReadOnlyList<string> headers, string header)
    {
        var wanted = JsText.ToLower(header);
        if (!headers.Any(h => JsText.ToLower(h) == wanted)) return null;
        var raw = Cell(row, header);
        if (raw is null) return null;
        raw = Js.Trim(raw);
        if (raw.Length == 0) return null;
        return IsYes(raw);
    }

    // /^(1|true|yes|y)$/i
    private static bool IsYes(string s) =>
        s == "1" || JsText.EqualsAsciiIgnoreCase(s, "true") || JsText.EqualsAsciiIgnoreCase(s, "yes") ||
        JsText.EqualsAsciiIgnoreCase(s, "y");

    /// <summary>A digits-only regex group (at most four ASCII digits) as a number.</summary>
    internal static int Int(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>An ISO date this module produced, as a date.</summary>
    internal static DateOnly Date(string iso) => DateOnlyExtensions.ParseIso(iso);
}
