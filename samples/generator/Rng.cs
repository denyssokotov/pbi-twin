namespace PbiTwin.SampleGenerator;

/// <summary>
/// SplitMix64. Tiny, fast, and — unlike System.Random — guaranteed to produce the same
/// sequence on every .NET version, so the sample data is byte-for-byte reproducible.
/// </summary>
internal sealed class Rng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform int in [min, max).</summary>
    public int Next(int min, int max) => min + (int)(NextDouble() * (max - min));

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(0, items.Count)];

    /// <summary>Picks an index according to relative weights.</summary>
    public int PickWeighted(IReadOnlyList<double> weights)
    {
        var total = 0.0;
        foreach (var w in weights)
        {
            total += w;
        }

        var r = NextDouble() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            r -= weights[i];
            if (r < 0)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }

    /// <summary>Skewed index in [0, n): small indexes are much more likely (long tail).</summary>
    public int LongTail(int n, double power) => Math.Min(n - 1, (int)(n * Math.Pow(NextDouble(), power)));

    public string Digits(int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)('0' + Next(0, 10));
        }

        return new string(chars);
    }
}
