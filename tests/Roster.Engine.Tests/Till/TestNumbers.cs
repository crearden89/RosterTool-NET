// New: the vitest number matchers the ported tests use.

namespace Roster.Engine.Tests.Till;

internal static class TestNumbers
{
    /// <summary>vitest toBeCloseTo: |expected - actual| &lt; 10^-digits / 2.</summary>
    public static void Close(double expected, double actual, int digits = 2) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"expected {expected} to be close to {actual} ({digits} digits)");

    /// <summary>The tests' round2: Math.round(n * 100) / 100.</summary>
    public static double Round2(double n) => Roster.Engine.Core.Js.Round(n * 100) / 100;
}
