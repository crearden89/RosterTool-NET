// Port of src/lib/payroll/rateGrid.ts.
// Pure helpers for the pay-rate grid.
//
// The grid used to save by writing EVERY position × 24 hours for the day type,
// substituting 0 for anything missing from its in-memory map. That made a
// truncated read destructive: cells the page never loaded were written back as
// real zeros. Saving now sends only what actually changed, so a cell the user
// never touched can never be overwritten.

namespace Roster.Engine.Pay;

public sealed record RateCellChange(string DayType, double Hour, string Role, double Rate);

public static class RateGrid
{
    public static string RateKey(string dayType, double hour, string role) => $"{dayType}:{Core.Js.NumberToString(hour)}:{role}";

    // Cells whose edited value differs from what the server sent. A cell with no
    // server row is only included once it holds a real (non-zero) rate - there is
    // nothing to record about an untouched blank.
    public static List<RateCellChange> ChangedRateCells(
        IReadOnlyDictionary<string, double> baseline,
        IReadOnlyDictionary<string, double?> values,
        string dayType,
        IEnumerable<string> roles,
        IEnumerable<double> hours)
    {
        var out_ = new List<RateCellChange>();
        var roleList = roles.ToList();
        foreach (var hour in hours)
        {
            foreach (var role in roleList)
            {
                var key = RateKey(dayType, hour, role);
                if (!values.TryGetValue(key, out var edited) || edited is null || !double.IsFinite(edited.Value)) continue;
                if (!baseline.TryGetValue(key, out var original))
                {
                    if (edited.Value != 0) out_.Add(new RateCellChange(dayType, hour, role, edited.Value));
                    continue;
                }
                if (Math.Abs(original - edited.Value) > 1e-9) out_.Add(new RateCellChange(dayType, hour, role, edited.Value));
            }
        }
        return out_;
    }
}
