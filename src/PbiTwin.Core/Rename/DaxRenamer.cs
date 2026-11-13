using System.Text.RegularExpressions;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Rename;

/// <summary>
/// Rewrites table, column and measure references in DAX using a <see cref="NameMap"/>. Works on tokens, so
/// strings, comments, <c>'Quoted Tables'</c>, <c>[Columns]</c> and escapes (<c>]]</c>, <c>''</c>) are handled safely.
/// The same code translates DAX written against the twin back to the original names (with a reversed map).
/// </summary>
public static partial class DaxRenamer
{
    /// <summary>An unquoted identifier that is not a function call (not followed by '(').</summary>
    [GeneratedRegex(@"(?<![\w.$@])([A-Za-z_][A-Za-z0-9_]*)(?!\w)(?!\s*\()")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex TrailingIdentifier();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"\bCALENDAR(AUTO)?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex Calendar();

    [GeneratedRegex(@"\bGENERATESERIES\s*\(|\{", RegexOptions.IgnoreCase)]
    private static partial Regex Series();

    /// <param name="dax">The expression.</param>
    /// <param name="map">Names to apply (use <see cref="NameMap.Reverse"/> to translate back).</param>
    /// <param name="homeTable">Table the expression lives in; unqualified <c>[Column]</c> refers to it.</param>
    /// <param name="anyTableFallback">
    /// Resolve an unqualified <c>[Column]</c> that is neither a measure nor a column of the home table to the
    /// only table that has such a column. Off for calculated tables, whose virtual columns (e.g. CALENDAR's
    /// <c>[Date]</c>) must not be renamed.
    /// </param>
    /// <param name="unresolved">Receives bracketed names that could not be mapped.</param>
    public static string Rename(string? dax, NameMap map, string? homeTable = null, bool anyTableFallback = true, ICollection<string>? unresolved = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (string.IsNullOrEmpty(dax))
        {
            return dax ?? string.Empty;
        }

        var tokens = DaxTokenizer.Tokenize(dax);
        var original = tokens.ToArray();
        var strings = original.Where(t => t.Kind == DaxTokenKind.String).Select(t => t.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasCalendar = Calendar().IsMatch(dax);
        var hasSeries = Series().IsMatch(dax);
        var measureNameStrings = dax.Contains("SELECTEDMEASURENAME", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = original[i];
            switch (t.Kind)
            {
                case DaxTokenKind.QuotedName when map.Tables.TryGetValue(t.Value, out var table):
                    tokens[i] = t with { Raw = Quote(table), Value = table };
                    break;

                case DaxTokenKind.Code:
                    var raw = Identifier().Replace(t.Raw, m =>
                        map.Tables.TryGetValue(m.Value, out var name) ? TableRef(name) : m.Value);
                    if (raw != t.Raw)
                    {
                        tokens[i] = t with { Raw = raw, Value = raw };
                    }

                    break;

                case DaxTokenKind.BracketName:
                    var qualifier = i == 0 ? null : original[i - 1] switch
                    {
                        { Kind: DaxTokenKind.QuotedName } q => q.Value,
                        { Kind: DaxTokenKind.Code } c when TrailingIdentifier().Match(c.Raw) is { Success: true } m && map.Tables.ContainsKey(m.Value) => m.Value,
                        _ => null,
                    };

                    var renamed = qualifier is not null
                        ? ColumnOf(map, qualifier, t.Value) ?? map.Measures.GetValueOrDefault(t.Value)
                        : map.Measures.GetValueOrDefault(t.Value)
                          ?? (IsVirtual(t.Value) ? null
                              : (homeTable is null ? null : ColumnOf(map, homeTable, t.Value))
                                ?? (anyTableFallback ? UniqueColumn(map, t.Value) : null));

                    if (renamed is not null)
                    {
                        tokens[i] = t with { Raw = "[" + renamed.Replace("]", "]]", StringComparison.Ordinal) + "]", Value = renamed };
                    }
                    else if (qualifier is not null || !IsVirtual(t.Value))
                    {
                        unresolved?.Add(t.Value);
                    }

                    break;

                case DaxTokenKind.String when measureNameStrings && map.Measures.TryGetValue(t.Value, out var measure):
                    tokens[i] = t with { Raw = DaxTokenizer.StringLiteral(measure), Value = measure };
                    break;
            }
        }

        return DaxTokenizer.Join(tokens);

        // A name defined inside the expression ("Year" in ADDCOLUMNS) or produced by a table function.
        bool IsVirtual(string name) =>
            strings.Contains(name)
            || (hasCalendar && name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            || (hasSeries && name.StartsWith("Value", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ColumnOf(NameMap map, string table, string column) =>
        map.Columns.TryGetValue(table, out var cols) && cols.TryGetValue(column, out var c) ? c : null;

    private static string? UniqueColumn(NameMap map, string column)
    {
        var tables = map.TablesWithColumn(column).Take(2).ToList();
        return tables.Count == 1 ? map.Columns[tables[0]][column] : null;
    }

    private static string TableRef(string name) => SimpleName().IsMatch(name) ? name : Quote(name);

    private static string Quote(string name) => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>"Translate DAX": between the twin's anonymous names and the real ones, using the codebook.</summary>
public static class DaxTranslator
{
    public static string ToReal(string dax, NameMap codebook, ICollection<string>? unresolved = null)
    {
        ArgumentNullException.ThrowIfNull(codebook);
        return DaxRenamer.Rename(dax, codebook.Reverse(), unresolved: unresolved);
    }

    public static string ToAnonymous(string dax, NameMap codebook, ICollection<string>? unresolved = null) =>
        DaxRenamer.Rename(dax, codebook, unresolved: unresolved);
}
