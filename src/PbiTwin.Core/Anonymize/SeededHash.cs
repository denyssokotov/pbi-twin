using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PbiTwin.Core.Anonymize;

/// <summary>
/// Keyed hashing: HMAC-SHA256(seed, domain + value). Every pseudonym, noise value and derived parameter
/// comes from here, so the same seed always produces the same twin and nothing can be reversed without it.
/// </summary>
public sealed class SeededHash
{
    private const char Separator = '\u001F';
    private readonly byte[] _key;

    public SeededHash(string seed)
    {
        ArgumentException.ThrowIfNullOrEmpty(seed);
        _key = Encoding.UTF8.GetBytes(seed);
    }

    /// <summary>A fresh random seed such as <c>K7QD-9XMF-2HTA-P4WN</c>. Shown in the UI, never written to disk.</summary>
    public static string NewSeed()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[19];
        var bytes = RandomNumberGenerator.GetBytes(16);
        var o = 0;
        for (var i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                chars[o++] = '-';
            }

            chars[o++] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    public byte[] Bytes(string domain, string value) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(domain + Separator + value));

    public ulong UInt64(string domain, string value) => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(domain, value));

    /// <summary>Uniform double in [0, 1).</summary>
    public double Unit(string domain, string value) => (UInt64(domain, value) >> 11) * (1.0 / (1UL << 53));

    /// <summary>Deterministic pseudo-random stream, e.g. for per-row noise in one table.</summary>
    public Rng Stream(string domain, string value) => new(UInt64(domain, value));
}

/// <summary>SplitMix64: tiny, fast and identical on every platform and .NET version.</summary>
public sealed class Rng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);
}
