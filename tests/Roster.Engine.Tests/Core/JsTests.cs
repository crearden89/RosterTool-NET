// The JavaScript-semantics layer, checked against values V8 itself produced
// (Core/js-vectors.mjs writes Core/js-vectors.json).

using System.Text.Json;
using Roster.Engine.Core;

namespace Roster.Engine.Tests.Core;

public class JsTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Core", "js-vectors.json"))).RootElement;

    [Fact]
    public void ToFixed_matches_V8_on_every_vector()
    {
        var failures = new List<string>();
        foreach (var v in Vectors.GetProperty("toFixed").EnumerateArray())
        {
            var x = v[0].GetDouble();
            var d = v[1].GetInt32();
            var want = v[2].GetString();
            var got = Js.ToFixed(x, d);
            if (got != want) failures.Add($"({x:R}).toFixed({d}) = {want}, got {got}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void Round_matches_Math_round()
    {
        foreach (var v in Vectors.GetProperty("round").EnumerateArray())
            Assert.Equal(v[1].GetDouble(), Js.Round(v[0].GetDouble()));
        Assert.Equal(3, Js.Round(2.5));
        Assert.Equal(-2, Js.Round(-2.5));
        Assert.Equal(0, Js.Round(0.49999999999999994));
        // -0 where Math.round gives -0 (toLocaleString prints it "-0").
        Assert.True(double.IsNegative(Js.Round(-0.004)));
        Assert.True(double.IsNegative(Js.Round(-0.5)));
        Assert.True(double.IsNegative(Js.Round(-0.0)));
        Assert.False(double.IsNegative(Js.Round(0.3)));
        Assert.Equal("-0", Js.ToLocaleString(Js.Round(-0.004)));
    }

    [Fact]
    public void NumberToString_matches_String()
    {
        var failures = new List<string>();
        foreach (var v in Vectors.GetProperty("str").EnumerateArray())
        {
            var got = Js.NumberToString(v[0].GetDouble());
            if (got != v[1].GetString()) failures.Add($"String({v[0].GetDouble():R}) = {v[1].GetString()}, got {got}");
        }
        Assert.Empty(failures);
        Assert.Equal("NaN", Js.NumberToString(double.NaN));
        Assert.Equal("Infinity", Js.NumberToString(double.PositiveInfinity));
        Assert.Equal("1e+21", Js.NumberToString(1e21));
        Assert.Equal("1e-7", Js.NumberToString(1e-7));
        Assert.Equal("0.000001", Js.NumberToString(0.000001));
    }

    [Fact]
    public void ToLocaleString_matches_Intl_in_en_US_and_en_AU()
    {
        var failures = new List<string>();
        foreach (var v in Vectors.GetProperty("toLocale").EnumerateArray())
        {
            var x = Js.Number(v[0].GetString());
            var (min, max) = (v[1].GetInt32(), v[2].GetInt32());
            var got = Js.ToLocaleString(x, max, min);
            if (got != v[4].GetString()) failures.Add($"({v[0].GetString()}).toLocaleString({v[3].GetString()}, {min}..{max}) = {v[4].GetString()}, got {got}");
        }
        Assert.Empty(failures);
        Assert.Equal("1,546,977", Js.ToLocaleString(1546977));
        Assert.Equal("2.68", Js.ToLocaleString(2.675, 2, 2));
        Assert.Equal("2.67", Js.ToFixed(2.675, 2));
        Assert.Equal("1.001", Js.ToLocaleString(1.0005));
        Assert.Equal("-0", Js.ToLocaleString(-0.4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Js.ToLocaleString(1, 1, 2));
    }

    [Fact]
    public void Trim_strips_what_JavaScript_strips()
    {
        var ws = new string([(char)0xFEFF, (char)0x2028, (char)0x3000, ' ', '\t']);
        Assert.Equal("a b", Js.Trim(ws + "a b" + ws));
        var nel = new string([(char)0x85]);
        Assert.Equal(nel + "a", Js.Trim(nel + "a")); // U+0085 is not JavaScript white space
        Assert.Equal(0, Js.Number(ws));
        var zs = new string([(char)0x1680, (char)0x2005, (char)0x202F, (char)0x205F, (char)0x3000]);
        Assert.Equal(12.5, Js.ParseFloat(zs + "12.5px"));
        Assert.Equal(12, Js.ParseInt(zs + "12px"));
        Assert.Equal(7, Js.Number(zs + "7" + zs));
    }

    [Fact]
    public void Sort_reproduces_V8_TimSort_even_for_inconsistent_comparators()
    {
        foreach (var s in Vectors.GetProperty("sorts").EnumerateArray())
        {
            var mode = s.GetProperty("mode").GetInt32();
            var keys = s.GetProperty("keys").EnumerateArray().Select(k => k.GetDouble()).ToArray();
            var want = s.GetProperty("order").EnumerateArray().Select(k => k.GetInt32()).ToList();
            var idx = Enumerable.Range(0, keys.Length).ToList();
            Js.Sort(idx, (a, b) => mode switch
            {
                0 => keys[a] - keys[b],
                1 => ((a * 31 + b * 17 + keys[a]) % 7) - 3,
                2 => keys[b] - keys[a],
                _ => keys[a] % 4 == 0 ? double.NaN : keys[a] - keys[b],
            });
            Assert.Equal(want, idx);
        }
    }

    [Fact]
    public void LocaleCompare_matches_V8_ICU()
    {
        var failures = new List<string>();
        foreach (var v in Vectors.GetProperty("localeCompare").EnumerateArray())
        {
            var a = v[0].GetString()!;
            var b = v[1].GetString()!;
            var want = v[2].GetInt32();
            var got = Js.LocaleCompare(a, b);
            if (got != want) failures.Add($"'{a}'.localeCompare('{b}') = {want}, got {got}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void Number_parses_like_JavaScript()
    {
        Assert.Equal(0, Js.Number(""));
        Assert.Equal(0, Js.Number("  "));
        Assert.Equal(7, Js.Number(" 7 "));
        Assert.Equal(0.5, Js.Number(".5"));
        Assert.Equal(5, Js.Number("5."));
        Assert.Equal(31, Js.Number("0x1F"));
        Assert.True(double.IsNaN(Js.Number("1,000")));
        Assert.True(double.IsNaN(Js.Number("12abc")));
        Assert.True(double.IsNaN(Js.Number(null)));
        Assert.Equal(double.NegativeInfinity, Js.Number("-Infinity"));
    }

    [Fact]
    public void Or_treats_zero_and_NaN_as_falsy()
    {
        Assert.Equal(5, 0.0.Or(5));
        Assert.Equal(5, double.NaN.Or(5));
        Assert.Equal(-1, (-1.0).Or(5));
    }

    [Fact]
    public void Min_and_max_follow_Math_min_and_max()
    {
        Assert.Equal(double.PositiveInfinity, Js.Min(Array.Empty<double>()));
        Assert.Equal(double.NegativeInfinity, Js.Max(Array.Empty<double>()));
        Assert.True(double.IsNaN(Js.Min(1, double.NaN)));
        Assert.Equal(1, Js.Min(3, 1, 2));
    }
}

public class JsMapTests
{
    [Fact]
    public void Iterates_in_insertion_order_and_keeps_a_reset_key_in_place()
    {
        var m = new JsMap<double, string>();
        m.Set(30, "a").Set(10, "b").Set(20, "c").Set(10, "B");
        Assert.Equal(new double[] { 30, 10, 20 }, m.Keys().ToArray());
        Assert.Equal("B", m.Get(10));
    }

    [Fact]
    public void A_deleted_and_re_added_key_goes_last()
    {
        var m = new JsMap<string, int>();
        m.Set("x", 1).Set("y", 2).Set("z", 3);
        m.Delete("x");
        m.Set("x", 4);
        Assert.Equal(["y", "z", "x"], m.Keys().ToArray());
        Assert.Equal(3, m.Size);
    }

    [Fact]
    public void An_iteration_sees_entries_added_during_it_and_skips_deleted_ones()
    {
        var m = new JsMap<int, int>();
        m.Set(1, 1).Set(2, 2).Set(3, 3);
        var seen = new List<int>();
        foreach (var (k, _) in m)
        {
            seen.Add(k);
            if (k == 1) m.Delete(2);
            if (k == 3) m.Set(4, 4);
        }
        Assert.Equal([1, 3, 4], seen);
    }

    [Fact]
    public void Set_iterates_in_insertion_order()
    {
        var s = new JsSet<double>([5, 3, 5, 1, 3]);
        Assert.Equal(new double[] { 5, 3, 1 }, s.ToArray());
    }
}
