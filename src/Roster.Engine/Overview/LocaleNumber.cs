// New: a stand-in for JavaScript's Number.prototype.toLocaleString in the server's default
// locale (en-US), until Core's Js helper for it lands (PROGRESS: P4 NEED E1). Internal on
// purpose: callers go through Money, so switching over touches this file only.

using System.Globalization;
using System.Text;
using Roster.Engine.Core;

namespace Roster.Engine.Overview;

internal static class LocaleNumber
{
    /// <summary>
    /// <c>x.toLocaleString(undefined, { maximumFractionDigits, minimumFractionDigits })</c>, en-US:
    /// Intl rounds the SHORTEST round-trip decimal digits of x half away from zero (so
    /// 2.675 → "2.68", where toFixed gives "2.67"), groups the integer part in threes with
    /// commas and keeps the sign of a negative zero ("-0").
    /// </summary>
    public static string ToLocaleString(double x, int maximumFractionDigits = 3, int minimumFractionDigits = 0)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "∞";
        if (double.IsNegativeInfinity(x)) return "-∞";
        var negative = x < 0 || (x == 0 && double.IsNegative(x));

        // The shortest round-trip digits d1d2… and the point: |x| = 0.d1d2… × 10^point.
        var (digits, point) = ShortestDigits(Math.Abs(x));

        // Round half away from zero, keeping `keep` leading digits.
        var keep = point + maximumFractionDigits;
        var kept = new List<int>();
        if (keep >= digits.Length)
        {
            foreach (var c in digits) kept.Add(c - '0');
        }
        else if (keep >= 0)
        {
            for (var i = 0; i < keep; i++) kept.Add(digits[i] - '0');
            if (digits[keep] >= '5')
            {
                var i = kept.Count - 1;
                while (i >= 0 && kept[i] == 9)
                {
                    kept[i] = 0;
                    i--;
                }
                if (i >= 0) kept[i]++;
                else
                {
                    kept.Insert(0, 1);
                    point++;
                }
            }
        }
        // else: every digit sits below the last kept place, so it rounds to zero.

        var integer = new StringBuilder();
        for (var i = 0; i < point; i++) integer.Append(i < kept.Count ? (char)('0' + kept[i]) : '0');
        var frac = new StringBuilder();
        if (point < 0) frac.Append('0', -point);
        for (var i = Math.Max(point, 0); i < kept.Count; i++) frac.Append((char)('0' + kept[i]));
        var fraction = frac.Length > maximumFractionDigits ? frac.ToString(0, maximumFractionDigits) : frac.ToString();
        fraction = fraction.TrimEnd('0');
        if (fraction.Length < minimumFractionDigits) fraction = fraction.PadRight(minimumFractionDigits, '0');
        var whole = integer.ToString().TrimStart('0');
        if (whole.Length == 0) whole = "0";

        var grouped = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(whole[i]);
        }
        return (negative ? "-" : "") + grouped + (fraction.Length > 0 ? "." + fraction : "");
    }

    private static (string Digits, int Point) ShortestDigits(double abs)
    {
        if (abs == 0) return ("0", 1);
        // .NET's "R" is the shortest round-trip form: "1546977.5", "1E-07", "1.23456E+24".
        var r = abs.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var mant = e < 0 ? r : r[..e];
        var exp10 = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mant.IndexOf('.');
        var intPart = dot < 0 ? mant : mant[..dot];
        var all = intPart + (dot < 0 ? "" : mant[(dot + 1)..]);
        var point = intPart.Length + exp10;
        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all[lead..].TrimEnd('0');
        point -= lead;
        return (all.Length == 0 ? "0" : all, point);
    }
}

/// <summary>The money and count formats the Dashboard and Analysis share.</summary>
public static class Money
{
    /// <summary><c>"$" + n.toLocaleString(undefined, { maximumFractionDigits: 0 })</c>: "$1,546,977".</summary>
    public static string Money0(double n) => "$" + LocaleNumber.ToLocaleString(n, 0);

    /// <summary><c>Math.round(n).toLocaleString()</c>: whole numbers with separators.</summary>
    public static string Count0(double n) => LocaleNumber.ToLocaleString(Js.Round(n));

    /// <summary><c>n.toLocaleString()</c> (up to three decimals).</summary>
    public static string Locale(double n) => LocaleNumber.ToLocaleString(n);
}
