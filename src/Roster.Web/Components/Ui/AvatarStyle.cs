// Port of src/components/ui/primitives.tsx.

using System.Text.RegularExpressions;

namespace Roster.Web.Components.Ui;

/// <summary>The avatar's initials and colour (<c>initialsOf</c>, <c>avatarColour</c>).</summary>
public static partial class AvatarStyle
{
    // White initials pass 4.5:1 on every one of these.
    private static readonly string[] Colours = ["#4f46e5", "#0369a1", "#b45309", "#047857", "#be185d", "#6d28d9", "#0f766e", "#be123c", "#1d4ed8"];

    [GeneratedRegex(@"^\d+[A-Za-z]*-")]
    private static partial Regex RankPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// "Hannah Lee" gives "HL". A UKG rank prefix ("4-Nye, Harper") is skipped
    /// and "Surname, First" reads first name first.
    /// </summary>
    public static string InitialsOf(string name)
    {
        var clean = RankPrefix().Replace(name, "").Trim();
        var ordered = clean.Contains(',') ? string.Join(' ', clean.Split(',').Reverse()) : clean;
        var words = Spaces().Split(ordered).Where(w => w.Length > 0).ToArray();
        var first = words.Length > 0 ? words[0][0].ToString() : "?";
        var last = words.Length > 1 ? words[^1][0].ToString() : "";
        return (first + last).ToUpperInvariant();
    }

    /// <summary>The same seed always gets the same colour: a 32-bit hash (h * 31 + c) of it.</summary>
    public static string ColourFor(string seed)
    {
        uint h = 0;
        foreach (var c in seed) h = unchecked(h * 31 + c);
        return Colours[h % (uint)Colours.Length];
    }

    /// <summary>The initials' size for an avatar of <paramref name="size"/> px (36% of it, rounded).</summary>
    public static int FontSize(int size) => (int)Math.Round(size * 0.36, MidpointRounding.AwayFromZero);
}
