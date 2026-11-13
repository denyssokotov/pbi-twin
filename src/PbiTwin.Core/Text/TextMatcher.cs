namespace PbiTwin.Core.Text;

/// <summary>
/// Finds many known strings inside text, fast enough to scan a whole twin. Matching is case-insensitive and
/// whole-word: a match must not be glued to letters or digits on either side ("Kyiv" matches in "to Kyiv,"
/// but not in "Kyivska").
/// </summary>
public sealed class TextMatcher
{
    public const int MinLength = 4;

    private readonly Dictionary<ulong, List<string>> _byPrefix = [];

    public TextMatcher(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        foreach (var p in patterns.Where(p => p.Length >= MinLength && HasLetterOrDigit(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = Key(p, 0);
            if (!_byPrefix.TryGetValue(key, out var list))
            {
                list = [];
                _byPrefix[key] = list;
            }

            list.Add(p);
        }

        foreach (var list in _byPrefix.Values)
        {
            list.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        Count = _byPrefix.Values.Sum(l => l.Count);
    }

    public int Count { get; }

    public readonly record struct Match(int Index, string Pattern);

    /// <summary>Non-overlapping whole-word matches, longest first at each position.</summary>
    public IEnumerable<Match> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_byPrefix.Count == 0)
        {
            yield break;
        }

        var i = 0;
        while (i <= text.Length - MinLength)
        {
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && _byPrefix.TryGetValue(Key(text, i), out var candidates))
            {
                var hit = candidates.FirstOrDefault(p => IsMatchAt(text, i, p));
                if (hit is not null)
                {
                    yield return new Match(i, hit);
                    i += hit.Length;
                    continue;
                }
            }

            i++;
        }
    }

    public string Replace(string text, Func<string, string> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        var matches = Find(text).ToList();
        if (matches.Count == 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        var last = 0;
        foreach (var m in matches)
        {
            sb.Append(text, last, m.Index - last).Append(replacement(m.Pattern));
            last = m.Index + m.Pattern.Length;
        }

        return sb.Append(text, last, text.Length - last).ToString();
    }

    private static bool IsMatchAt(string text, int i, string p) =>
        i + p.Length <= text.Length
        && text.AsSpan(i, p.Length).Equals(p, StringComparison.OrdinalIgnoreCase)
        && (i + p.Length == text.Length || !char.IsLetterOrDigit(text[i + p.Length]));

    private static ulong Key(string s, int i)
    {
        ulong k = 0;
        for (var j = 0; j < MinLength; j++)
        {
            k = (k << 16) | char.ToUpperInvariant(s[i + j]);
        }

        return k;
    }

    private static bool HasLetterOrDigit(string s) => s.Any(char.IsLetterOrDigit);
}
