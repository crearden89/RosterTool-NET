// Port of the pure part of src/lib/storeDay.ts (resolveStoreDay; the reads of
// the trading week, special hours and rule overrides are the data layer's).
//
// The single source of truth for a store's real staffed window on a given date.
// A special_trading_hours override (date-range) wins over the regular
// day-of-week store_trading_hours pattern. All downstream planning uses this so
// public-holiday / extended-hours dates are handled in exactly one place.
//
// Fields (minutes from midnight):
//  - TradingOpen/TradingClose: doors-open window (checkout demand lives here).
//  - EarliestStaffStart: pre-open prep floor (06:00, never later than open).
//    Load Running runs in [EarliestStaffStart, TradingOpen).
//  - StaffClose: the close-down, 30 minutes after close every day (CloseRules).

using System.Text.Json.Serialization;

namespace Roster.Engine.Core;

public sealed record StoreDay(bool Closed, double TradingOpen, double TradingClose, double EarliestStaffStart, double StaffClose)
{
    public static readonly StoreDay ClosedDay = new(true, 0, 0, 0, 0);
}

/// <summary>A row of store_trading_hours: the regular weekly pattern.</summary>
public sealed record TradingDayRow(
    [property: JsonPropertyName("day_of_week")] int DayOfWeek,
    [property: JsonPropertyName("open_time")] string? OpenTime,
    [property: JsonPropertyName("close_time")] string? CloseTime,
    [property: JsonPropertyName("closed")] bool Closed);

/// <summary>A row of special_trading_hours: a date range that overrides the pattern.</summary>
public sealed record SpecialHoursRow(
    [property: JsonPropertyName("start_date")] DateOnly StartDate,
    [property: JsonPropertyName("end_date")] DateOnly EndDate,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("open_time")] string? OpenTime,
    [property: JsonPropertyName("close_time")] string? CloseTime);

public static class StoreDays
{
    public const double PreOpenFloorMin = 6 * 60; // 06:00 - earliest a staff shift may begin

    public static StoreDay ResolveStoreDay(
        IReadOnlyList<TradingDayRow> week,
        IReadOnlyList<SpecialHoursRow> specials,
        IReadOnlyDictionary<string, double> overrides,
        DateOnly date)
    {
        // The close-down after close (decision 2), a setting on the Rules page (R15).
        var closeDown = overrides.TryGetValue("closeDownMin", out var cd) && double.IsFinite(cd) ? cd : CloseRules.CloseDownMin;

        // Special override wins when the date falls inside a defined range. If
        // several overlap, the most recently-defined (latest start) applies.
        var special = Js.Sorted(
            specials.Where(s => s.StartDate <= date && date <= s.EndDate),
            (a, b) => a.StartDate < b.StartDate ? 1 : -1).FirstOrDefault();

        bool closed;
        string? openT;
        string? closeT;
        if (special is not null)
        {
            closed = special.Closed;
            openT = special.OpenTime;
            closeT = special.CloseTime;
        }
        else
        {
            var th = week.FirstOrDefault(w => w.DayOfWeek == Period.DowMon0(date));
            closed = th?.Closed ?? true;
            openT = th?.OpenTime;
            closeT = th?.CloseTime;
        }

        if (closed || string.IsNullOrEmpty(openT) || string.IsNullOrEmpty(closeT)) return StoreDay.ClosedDay;

        var tradingOpen = Grid.TimeToMin(openT);
        var tradingClose = Grid.TimeToMin(closeT);
        if (tradingClose <= tradingOpen) return StoreDay.ClosedDay;

        // The close-down: End of Day, UKG End of Day, the tail of the cash-up and
        // the last of the cleaning - 30 minutes every day (owner decision 2).
        return new StoreDay(false, tradingOpen, tradingClose, Math.Min(tradingOpen, PreOpenFloorMin), tradingClose + closeDown);
    }
}
