// Port of src/lib/csv/posQuarterHour.ts.
// ─── POS Quarter-Hour statistics (the replacement Quarter-Hour source) ───────
// The "Hourly and Quarterly POS Statistics" export, one row per
// store / date / register type / metric / 15-minute interval:
//
//   StoreID , RegisterType1 , Period , DataGroup , Interval , value
//   G999    , M             , 1/08/2026 , 2. Customer Count , 08:30 - 08:45 , 7
//
// It carries FOUR weeks of real trade, where the file it replaces carried a
// pre-computed weekly allocation curve. This module parses it into one record
// per store/date/block with the MCO and SCO figures side by side;
// PosAllocation turns those into the weekly curve the Till engine reads.
//
// Keeping the raw counts (rather than only the derived curve) is deliberate:
// customers-per-block is a measure the roster tool has never had, and the
// derivation throws it away.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using static Roster.Data.Feeds.CsvParse;

namespace Roster.Data.Feeds;

public static class PosQhColumns
{
    public const string StoreCode = "StoreID";
    public const string RegisterType = "RegisterType1";
    public const string Period = "Period";
    public const string DataGroup = "DataGroup";
    public const string Interval = "Interval";
    public const string Value = "value";

    /// <summary><c>Object.values(POS_QH_COLUMNS)</c>, in order.</summary>
    public static readonly IReadOnlyList<string> All = [StoreCode, RegisterType, Period, DataGroup, Interval, Value];
}

/// <summary>
/// One store's quarter-hour of measured trade, manned (MCO) and self-checkout (SCO) side by
/// side. <paramref name="BlockTime"/> is "HH:MM:00", the interval's START. D2's refdata
/// builds these from pos_interval_stats with <paramref name="Row"/> 0 and the store id as
/// <paramref name="StoreCode"/>, exactly as the TypeScript does.
/// </summary>
public sealed record PosQuarterHourRow(
    int Row,
    string StoreCode,
    DateOnly Date,
    string BlockTime,
    double McoCustomers,
    double ScoCustomers,
    double McoItems,
    double ScoItems) : IFeedRow;

public static partial class PosQuarterHour
{
    /// <summary>Register types the export distinguishes: manned checkout vs self-checkout.</summary>
    public const string Manned = "M", SelfCheckout = "S";

    [GeneratedRegex(@"^([0-9]{1,2}):([0-9]{2})")]
    private static partial Regex IntervalStart();

    // The interval label is "HH:MM - HH:MM". Only the START matters: the export
    // closes each hour's last quarter at :59 ("08:45 - 08:59"), so the end time is
    // not a reliable 15-minute boundary.
    public static int? IntervalStartMinutes(string raw)
    {
        var m = IntervalStart().Match(raw);
        if (!m.Success) return null;
        var h = Int(m.Groups[1].Value);
        var min = Int(m.Groups[2].Value);
        if (h < 0 || h > 23 || min < 0 || min > 59) return null;
        return h * 60 + min;
    }

    internal static string Hhmm(int min) => $"{min / 60:00}:{min % 60:00}:00";

    // DataGroup values are prefixed and numbered by the reporting tool
    // ("2. Customer Count", "5. Items Scanned"); match on the words, not the number.
    private static bool IsCustomers(string g) => JsText.ContainsAsciiIgnoreCase(g, "customer");
    private static bool IsItems(string g) => JsText.ContainsAsciiIgnoreCase(g, "item");

    // One line of the file: a store, date, register type, measure and quarter-hour.
    private sealed record Reading(
        int Row, string StoreCode, DateOnly Date, int StartMin, string RegisterType, bool Customers, double Value) : IFeedRow;

    private sealed class Block(int row, string storeCode, DateOnly date, string blockTime)
    {
        public double McoCustomers, ScoCustomers, McoItems, ScoItems;

        public PosQuarterHourRow ToRow() =>
            new(row, storeCode, date, blockTime, McoCustomers, ScoCustomers, McoItems, ScoItems);
    }

    public static ValidationResult<PosQuarterHourRow> ValidatePosQuarterHour(string text, IReadOnlySet<string> validStoreCodes)
    {
        var (headers, rows) = ParseCsv(text);
        var errors = new List<RowError>();
        var fileErrors = new List<string>();

        var missing = MissingHeaders(headers, PosQhColumns.All);
        if (missing.Count > 0)
        {
            fileErrors.Add($"Missing required column(s): {string.Join(", ", missing)}");
            return new ValidationResult<PosQuarterHourRow>(rows.Count, [], errors, fileErrors);
        }

        // Each line is one reading: a store, date, register type, measure and
        // quarter-hour. They are collected first so a reading listed twice can be
        // settled before anything is added up - folded as they came, a repeated
        // line was silently counted twice.
        var readings = new List<Reading>();

        for (var i = 0; i < rows.Count; i++)
        {
            var raw = rows[i];
            var rowNo = i + 1;
            var storeCode = StoreIds.FormatStoreId(Js.Trim(Cell(raw, PosQhColumns.StoreCode) ?? ""));
            var bad = false;
            void Flag(string field, string message)
            {
                errors.Add(new RowError(rowNo, storeCode, field, message));
                bad = true;
            }

            if (storeCode.Length == 0) Flag(PosQhColumns.StoreCode, "Missing store ID");
            else if (!validStoreCodes.Contains(storeCode))
                Flag(PosQhColumns.StoreCode, $"Store ID '{storeCode}' not recognised");

            var iso = ToIsoDate(Cell(raw, PosQhColumns.Period));
            if (iso is null) Flag(PosQhColumns.Period, $"Unparseable date '{Cell(raw, PosQhColumns.Period) ?? ""}'");

            var rawInterval = Cell(raw, PosQhColumns.Interval) ?? "";
            var startMin = IntervalStartMinutes(rawInterval);
            if (startMin is null) Flag(PosQhColumns.Interval, $"Unparseable interval '{rawInterval}'");
            else if (startMin % 15 != 0)
                Flag(PosQhColumns.Interval, $"Interval '{rawInterval}' does not start on a 15-minute boundary");

            var rt = JsText.ToUpper(Js.Trim(Cell(raw, PosQhColumns.RegisterType) ?? ""));
            if (rt != Manned && rt != SelfCheckout)
                Flag(PosQhColumns.RegisterType, $"Unknown register type '{rt}' - expected M or S");

            var group = Cell(raw, PosQhColumns.DataGroup) ?? "";
            var customers = IsCustomers(group);
            var items = IsItems(group);
            if (!customers && !items)
                Flag(PosQhColumns.DataGroup, $"Unknown data group '{group}' - expected Customer Count or Items Scanned");

            // A blank value is a real reading: no trade in that block, not bad data.
            var rawValue = Js.Trim(Cell(raw, PosQhColumns.Value) ?? "");
            var value = rawValue.Length == 0 ? 0 : ParseDecimal(rawValue);
            if (!double.IsFinite(value)) Flag(PosQhColumns.Value, $"Invalid number '{rawValue}'");
            else if (value < 0) Flag(PosQhColumns.Value, $"Negative value not allowed ({Js.NumberToString(value)})");

            if (bad || startMin is null || iso is null) continue;
            readings.Add(new Reading(rowNo, storeCode, Date(iso), startMin.Value, rt, customers, value));
        }

        var dupes = Duplicates.CheckDuplicates(
            readings,
            r => $"{r.StoreCode}|{r.Date.ToIso()}|{r.StartMin}|{r.RegisterType}|{(r.Customers ? "customers" : "items")}",
            r => $"{r.StoreCode} {r.Date.ToIso()} {Hhmm(r.StartMin)[..5]} " +
                 $"(register type {r.RegisterType}, {(r.Customers ? "customer count" : "items scanned")})",
            PosQhColumns.Interval);
        errors.AddRange(dupes.Errors);

        // Fold the long format into one record per store/date/block.
        var byKey = new OrderedDictionary<string, Block>(StringComparer.Ordinal);
        var datesByStore = new OrderedDictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        foreach (var r in dupes.Rows)
        {
            var key = $"{r.StoreCode}|{r.Date.ToIso()}|{r.StartMin}";
            if (!byKey.TryGetValue(key, out var rec))
                byKey[key] = rec = new Block(r.Row, r.StoreCode, r.Date, Hhmm(r.StartMin));
            if (r.Customers)
            {
                if (r.RegisterType == Manned) rec.McoCustomers += r.Value;
                else rec.ScoCustomers += r.Value;
            }
            else
            {
                if (r.RegisterType == Manned) rec.McoItems += r.Value;
                else rec.ScoItems += r.Value;
            }

            if (!datesByStore.TryGetValue(r.StoreCode, out var seen)) datesByStore[r.StoreCode] = seen = [];
            seen.Add(r.Date);
        }

        // The curve is a weekly pattern, so a store needs every weekday represented
        // at least once or that day would have no shape to plan against.
        foreach (var (store, dates) in datesByStore)
        {
            var dows = dates.Select(Period.DowMon0).ToHashSet();
            if (dows.Count < 7)
                fileErrors.Add(
                    $"{store}: only {dows.Count} of the 7 weekdays appear in this file - every weekday needs at least one trading date to build its curve.");
        }

        return new ValidationResult<PosQuarterHourRow>(
            rows.Count,
            byKey.Values.Select(b => b.ToRow()).ToList(),
            Duplicates.ByRow(errors),
            fileErrors,
            dupes.Collapsed);
    }
}
