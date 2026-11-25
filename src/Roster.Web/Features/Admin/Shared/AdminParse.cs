// New: the date and time text the admin forms send ("2026-04-25", "08:30"), read
// the way Postgres read them from the TypeScript app's inserts. A value Postgres
// would refuse is refused with its message.

using System.Globalization;

namespace Roster.Web.Features.Admin.Shared;

public static class AdminParse
{
    public static bool TryDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    public static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "HH:mm:ss", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Postgres's refusal of a date it cannot read.</summary>
    public static string BadDate(string? s) => $"invalid input syntax for type date: \"{s}\"";

    /// <summary>Postgres's refusal of a time it cannot read.</summary>
    public static string BadTime(string? s) => $"invalid input syntax for type time: \"{s}\"";

    /// <summary>"2026-04-25", as the date inputs and the TypeScript app's ISO dates write it.</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"08:30": the time as the pages show it (Postgres "08:30:00".slice(0, 5)).</summary>
    public static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
