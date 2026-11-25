// New: vitest's toBeCloseTo for the ported tests: |expected - actual| < 10^-digits / 2.

namespace Roster.Engine.Tests.Overview;

internal static class Close
{
    public static void To(double expected, double? actual, int digits = 2)
    {
        Assert.NotNull(actual);
        var ok = Math.Abs(expected - actual!.Value) < Math.Pow(10, -digits) / 2;
        Assert.True(ok, $"Expected {actual} to be close to {expected} ({digits} digits).");
    }
}
