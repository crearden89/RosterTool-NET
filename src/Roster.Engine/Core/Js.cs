// ─── JavaScript semantics, copied exactly ────────────────────────────────────
// The engine was written in TypeScript and its behaviour is the specification.
// Wherever JavaScript's own rules decide an answer - how a number rounds or
// prints, which of two equal elements a sort keeps first, how strings compare
// under a locale - the C# port calls this class instead of the .NET
// equivalent, because the .NET equivalents differ:
//
//   Math.round      ties go up (toward +Infinity); .NET's Math.Round is banker's.
//   toFixed         rounds the EXACT binary value, ties to the larger digit.
//   String(number)  shortest round-trip digits in ECMAScript's layout
//                   (1e+21, 0.000001, 1e-7) - .NET prints 1E+21 and 1E-06.
//   Array.sort      V8's TimSort: stable, and for a comparator that is not a
//                   consistent order the exact sequence of comparisons decides
//                   the result. List.Sort is an unstable introsort.
//   a || b          a number is falsy when it is 0 or NaN.
//   localeCompare   ICU collation (V8 and .NET both use ICU).
//
// Everything here is pure and allocation-light; the engine calls it in its
// innermost loops.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace Roster.Engine.Core;

public static class Js
{
    // ── Numbers ──────────────────────────────────────────────────────────────

    /// <summary>JavaScript <c>Math.round</c>: the nearest integer, ties toward +∞.</summary>
    public static double Round(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        var f = Math.Floor(x);
        // x - floor(x) is exact for every finite double.
        return x - f >= 0.5 ? f + 1 : f;
    }

    /// <summary>JavaScript truthiness of a number: false for 0, -0 and NaN.</summary>
    public static bool Truthy(double x) => x != 0 && !double.IsNaN(x);

    /// <summary>
    /// JavaScript <c>a || b</c> on numbers, the comparator idiom
    /// <c>x - y || p - q</c>. Both sides are evaluated (comparators are pure).
    /// </summary>
    public static double Or(this double a, double b) => a != 0 && !double.IsNaN(a) ? a : b;

    /// <summary>JavaScript <c>Math.sign</c>.</summary>
    public static double Sign(double x) => double.IsNaN(x) ? double.NaN : x > 0 ? 1 : x < 0 ? -1 : x;

    /// <summary>JavaScript <c>Math.min(...xs)</c>: +Infinity for none, NaN if any is NaN.</summary>
    public static double Min(IEnumerable<double> xs)
    {
        var m = double.PositiveInfinity;
        foreach (var x in xs)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x < m || (x == 0 && m == 0 && double.IsNegative(x))) m = x;
        }
        return m;
    }

    /// <summary>JavaScript <c>Math.max(...xs)</c>: -Infinity for none, NaN if any is NaN.</summary>
    public static double Max(IEnumerable<double> xs)
    {
        var m = double.NegativeInfinity;
        foreach (var x in xs)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x > m || (x == 0 && m == 0 && !double.IsNegative(x))) m = x;
        }
        return m;
    }

    public static double Min(params double[] xs) => Min((IEnumerable<double>)xs);
    public static double Max(params double[] xs) => Max((IEnumerable<double>)xs);

    /// <summary>JavaScript <c>Number(s)</c> for a string (StringToNumber).</summary>
    public static double Number(string? s)
    {
        if (s is null) return double.NaN; // Number(undefined)
        var t = Trim(s);
        if (t.Length == 0) return 0;
        switch (t)
        {
            case "Infinity":
            case "+Infinity":
                return double.PositiveInfinity;
            case "-Infinity":
                return double.NegativeInfinity;
        }
        if (t.Length > 2 && t[0] == '0' && (t[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B'))
        {
            var radix = t[1] is 'x' or 'X' ? 16 : t[1] is 'o' or 'O' ? 8 : 2;
            double v = 0;
            for (var i = 2; i < t.Length; i++)
            {
                var d = HexDigit(t[i]);
                if (d < 0 || d >= radix) return double.NaN;
                v = v * radix + d;
            }
            return v;
        }
        // A StrDecimalLiteral: sign, digits, optional point and exponent. No
        // thousands separators, no trailing garbage.
        var j = 0;
        if (j < t.Length && (t[j] == '+' || t[j] == '-')) j++;
        var digits = 0;
        while (j < t.Length && char.IsAsciiDigit(t[j])) { j++; digits++; }
        if (j < t.Length && t[j] == '.')
        {
            j++;
            while (j < t.Length && char.IsAsciiDigit(t[j])) { j++; digits++; }
        }
        if (digits == 0) return double.NaN;
        if (j < t.Length && (t[j] == 'e' || t[j] == 'E'))
        {
            j++;
            if (j < t.Length && (t[j] == '+' || t[j] == '-')) j++;
            var expDigits = 0;
            while (j < t.Length && char.IsAsciiDigit(t[j])) { j++; expDigits++; }
            if (expDigits == 0) return double.NaN;
        }
        if (j != t.Length) return double.NaN;
        return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>JavaScript <c>parseFloat(s)</c>: the longest decimal prefix after leading white space.</summary>
    public static double ParseFloat(string? s)
    {
        if (s is null) return double.NaN;
        var t = s.TrimStart(' ', '\t', '\n', '\v', '\f', '\r', '\u00A0', '\uFEFF', '\u2028', '\u2029');
        var j = 0;
        if (j < t.Length && (t[j] == '+' || t[j] == '-')) j++;
        if (string.CompareOrdinal(t, j, "Infinity", 0, 8) == 0) return t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        var digits = 0;
        while (j < t.Length && char.IsAsciiDigit(t[j])) { j++; digits++; }
        if (j < t.Length && t[j] == '.')
        {
            var k = j + 1;
            var frac = 0;
            while (k < t.Length && char.IsAsciiDigit(t[k])) { k++; frac++; }
            if (digits + frac > 0) { j = k; digits += frac; }
        }
        if (digits == 0) return double.NaN;
        if (j < t.Length && (t[j] == 'e' || t[j] == 'E'))
        {
            var k = j + 1;
            if (k < t.Length && (t[k] == '+' || t[k] == '-')) k++;
            var expDigits = 0;
            while (k < t.Length && char.IsAsciiDigit(t[k])) { k++; expDigits++; }
            if (expDigits > 0) j = k;
        }
        return double.Parse(t[..j], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// JavaScript <c>parseInt(s, radix)</c>: leading white space, a sign, "0x" for
    /// radix 16 (or radix 0/absent), then the longest run of digits valid in the
    /// radix. NaN when there are none.
    /// </summary>
    public static double ParseInt(string? s, int radix = 0)
    {
        if (s is null) return double.NaN;
        var t = s.TrimStart(' ', '\t', '\n', '\v', '\f', '\r', '\u00A0', '\uFEFF', '\u2028', '\u2029');
        var j = 0;
        var sign = 1;
        if (j < t.Length && (t[j] == '+' || t[j] == '-'))
        {
            if (t[j] == '-') sign = -1;
            j++;
        }
        var stripPrefix = true;
        if (radix != 0)
        {
            if (radix < 2 || radix > 36) return double.NaN;
            if (radix != 16) stripPrefix = false;
        }
        else radix = 10;
        if (stripPrefix && j + 1 < t.Length && t[j] == '0' && (t[j + 1] == 'x' || t[j + 1] == 'X'))
        {
            j += 2;
            radix = 16;
        }
        double v = 0;
        var any = false;
        while (j < t.Length)
        {
            var c = t[j];
            var d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
            if (d >= radix) break;
            v = v * radix + d;
            any = true;
            j++;
        }
        return any ? sign * v : double.NaN;
    }

    private static int HexDigit(char c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>
    /// JavaScript <c>Number.prototype.toFixed</c>: the exact binary value
    /// rounded to <paramref name="digits"/> places, an exact tie to the larger
    /// figure (so 2.5 → "3", -2.5 → "-3", and 1.005 → "1.00" because 1.005 is
    /// really 1.00499999999999989…).
    /// </summary>
    public static string ToFixed(double x, int digits)
    {
        if (digits < 0 || digits > 100) throw new ArgumentOutOfRangeException(nameof(digits));
        if (double.IsNaN(x)) return "NaN";
        if (Math.Abs(x) >= 1e21 || double.IsInfinity(x)) return NumberToString(x);
        var sign = "";
        if (x < 0)
        {
            sign = "-";
            x = -x;
        }
        // x = mantissa × 2^exponent, exactly.
        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        BigInteger n;
        if (exponent >= 0)
        {
            n = scaled << exponent;
        }
        else
        {
            var den = BigInteger.One << -exponent;
            n = BigInteger.DivRem(scaled, den, out var rem);
            if (rem * 2 >= den) n += 1;
        }
        var m = n.IsZero ? "0" : n.ToString(CultureInfo.InvariantCulture);
        if (digits != 0)
        {
            var k = m.Length;
            if (k <= digits)
            {
                m = new string('0', digits + 1 - k) + m;
                k = digits + 1;
            }
            m = m[..(k - digits)] + "." + m[(k - digits)..];
        }
        return sign + m;
    }

    /// <summary>
    /// JavaScript <c>String(x)</c> for a number (Number::toString): the
    /// shortest digits that round-trip, laid out the ECMAScript way.
    /// </summary>
    public static string NumberToString(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (x == 0) return "0";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x < 0) return "-" + NumberToString(-x);

        var s = ShortestDigits(x, out var n);
        var k = s.Length;
        if (k <= n && n <= 21) return s + new string('0', n - k);
        if (0 < n && n <= 21) return s[..n] + "." + s[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + s;
        var en = n - 1;
        var expText = (en >= 0 ? "+" : "-") + Math.Abs(en).ToString(CultureInfo.InvariantCulture);
        return k == 1 ? s + "e" + expText : s[..1] + "." + s[1..] + "e" + expText;
    }

    /// <summary>
    /// The shortest decimal digits that round-trip to a finite x &gt; 0, with no
    /// leading or trailing zeros, and where the point goes: x = 0.digits × 10^n.
    /// </summary>
    private static string ShortestDigits(double x, out int n)
    {
        // .NET's "R" is the shortest round-trip form: "1.5", "1E-07", "1.2345E+21".
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var mant = e < 0 ? r : r[..e];
        var exp10 = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mant.IndexOf('.');
        var intPart = point < 0 ? mant : mant[..point];
        var fracPart = point < 0 ? "" : mant[(point + 1)..];
        var all = intPart + fracPart;
        // The decimal point sits after intPart.Length digits, moved by exp10.
        n = intPart.Length + exp10;
        // Strip leading zeros (moving the point) and trailing zeros.
        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all[lead..];
        n -= lead;
        return all.TrimEnd('0');
    }

    /// <summary>
    /// JavaScript <c>x.toLocaleString(locale, { minimumFractionDigits,
    /// maximumFractionDigits })</c> for the English locales the app runs in
    /// (en-US, the server's default, and en-AU write numbers the same way):
    /// "1,234.56". Intl rounds the SHORTEST round-trip digits half away from
    /// zero, not the exact binary value as toFixed does, so 2.675 at two places
    /// is "2.68" (toFixed: "2.67") and 1.005 is "1.01". A negative figure keeps
    /// its sign when it rounds to nothing ("-0"); NaN is "NaN", Infinity "∞".
    /// The defaults are the plain <c>x.toLocaleString()</c>: up to 3 places.
    /// </summary>
    public static string ToLocaleString(double x, int maximumFractionDigits = 3, int minimumFractionDigits = 0)
    {
        // RangeError in JavaScript.
        if (minimumFractionDigits < 0 || maximumFractionDigits > 100 || minimumFractionDigits > maximumFractionDigits)
            throw new ArgumentOutOfRangeException(nameof(maximumFractionDigits), "fraction digits out of range");
        if (double.IsNaN(x)) return "NaN";
        var sign = double.IsNegative(x) ? "-" : "";
        if (double.IsInfinity(x)) return sign + "\u221E";

        // x = 0.digits × 10^n, then rounded at the maximumFractionDigits place.
        var digits = "";
        var n = 0;
        if (x != 0)
        {
            digits = ShortestDigits(Math.Abs(x), out n);
            var keep = n + maximumFractionDigits;
            if (keep < 0)
            {
                digits = "";
            }
            else if (keep < digits.Length)
            {
                var up = digits[keep] >= '5';
                digits = digits[..keep];
                if (up)
                {
                    var d = digits.ToCharArray();
                    var i = d.Length - 1;
                    while (i >= 0 && d[i] == '9') d[i--] = '0';
                    if (i >= 0)
                    {
                        d[i]++;
                        digits = new string(d);
                    }
                    else
                    {
                        digits = "1" + new string(d);
                        n++;
                    }
                }
            }
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) n = 0;
        }

        var intDigits = n <= 0 ? "0" : n >= digits.Length ? digits + new string('0', n - digits.Length) : digits[..n];
        var frac = n >= digits.Length ? "" : n < 0 ? new string('0', -n) + digits : digits[n..];
        if (frac.Length < minimumFractionDigits) frac += new string('0', minimumFractionDigits - frac.Length);

        var grouped = new StringBuilder(intDigits.Length + intDigits.Length / 3);
        for (var i = 0; i < intDigits.Length; i++)
        {
            if (i > 0 && (intDigits.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(intDigits[i]);
        }
        return sign + grouped + (frac.Length > 0 ? "." + frac : "");
    }

    // JavaScript's WhiteSpace and LineTerminator characters: what trim() strips
    // and \s matches (.NET's char.IsWhiteSpace differs on U+0085 and U+FEFF).
    private static readonly char[] WhiteSpaceChars =
    [
        (char)0x09, (char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x20, (char)0xA0, (char)0x1680,
        (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004, (char)0x2005, (char)0x2006,
        (char)0x2007, (char)0x2008, (char)0x2009, (char)0x200A, (char)0x2028, (char)0x2029, (char)0x202F,
        (char)0x205F, (char)0x3000, (char)0xFEFF,
    ];

    /// <summary>JavaScript <c>String.prototype.trim</c>.</summary>
    public static string Trim(string s) => s.Trim(WhiteSpaceChars);

    /// <summary>JavaScript <c>String.prototype.trimStart</c>.</summary>
    public static string TrimStart(string s) => s.TrimStart(WhiteSpaceChars);

    /// <summary>JavaScript <c>String.prototype.trimEnd</c>.</summary>
    public static string TrimEnd(string s) => s.TrimEnd(WhiteSpaceChars);

    /// <summary>JavaScript <c>String.prototype.padStart</c>.</summary>
    public static string PadStart(string s, int length, char fill = ' ') =>
        s.Length >= length ? s : new string(fill, length - s.Length) + s;

    /// <summary>JavaScript <c>String.prototype.padEnd</c>.</summary>
    public static string PadEnd(string s, int length, char fill = ' ') =>
        s.Length >= length ? s : s + new string(fill, length - s.Length);

    /// <summary>JavaScript <c>s.replace("a", "b")</c> with a string pattern: the FIRST occurrence only.</summary>
    public static string ReplaceFirst(string s, string find, string with)
    {
        var i = s.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + with + s[(i + find.Length)..];
    }

    // ── Strings ──────────────────────────────────────────────────────────────

    private static readonly CompareInfo Collator = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>
    /// JavaScript <c>a.localeCompare(b)</c> with no locale: ICU root collation,
    /// tertiary strength. -1, 0 or 1.
    /// </summary>
    public static int LocaleCompare(string a, string b) => Math.Sign(Collator.Compare(a, b, CompareOptions.None));

    /// <summary>JavaScript <c>a &lt; b</c> on strings: UTF-16 code unit order.</summary>
    public static int CompareStrings(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));

    // ── Sorting: V8's TimSort, comparison for comparison ─────────────────────

    /// <summary>
    /// <c>list.sort(cmp)</c>, in place, exactly as V8 does it. The comparator
    /// returns a number; only its sign is read, and NaN counts as 0.
    /// </summary>
    public static List<T> Sort<T>(List<T> list, Func<T, T, double> cmp)
    {
        if (list.Count < 2) return list;
        var arr = list.ToArray();
        new TimSort<T>(arr, cmp).Sort();
        for (var i = 0; i < arr.Length; i++) list[i] = arr[i];
        return list;
    }

    /// <summary><c>[...xs].sort(cmp)</c>: a sorted copy.</summary>
    public static List<T> Sorted<T>(IEnumerable<T> xs, Func<T, T, double> cmp) => Sort(new List<T>(xs), cmp);

    /// <summary><c>xs.sort()</c> on strings: UTF-16 code unit order.</summary>
    public static List<string> SortDefault(List<string> list) => Sort(list, (a, b) => CompareStrings(a, b));

    /// <summary><c>xs.sort()</c> on numbers: JavaScript compares their STRING forms.</summary>
    public static List<double> SortDefault(List<double> list) =>
        Sort(list, (a, b) => CompareStrings(NumberToString(a), NumberToString(b)));

    /// <summary>A comparator ascending by a numeric key: <c>(a, b) =&gt; key(a) - key(b)</c>.</summary>
    public static Func<T, T, double> By<T>(Func<T, double> key) => (a, b) => key(a) - key(b);

    private sealed class TimSort<T>
    {
        private const int MinGallopWins = 7;
        private readonly T[] _a;
        private readonly Func<T, T, double> _cmp;
        private int _minGallop = MinGallopWins;
        private readonly List<(int Base, int Length)> _runs = new();
        private T[] _temp = [];

        public TimSort(T[] a, Func<T, T, double> cmp)
        {
            _a = a;
            _cmp = cmp;
        }

        private double Compare(T x, T y)
        {
            var v = _cmp(x, y);
            return double.IsNaN(v) ? 0 : v;
        }

        public void Sort()
        {
            var length = _a.Length;
            if (length < 2) return;
            var remaining = length;
            var low = 0;
            var minRun = ComputeMinRunLength(remaining);
            while (remaining != 0)
            {
                var current = CountAndMakeRun(low, low + remaining);
                if (current < minRun)
                {
                    var forced = Math.Min(minRun, remaining);
                    BinaryInsertionSort(low, low + current, low + forced);
                    current = forced;
                }
                _runs.Add((low, current));
                MergeCollapse();
                low += current;
                remaining -= current;
            }
            MergeForceCollapse();
        }

        private static int ComputeMinRunLength(int n)
        {
            var r = 0;
            while (n >= 64)
            {
                r |= n & 1;
                n >>= 1;
            }
            return n + r;
        }

        private int CountAndMakeRun(int lowArg, int high)
        {
            var low = lowArg + 1;
            if (low == high) return 1;
            var runLength = 2;
            var elementLow = _a[low];
            var elementLowPre = _a[low - 1];
            var order = Compare(elementLow, elementLowPre);
            var isDescending = order < 0;
            var previous = elementLow;
            for (var idx = low + 1; idx < high; ++idx)
            {
                var current = _a[idx];
                order = Compare(current, previous);
                if (isDescending)
                {
                    if (order >= 0) break;
                }
                else if (order < 0)
                {
                    break;
                }
                previous = current;
                ++runLength;
            }
            if (isDescending) Array.Reverse(_a, lowArg, runLength);
            return runLength;
        }

        private void BinaryInsertionSort(int low, int startArg, int high)
        {
            var start = low == startArg ? startArg + 1 : startArg;
            for (; start < high; ++start)
            {
                var left = low;
                var right = start;
                var pivot = _a[start];
                while (left < right)
                {
                    var mid = left + ((right - left) >> 1);
                    var order = Compare(pivot, _a[mid]);
                    if (order < 0) right = mid;
                    else left = mid + 1;
                }
                for (var p = start; p > left; --p) _a[p] = _a[p - 1];
                _a[left] = pivot;
            }
        }

        private bool RunInvariantEstablished(int n)
        {
            if (n < 2) return true;
            return _runs[n - 2].Length > _runs[n - 1].Length + _runs[n].Length;
        }

        private void MergeCollapse()
        {
            while (_runs.Count > 1)
            {
                var n = _runs.Count - 2;
                if (!RunInvariantEstablished(n + 1) || !RunInvariantEstablished(n))
                {
                    if (_runs[n - 1].Length < _runs[n + 1].Length) --n;
                    MergeAt(n);
                }
                else if (_runs[n].Length <= _runs[n + 1].Length)
                {
                    MergeAt(n);
                }
                else
                {
                    break;
                }
            }
        }

        private void MergeForceCollapse()
        {
            while (_runs.Count > 1)
            {
                var n = _runs.Count - 2;
                if (n > 0 && _runs[n - 1].Length < _runs[n + 1].Length) --n;
                MergeAt(n);
            }
        }

        private void MergeAt(int i)
        {
            var stackSize = _runs.Count;
            var baseA = _runs[i].Base;
            var lengthA = _runs[i].Length;
            var baseB = _runs[i + 1].Base;
            var lengthB = _runs[i + 1].Length;
            _runs[i] = (baseA, lengthA + lengthB);
            if (i == stackSize - 3) _runs[i + 1] = _runs[i + 2];
            _runs.RemoveAt(_runs.Count - 1);

            var keyRight = _a[baseB];
            var k = GallopRight(_a, keyRight, baseA, lengthA, 0);
            baseA += k;
            lengthA -= k;
            if (lengthA == 0) return;

            var keyLeft = _a[baseA + lengthA - 1];
            lengthB = GallopLeft(_a, keyLeft, baseB, lengthB, lengthB - 1);
            if (lengthB == 0) return;

            if (lengthA <= lengthB) MergeLow(baseA, lengthA, baseB, lengthB);
            else MergeHigh(baseA, lengthA, baseB, lengthB);
        }

        private int GallopLeft(T[] array, T key, int @base, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            var baseHintElement = array[@base + hint];
            var order = Compare(baseHintElement, key);
            if (order < 0)
            {
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    order = Compare(array[@base + hint + offset], key);
                    if (order >= 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                lastOfs += hint;
                offset += hint;
            }
            else
            {
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    order = Compare(array[@base + hint - offset], key);
                    if (order < 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                order = Compare(array[@base + m], key);
                if (order < 0) lastOfs = m + 1;
                else offset = m;
            }
            return offset;
        }

        private int GallopRight(T[] array, T key, int @base, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            var baseHintElement = array[@base + hint];
            var order = Compare(key, baseHintElement);
            if (order < 0)
            {
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    order = Compare(key, array[@base + hint - offset]);
                    if (order >= 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }
            else
            {
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    order = Compare(key, array[@base + hint + offset]);
                    if (order < 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                lastOfs += hint;
                offset += hint;
            }
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                order = Compare(key, array[@base + m]);
                if (order < 0) offset = m;
                else lastOfs = m + 1;
            }
            return offset;
        }

        private T[] TempArray(int length)
        {
            if (_temp.Length < length) _temp = new T[Math.Max(length, _temp.Length * 2)];
            return _temp;
        }

        private void MergeLow(int baseA, int lengthA, int baseB, int lengthB)
        {
            var w = _a;
            var temp = TempArray(lengthA);
            Array.Copy(w, baseA, temp, 0, lengthA);
            var dest = baseA;
            var cursorTemp = 0;
            var cursorB = baseB;
            w[dest++] = w[cursorB++];

            if (--lengthB == 0) goto Succeed;
            if (lengthA == 1) goto CopyB;
            var minGallop = _minGallop;
            while (true)
            {
                var winsA = 0;
                var winsB = 0;
                while (true)
                {
                    var order = Compare(w[cursorB], temp[cursorTemp]);
                    if (order < 0)
                    {
                        w[dest++] = w[cursorB++];
                        ++winsB;
                        --lengthB;
                        winsA = 0;
                        if (lengthB == 0) goto Succeed;
                        if (winsB >= minGallop) break;
                    }
                    else
                    {
                        w[dest++] = temp[cursorTemp++];
                        ++winsA;
                        --lengthA;
                        winsB = 0;
                        if (lengthA == 1) goto CopyB;
                        if (winsA >= minGallop) break;
                    }
                }
                ++minGallop;
                var first = true;
                while (winsA >= MinGallopWins || winsB >= MinGallopWins || first)
                {
                    first = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    _minGallop = minGallop;
                    winsA = GallopRight(temp, w[cursorB], cursorTemp, lengthA, 0);
                    if (winsA > 0)
                    {
                        Array.Copy(temp, cursorTemp, w, dest, winsA);
                        dest += winsA;
                        cursorTemp += winsA;
                        lengthA -= winsA;
                        if (lengthA == 1) goto CopyB;
                        if (lengthA == 0) goto Succeed;
                    }
                    w[dest++] = w[cursorB++];
                    if (--lengthB == 0) goto Succeed;
                    winsB = GallopLeft(w, temp[cursorTemp], cursorB, lengthB, 0);
                    if (winsB > 0)
                    {
                        Array.Copy(w, cursorB, w, dest, winsB);
                        dest += winsB;
                        cursorB += winsB;
                        lengthB -= winsB;
                        if (lengthB == 0) goto Succeed;
                    }
                    w[dest++] = temp[cursorTemp++];
                    if (--lengthA == 1) goto CopyB;
                }
                ++minGallop;
                _minGallop = minGallop;
            }

        Succeed:
            if (lengthA > 0) Array.Copy(temp, cursorTemp, w, dest, lengthA);
            return;
        CopyB:
            Array.Copy(w, cursorB, w, dest, lengthB);
            w[dest + lengthB] = temp[cursorTemp];
        }

        private void MergeHigh(int baseA, int lengthA, int baseB, int lengthB)
        {
            var w = _a;
            var temp = TempArray(lengthB);
            Array.Copy(w, baseB, temp, 0, lengthB);
            var dest = baseB + lengthB - 1;
            var cursorTemp = lengthB - 1;
            var cursorA = baseA + lengthA - 1;
            w[dest--] = w[cursorA--];

            if (--lengthA == 0) goto Succeed;
            if (lengthB == 1) goto CopyA;
            var minGallop = _minGallop;
            while (true)
            {
                var winsA = 0;
                var winsB = 0;
                while (true)
                {
                    var order = Compare(temp[cursorTemp], w[cursorA]);
                    if (order < 0)
                    {
                        w[dest--] = w[cursorA--];
                        ++winsA;
                        --lengthA;
                        winsB = 0;
                        if (lengthA == 0) goto Succeed;
                        if (winsA >= minGallop) break;
                    }
                    else
                    {
                        w[dest--] = temp[cursorTemp--];
                        ++winsB;
                        --lengthB;
                        winsA = 0;
                        if (lengthB == 1) goto CopyA;
                        if (winsB >= minGallop) break;
                    }
                }
                ++minGallop;
                var first = true;
                while (winsA >= MinGallopWins || winsB >= MinGallopWins || first)
                {
                    first = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    _minGallop = minGallop;
                    var k = GallopRight(w, temp[cursorTemp], baseA, lengthA, lengthA - 1);
                    winsA = lengthA - k;
                    if (winsA > 0)
                    {
                        dest -= winsA;
                        cursorA -= winsA;
                        Array.Copy(w, cursorA + 1, w, dest + 1, winsA);
                        lengthA -= winsA;
                        if (lengthA == 0) goto Succeed;
                    }
                    w[dest--] = temp[cursorTemp--];
                    if (--lengthB == 1) goto CopyA;
                    k = GallopLeft(temp, w[cursorA], 0, lengthB, lengthB - 1);
                    winsB = lengthB - k;
                    if (winsB > 0)
                    {
                        dest -= winsB;
                        cursorTemp -= winsB;
                        Array.Copy(temp, cursorTemp + 1, w, dest + 1, winsB);
                        lengthB -= winsB;
                        if (lengthB == 1) goto CopyA;
                        if (lengthB == 0) goto Succeed;
                    }
                    w[dest--] = w[cursorA--];
                    if (--lengthA == 0) goto Succeed;
                }
                ++minGallop;
                _minGallop = minGallop;
            }

        Succeed:
            if (lengthB > 0) Array.Copy(temp, 0, w, dest - (lengthB - 1), lengthB);
            return;
        CopyA:
            dest -= lengthA;
            cursorA -= lengthA;
            Array.Copy(w, cursorA + 1, w, dest + 1, lengthA);
            w[dest] = temp[cursorTemp];
        }
    }

    // ── Misc ─────────────────────────────────────────────────────────────────

    /// <summary><c>xs.join(sep)</c> of numbers, each printed as JavaScript prints it.</summary>
    public static string Join(IEnumerable<double> xs, string sep = ",")
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var x in xs)
        {
            if (!first) sb.Append(sep);
            sb.Append(NumberToString(x));
            first = false;
        }
        return sb.ToString();
    }
}
