using System.Text.RegularExpressions;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Output;

/// <summary>
/// Replaces original values that appear as literals in DAX, report filters and free text with their pseudonyms.
/// Because the mapping is consistent, <c>CALCULATE([Revenue], DimCustomer[Segment] = "Enterprise")</c> still
/// selects the same rows in the twin.
/// </summary>
public sealed partial class LiteralRewriter
{
    private readonly IReadOnlyDictionary<string, Dictionary<Domain, string>> _known;
    private readonly Dictionary<string, Dictionary<Domain, string>> _knownIgnoreCase;
    private readonly TextMatcher _matcher;
    private readonly INames _names;

    public LiteralRewriter(IReadOnlyDictionary<string, Dictionary<Domain, string>> knownText, INames? names = null)
    {
        ArgumentNullException.ThrowIfNull(knownText);
        _known = knownText;
        _names = names ?? IdentityNames.Instance;
        _knownIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in knownText)
        {
            _knownIgnoreCase.TryAdd(k, v);
        }

        _matcher = new TextMatcher(knownText.Keys);
    }

    /// <summary>Number of replaced values, per location ("Measure 'X'", "visual.json"). Never contains the values.</summary>
    public Dictionary<string, int> Replacements { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    [GeneratedRegex(@"^[\s=<>!{}(),;&|]*(?:(?:NOT\s+)?IN\b[\s{(]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OperatorsOnly();

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_.]*)$")]
    private static partial Regex TrailingIdentifier();

    /// <summary>DATE(2024, ...), DATEVALUE("...") or dt"..." — dates written as constants, not derived from data.</summary>
    [GeneratedRegex(@"\bDATE\s*\(\s*\d|\bDATEVALUE\s*\(\s*$|\bdt$", RegexOptions.IgnoreCase)]
    private static partial Regex DateLiteral();

    /// <summary>Rewrites string literals in a DAX expression.</summary>
    public string RewriteDax(string? dax, string location)
    {
        if (string.IsNullOrEmpty(dax))
        {
            return dax ?? string.Empty;
        }

        var tokens = DaxTokenizer.Tokenize(dax);
        (string? Table, string Column)? context = null;
        var changed = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case DaxTokenKind.Code:
                    if (DateLiteral().IsMatch(t.Raw))
                    {
                        Warn($"{location} contains a hard-coded date; dates in the twin are shifted, so review this expression.");
                    }

                    if (!OperatorsOnly().IsMatch(t.Raw))
                    {
                        context = null;
                    }

                    break;
                case DaxTokenKind.BracketName:
                    string? table = null;
                    if (i > 0)
                    {
                        var prev = tokens[i - 1];
                        table = prev.Kind switch
                        {
                            DaxTokenKind.QuotedName => prev.Value,
                            DaxTokenKind.Code when TrailingIdentifier().Match(prev.Raw) is { Success: true } m => m.Groups[1].Value,
                            _ => null,
                        };
                    }

                    context = (table, t.Value);
                    break;
                case DaxTokenKind.String:
                    var replaced = MapLiteral(t.Value, context?.Table, context?.Column, location);
                    if (replaced != t.Value)
                    {
                        tokens[i] = t with { Raw = DaxTokenizer.StringLiteral(replaced), Value = replaced };
                        changed = true;
                    }

                    break;
            }
        }

        return changed ? DaxTokenizer.Join(tokens) : dax;
    }

    /// <summary>
    /// A literal that equals an original value becomes its pseudonym; otherwise original values inside it
    /// (e.g. a city in a title) are replaced word by word.
    /// </summary>
    public string MapLiteral(string value, string? table, string? column, string location)
    {
        ArgumentNullException.ThrowIfNull(value);

        var exact = MapExact(value, table, column, location);
        return exact ?? RewriteText(value, location);
    }

    /// <summary>Replaces every original value found inside free text (textboxes, titles, descriptions).</summary>
    public string RewriteText(string? text, string location)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var count = 0;
        var result = _matcher.Replace(text, original =>
        {
            count++;
            return MapExact(original, null, null, location, countIt: false) ?? original;
        });

        Count(location, count);
        return result;
    }

    private string? MapExact(string value, string? table, string? column, string location, bool countIt = true)
    {
        if (!_known.TryGetValue(value, out var byDomain) && !_knownIgnoreCase.TryGetValue(value, out byDomain))
        {
            return null;
        }

        string pseudonym;
        var distinct = byDomain.Values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1)
        {
            pseudonym = distinct[0];
        }
        else
        {
            var match = column is null ? null : byDomain.FirstOrDefault(kv =>
                kv.Key.Columns.Any(c => c.Name == column && (table is null || c.Table == table))).Value;

            if (match is null)
            {
                match = byDomain.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal).First().Value;
                Warn($"{location}: a literal matches values of several columns ({string.Join(", ", byDomain.Keys.SelectMany(d => d.Columns).Select(_names.Id))}); the first mapping was used.");
            }

            pseudonym = match;
        }

        if (countIt)
        {
            Count(location, 1);
        }

        return pseudonym;
    }

    private void Count(string location, int n)
    {
        if (n > 0)
        {
            Replacements[location] = Replacements.GetValueOrDefault(location) + n;
        }
    }

    private void Warn(string message)
    {
        if (!Warnings.Contains(message))
        {
            Warnings.Add(message);
        }
    }
}
