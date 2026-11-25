// New: vitest's matchers where xUnit's differ.

namespace Roster.Engine.Tests.Planning;

internal static class Vitest
{
    /// <summary>
    /// expect(actual).toBeCloseTo(expected, digits): |expected - actual| &lt; 10^-digits / 2.
    /// (xUnit's Assert.Equal with a precision rounds both sides first, which can
    /// part two values a hair apart across a rounding edge.)
    /// </summary>
    public static void CloseTo(double expected, double actual, int digits = 2)
    {
        if (double.IsPositiveInfinity(expected) && double.IsPositiveInfinity(actual)) return;
        if (double.IsNegativeInfinity(expected) && double.IsNegativeInfinity(actual)) return;
        var ok = Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2;
        Assert.True(ok, $"expected {actual:R} to be close to {expected:R} ({digits} digits)");
    }
}
