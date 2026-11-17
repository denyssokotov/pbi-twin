using System.Globalization;
using System.Text;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Resources;

namespace PbiTwin.Core.Anonymize;

/// <summary>
/// Turns original values into pseudonyms according to an <see cref="AnonymizationPlan"/>.
/// Deterministic: same seed + same input = same output.
/// </summary>
public sealed class Anonymizer
{
    private readonly SeededHash _hash;
    private readonly Dictionary<Domain, Dictionary<object, object>> _dictionaries = [];
    private readonly Dictionary<string, Dictionary<Domain, string>> _knownText = new(StringComparer.Ordinal);

    private readonly Func<Domain, string>? _neutralLabel;

    /// <param name="plan">Rules per column.</param>
    /// <param name="options">Seed and numeric settings.</param>
    /// <param name="neutralLabel">
    /// When names are obfuscated, labels like "Segment 01" would reveal the column name. This supplies a neutral
    /// label per domain instead (e.g. the twin's column name), and text keys get the prefix "ID".
    /// </param>
    public Anonymizer(AnonymizationPlan plan, TwinOptions options, Func<Domain, string>? neutralLabel = null)
    {
        _neutralLabel = neutralLabel;
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        Plan = plan;
        _hash = new SeededHash(options.Seed);
        Parameters = TwinParameters.From(options, _hash);
    }

    public AnonymizationPlan Plan { get; }

    internal TwinParameters Parameters { get; }

    /// <summary>Every original text value that was replaced, with its pseudonym per domain. In memory only.</summary>
    public IReadOnlyDictionary<string, Dictionary<Domain, string>> KnownText => _knownText;

    /// <summary>Per-table deterministic noise stream; rows must be fed in the same order to reproduce a twin.</summary>
    public Rng NoiseFor(string table) => _hash.Stream("noise", table);

    /// <summary>
    /// Builds the one-to-one mapping of a dictionary domain from all its distinct values. Pseudonym order follows
    /// the keyed hash of each value, so the original order (e.g. of customer ids) is not revealed.
    /// </summary>
    public void BuildDictionary(Domain domain, IEnumerable<object> distinctValues)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(distinctValues);

        var ordered = distinctValues
            .Distinct()
            .Select(v => (Value: v, Text: Canonical(v)))
            .Select(x => (x.Value, x.Text, Hash: _hash.UInt64(domain.Id, x.Text)))
            .OrderBy(x => x.Hash)
            .ThenBy(x => x.Text, StringComparer.Ordinal)
            .ToList();

        var map = new Dictionary<object, object>(ordered.Count);
        var width = Math.Max(2, ordered.Count.ToString(CultureInfo.InvariantCulture).Length);
        for (var i = 0; i < ordered.Count; i++)
        {
            map[ordered[i].Value] = Pseudonym(domain, ordered[i].Value, i, width);
        }

        _dictionaries[domain] = map;
    }

    public bool HasDictionary(Domain domain) => _dictionaries.ContainsKey(domain);

    public object? Transform(ColumnPlan column, object? value, Rng noise)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(noise);

        if (value is null)
        {
            return null;
        }

        var domain = column.Domain;
        var result = domain.Rule switch
        {
            Rule.Keep => value,
            Rule.Blank => null,
            Rule.Key or Rule.City or Rule.Country or Rule.Label => Lookup(domain, value),
            Rule.DateShift => value is DateTime d ? DateShift.Apply(d, Parameters.DateShiftDays) : value,
            Rule.Money => Scale(value, Parameters.MoneyFactor, noise, wholeNumbers: false),
            Rule.Quantity => Scale(value, Parameters.QuantityFactor, noise, wholeNumbers: true),
            _ => value is string s ? FakeText(domain, s) : null,
        };

        // Free text is not remembered: millions of long comments would fill memory, and the names, emails and
        // phones inside them are already covered by their own columns.
        if (value is string original && result is string pseudo && Rules.ReplacesText(domain.Rule) && domain.Rule != Rule.FreeText)
        {
            Remember(original, domain, pseudo);
        }

        return result;
    }

    private void Remember(string original, Domain domain, string pseudonym)
    {
        if (!_knownText.TryGetValue(original, out var byDomain))
        {
            byDomain = [];
            _knownText[original] = byDomain;
        }

        byDomain.TryAdd(domain, pseudonym);
    }

    /// <summary>Records all dictionary mappings as known text (used for literal rewriting and leak scan).</summary>
    public void RememberDictionaries()
    {
        foreach (var (domain, map) in _dictionaries)
        {
            if (!Rules.ReplacesText(domain.Rule))
            {
                continue;
            }

            foreach (var (original, pseudo) in map)
            {
                if (original is string s && pseudo is string p)
                {
                    Remember(s, domain, p);
                }
            }
        }
    }

    private object Lookup(Domain domain, object value)
    {
        if (!_dictionaries.TryGetValue(domain, out var map))
        {
            throw new InvalidOperationException($"Dictionary for domain {domain.Id} was not built.");
        }

        if (map.TryGetValue(value, out var pseudo))
        {
            return pseudo;
        }

        // A value that was not in the pre-pass (should not happen): extend the dictionary, keeping it one-to-one.
        var width = Math.Max(2, (map.Count + 1).ToString(CultureInfo.InvariantCulture).Length);
        pseudo = Pseudonym(domain, value, map.Count, width);
        map[value] = pseudo;
        return pseudo;
    }

    private object Pseudonym(Domain domain, object value, int index, int width)
    {
        var n = index + 1;
        var label = _neutralLabel?.Invoke(domain) ?? domain.Label;
        var prefix = _neutralLabel is null ? AnonymizationPlan.Initials(domain.Columns[0].Name) : "ID";
        var inv = CultureInfo.InvariantCulture;
        return domain.Rule switch
        {
            Rule.Key => value switch
            {
                long => (long)n,
                double => (double)n,
                decimal => (decimal)n,
                _ => $"{prefix}-{n.ToString("D6", inv)}",
            },
            Rule.City => index < FakeData.CityPrefixes.Length * FakeData.CitySuffixes.Length
                ? FakeData.CityPrefixes[index % FakeData.CityPrefixes.Length] + FakeData.CitySuffixes[index / FakeData.CityPrefixes.Length]
                : $"City {n.ToString(inv)}",
            Rule.Country => index < FakeData.Countries.Length ? FakeData.Countries[index] : $"Country {n.ToString(inv)}",
            _ => $"{label} {n.ToString("D" + width.ToString(inv), inv)}",
        };
    }

    /// <summary>
    /// value × factor × (1 ± noise). The factor is shared by all columns of the rule, so ratios between them
    /// (margin, shares, YoY %) survive. Zero stays zero, the sign never changes, non-zero whole numbers stay non-zero.
    /// </summary>
    private object Scale(object value, double factor, Rng noise, bool wholeNumbers)
    {
        var m = factor * (1 + (((noise.NextDouble() * 2) - 1) * Parameters.Noise));
        switch (value)
        {
            case decimal d:
                if (d == 0)
                {
                    return d;
                }

                var dm = d * (decimal)m;
                return wholeNumbers ? NonZero(Math.Round(dm, MidpointRounding.AwayFromZero), Math.Sign(d)) : Math.Round(dm, 2, MidpointRounding.AwayFromZero);
            case double x:
                if (x == 0)
                {
                    return x;
                }

                var xm = x * m;
                return wholeNumbers ? (double)NonZero((decimal)Math.Round(xm, MidpointRounding.AwayFromZero), Math.Sign(x)) : Math.Round(xm, 4);
            case long l:
                if (l == 0)
                {
                    return l;
                }

                return (long)NonZero((decimal)Math.Round(l * m, MidpointRounding.AwayFromZero), Math.Sign(l));
            default:
                return value;
        }

        static decimal NonZero(decimal v, int sign) => v == 0 ? sign : v;
    }

    private string FakeText(Domain domain, string s)
    {
        var h = _hash.UInt64(domain.Id, s);
        var rng = new Rng(h);
        return domain.Rule switch
        {
            Rule.PersonName => FakePerson(s, rng),
            Rule.Email => FakeEmail(rng),
            Rule.Phone or Rule.Iban => FormatPreserving(s, rng),
            Rule.Address => $"{1 + rng.Next(199)} {Pick(FakeData.StreetNames, rng)} {Pick(FakeData.StreetTypes, rng)}",
            Rule.FreeText => Lorem(s.Length, rng),
            _ => throw new InvalidOperationException($"Rule {domain.Rule} does not produce text."),
        };
    }

    private static string FakePerson(string s, Rng rng)
    {
        var first = Pick(FakeData.FirstNames, rng);
        var last = Pick(FakeData.LastNames, rng);
        return s.Contains(' ', StringComparison.Ordinal) ? $"{first} {last}" : first;
    }

    private static string FakeEmail(Rng rng) =>
        $"{Pick(FakeData.FirstNames, rng)}.{Pick(FakeData.LastNames, rng)}{10 + rng.Next(90)}@{Pick(FakeData.EmailDomains, rng)}".ToLowerInvariant();

    /// <summary>Same shape, new characters: digits stay digits, letters stay letters (and keep their case).</summary>
    private static string FormatPreserving(string s, Rng rng)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                >= '0' and <= '9' => (char)('0' + rng.Next(10)),
                >= 'A' and <= 'Z' => (char)('A' + rng.Next(26)),
                >= 'a' and <= 'z' => (char)('a' + rng.Next(26)),
                _ => c,
            });
        }

        return sb.ToString();
    }

    private static string Lorem(int length, Rng rng)
    {
        var target = Math.Max(4, (int)(length * (0.9 + rng.NextDouble() * 0.2)));
        var sb = new StringBuilder();
        while (sb.Length < target)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Pick(FakeData.Lorem, rng));
        }

        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    private static string Pick(string[] items, Rng rng) => items[rng.Next(items.Length)];

    internal static string Canonical(object value) => value switch
    {
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
