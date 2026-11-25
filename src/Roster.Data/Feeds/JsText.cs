// New: JavaScript string rules the CSV ports depend on that .NET spells differently
// (String.prototype.trim is Roster.Engine.Core.Js.Trim).
//
//   /abc/i                 without the u flag folds only ASCII letters for an ASCII pattern
//                          (a non-ASCII character never canonicalises to an ASCII one), so
//                          "true", "customer" and "item" match by ASCII case alone.

namespace Roster.Data.Feeds;

internal static class JsText
{
    /// <summary>JavaScript <c>s.toLowerCase()</c> (locale-independent).</summary>
    public static string ToLower(string s) => s.ToLowerInvariant();

    /// <summary>JavaScript <c>s.toUpperCase()</c> (locale-independent).</summary>
    public static string ToUpper(string s) => s.ToUpperInvariant();

    private static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    /// <summary><c>/^literal$/i.test(s)</c> for an ASCII literal.</summary>
    public static bool EqualsAsciiIgnoreCase(string s, string literal)
    {
        if (s.Length != literal.Length) return false;
        for (var i = 0; i < s.Length; i++)
            if (AsciiLower(s[i]) != AsciiLower(literal[i])) return false;
        return true;
    }

    /// <summary><c>/literal/i.test(s)</c> for an ASCII literal: found anywhere in <paramref name="s"/>.</summary>
    public static bool ContainsAsciiIgnoreCase(string s, string literal)
    {
        for (var i = 0; i + literal.Length <= s.Length; i++)
        {
            var j = 0;
            while (j < literal.Length && AsciiLower(s[i + j]) == AsciiLower(literal[j])) j++;
            if (j == literal.Length) return true;
        }
        return false;
    }

    /// <summary>JavaScript <c>s.substring(start, end)</c>: clamps, and swaps a reversed pair.</summary>
    public static string Substring(string s, int start, int end)
    {
        start = Math.Clamp(start, 0, s.Length);
        end = Math.Clamp(end, 0, s.Length);
        if (start > end) (start, end) = (end, start);
        return s[start..end];
    }

    /// <summary>JavaScript <c>s.substring(start)</c>.</summary>
    public static string Substring(string s, int start) => Substring(s, start, s.Length);

    /// <summary>JavaScript <c>s.substr(start, length)</c> for a non-negative start.</summary>
    public static string Substr(string s, int start, int length)
    {
        if (start >= s.Length || length <= 0) return "";
        return s.Substring(start, Math.Min(length, s.Length - start));
    }

    /// <summary>JavaScript <c>s[i] === c</c>: false past either end.</summary>
    public static bool CharIs(string s, int i, char c) => i >= 0 && i < s.Length && s[i] == c;
}
