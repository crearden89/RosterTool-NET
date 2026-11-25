// Port of mulberry32 in src/test/rosterScenario.ts: the same seed always builds the
// same data, so a reseeded database has the same people, numbers and shifts.
namespace Roster.Data.Seed;

/// <summary>Deterministic pseudo-random numbers in [0, 1).</summary>
public sealed class SeedRandom(uint seed)
{
    private uint _a = seed;

    /// <summary>The next number in [0, 1), exactly as mulberry32 draws it.</summary>
    public double Next()
    {
        unchecked
        {
            _a += 0x6d2b79f5;
            var t = _a;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>A whole number in [min, max].</summary>
    public int Int(int min, int max) => min + (int)Math.Floor(Next() * (max - min + 1));

    /// <summary>A number in [min, max).</summary>
    public double Between(double min, double max) => min + Next() * (max - min);

    /// <summary>One of the items.</summary>
    public T Pick<T>(IReadOnlyList<T> items) => items[(int)Math.Floor(Next() * items.Count)];

    /// <summary>True with the given probability.</summary>
    public bool Chance(double probability) => Next() < probability;
}
