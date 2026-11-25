// New: reads the five-field cron expressions vercel.json used ("0 8 * * 3"), so the
// in-process scheduler fires at the same minutes Vercel Cron did. Times are UTC, as
// Vercel's were.
using System.Globalization;

namespace Roster.Web.Features.Jobs;

/// <summary>
/// A standard five-field cron expression: minute, hour, day of month, month, day of week
/// (0 or 7 = Sunday). Each field takes <c>*</c>, numbers, ranges (<c>1-5</c>), lists
/// (<c>1,3</c>) and steps (<c>*/15</c>, <c>0-30/10</c>). When both day fields are
/// restricted a day matches either of them, as in standard cron. Names (MON, JAN) and
/// the extensions (L, W, #, ?) are not supported.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth; // index 1..31
    private readonly bool[] _months; // index 1..12
    private readonly bool[] _daysOfWeek; // index 0..6, Sunday = 0
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronSchedule(string expression, bool[] minutes, bool[] hours, bool[] dom, bool[] months, bool[] dow, bool domRestricted, bool dowRestricted)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = dom;
        _months = months;
        _daysOfWeek = dow;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    /// <summary>The expression as given.</summary>
    public string Expression { get; }

    public override string ToString() => Expression;

    /// <summary>Parse an expression; throws <see cref="FormatException"/> when it is not one.</summary>
    public static CronSchedule Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException($"A cron expression has five fields (minute hour day month weekday): \"{expression}\".");

        var minutes = ParseField(fields[0], 0, 59, "minute", out _);
        var hours = ParseField(fields[1], 0, 23, "hour", out _);
        var dom = ParseField(fields[2], 1, 31, "day of month", out var domRestricted);
        var months = ParseField(fields[3], 1, 12, "month", out _);
        var dowRaw = ParseField(fields[4], 0, 7, "day of week", out var dowRestricted);
        var dow = new bool[7];
        for (var i = 0; i <= 7; i++)
            if (dowRaw[i]) dow[i % 7] = true; // 7 is Sunday too

        return new CronSchedule(expression, minutes, hours, dom, months, dow, domRestricted, dowRestricted);
    }

    /// <summary>Parse an expression, or false when it is not one.</summary>
    public static bool TryParse(string? expression, out CronSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try
        {
            schedule = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Does the schedule fire in this minute (UTC)?</summary>
    public bool Matches(DateTimeOffset instant)
    {
        var t = instant.ToUniversalTime();
        return _minutes[t.Minute] && _hours[t.Hour] && _months[t.Month] && DayMatches(t);
    }

    /// <summary>
    /// The first minute strictly after <paramref name="after"/> at which the schedule fires,
    /// in UTC; null when it never fires (for example 31 February).
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var u = after.ToUniversalTime();
        // The next whole minute after `after`.
        var t = new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, u.Minute, 0, TimeSpan.Zero).AddMinutes(1);
        var limit = t.AddYears(5); // every valid expression fires within four years (29 February)

        while (t <= limit)
        {
            if (!_months[t.Month])
            {
                t = new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
                continue;
            }
            if (!_hours[t.Hour])
            {
                t = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                continue;
            }
            if (!_minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }
            return t;
        }
        return null;
    }

    private bool DayMatches(DateTimeOffset t)
    {
        var dom = _daysOfMonth[t.Day];
        var dow = _daysOfWeek[(int)t.DayOfWeek];
        // Standard cron: when both day fields are restricted, either may match.
        if (_domRestricted && _dowRestricted) return dom || dow;
        if (_domRestricted) return dom;
        if (_dowRestricted) return dow;
        return true;
    }

    private static bool[] ParseField(string field, int min, int max, string name, out bool restricted)
    {
        var allowed = new bool[max + 1];
        // As in Vixie cron, a field starting with "*" (including "*/2") does not restrict
        // the day for the either-day-field rule.
        restricted = !field.StartsWith('*');
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) throw Bad(field, name);
            var stepAt = part.IndexOf('/');
            var range = stepAt < 0 ? part : part[..stepAt];
            var step = 1;
            if (stepAt >= 0)
            {
                if (!int.TryParse(part[(stepAt + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    throw Bad(field, name);
            }

            int from, to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (dash < 0)
                {
                    from = Number(range, field, name);
                    // "5/15" means from 5 to the end in steps of 15.
                    to = stepAt >= 0 ? max : from;
                }
                else
                {
                    from = Number(range[..dash], field, name);
                    to = Number(range[(dash + 1)..], field, name);
                }
            }
            if (from < min || to > max || from > to) throw Bad(field, name);
            for (var v = from; v <= to; v += step) allowed[v] = true;
        }
        return allowed;
    }

    private static int Number(string text, string field, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw Bad(field, name);

    private static FormatException Bad(string field, string name) =>
        new($"\"{field}\" is not a valid cron {name} field.");
}
