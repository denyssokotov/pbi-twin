using System.Text.RegularExpressions;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Verify;

/// <summary>A leak, described without the leaked value.</summary>
public sealed record LeakFinding(string File, int Line, string Columns);

/// <summary>
/// Searches every output file for every original value (at least 4 characters) of every anonymized text column.
/// The result is either "0 leaks found" or a list of file/line/column — never the value itself.
/// </summary>
public static partial class LeakScanner
{
    // GUIDs name the auto date tables, relationships and bookmarks; their segments are digit runs like 4234.
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    private const string CellDelimiters = ",;\"\t|'";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tmdl", ".json", ".pbir", ".pbism", ".pbip", ".platform", ".bim", ".dax", ".m", ".txt", ".xml",
    };

    private static readonly HashSet<string> Syntax = new(["true", "false", "null", "none", "blank"], StringComparer.OrdinalIgnoreCase);

    public static (List<LeakFinding> Findings, int ValuesChecked, int FilesScanned) Scan(
        string folder,
        IReadOnlyDictionary<string, Dictionary<Domain, string>> knownText,
        IEnumerable<string>? excludeFiles = null,
        string? ignoredText = null,
        INames? names = null)
    {
        names ??= IdentityNames.Instance;
        ArgumentNullException.ThrowIfNull(knownText);

        // A value that is its own pseudonym somewhere (rare) cannot be told apart from a leak, so skip it.
        // The literals true, false and null are syntax in every JSON, TMDL and DAX file, not data: a text column holding
        // "True"/"False" would otherwise "leak" into every theme and visual.
        var originals = knownText
            .Where(kv => !kv.Value.Values.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) && !Syntax.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        var matcher = new TextMatcher(originals);
        var lookup = new Dictionary<string, Dictionary<Domain, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in knownText)
        {
            lookup.TryAdd(k, v);
        }

        var excluded = (excludeFiles ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<LeakFinding>();
        // A hit that is only a piece of a bigger cell may be a pseudonym that happens to contain an original value
        // (ID-48213 contains the old ID 48213). Those are decided after the scan, against the pseudonyms.
        var partial = new List<(LeakFinding Finding, string Token)>();
        var files = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(file);
            var name = Path.GetFileName(file);
            if (excluded.Contains(Path.GetFullPath(file)) || !(TextExtensions.Contains(ext) || name.StartsWith('.')))
            {
                continue;
            }

            files++;
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            using var reader = new StreamReader(file);
            var lineNo = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNo++;
                if (ignoredText is not null)
                {
                    line = line.Replace(ignoredText, new string(' ', ignoredText.Length), StringComparison.OrdinalIgnoreCase);
                }

                foreach (var m in matcher.Find(line))
                {
                    var end = m.Index + m.Pattern.Length;
                    if (line.Contains('-', StringComparison.Ordinal)
                        && Guid().Matches(line).Any(g => g.Index <= m.Index && end <= g.Index + g.Length && m.Pattern.Length < g.Length))
                    {
                        continue;
                    }

                    var columns = string.Join(", ", lookup[m.Pattern].Keys.SelectMany(d => d.Columns).Select(names.Id).Distinct());
                    var finding = new LeakFinding(rel, lineNo, columns);
                    var from = m.Index;
                    while (from > 0 && !CellDelimiters.Contains(line[from - 1], StringComparison.Ordinal))
                    {
                        from--;
                    }

                    var to = end;
                    while (to < line.Length && !CellDelimiters.Contains(line[to], StringComparison.Ordinal))
                    {
                        to++;
                    }

                    if (to - from > m.Pattern.Length)
                    {
                        var token = line[from..to].Trim();
                        if (m.Pattern.All(char.IsAsciiDigit) && token.All(c => char.IsAsciiDigit(c) || ".,-+:/ TZ".Contains(c, StringComparison.Ordinal)))
                        {
                            // A digit-only original inside a number or date (12.4821 contains 4821) is a coincidence.
                            continue;
                        }

                        partial.Add((finding, token));
                    }
                    else
                    {
                        findings.Add(finding);
                    }
                }
            }
        }

        if (partial.Count > 0)
        {
            var tokens = partial.Select(p => p.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pseudonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in knownText.Values.SelectMany(v => v.Values))
            {
                if (tokens.Contains(value))
                {
                    pseudonyms.Add(value);
                }
            }

            // Findings stay in file and line order: they were collected in the same loop.
            var all = new List<(LeakFinding Finding, bool Keep)>();
            all.AddRange(findings.Select(f => (f, true)));
            all.AddRange(partial.Select(p => (p.Finding, !pseudonyms.Contains(p.Token))));
            findings = all.Where(x => x.Keep).Select(x => x.Finding)
                .OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        }

        return (findings, matcher.Count, files);
    }

    /// <summary>Columns whose original values occur in <paramref name="text"/> (e.g. the output path).</summary>
    public static IReadOnlyList<string> ColumnsFoundIn(string text, IReadOnlyDictionary<string, Dictionary<Domain, string>> knownText, INames? names = null)
    {
        names ??= IdentityNames.Instance;
        ArgumentNullException.ThrowIfNull(knownText);
        var lookup = new Dictionary<string, Dictionary<Domain, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in knownText)
        {
            lookup.TryAdd(k, v);
        }

        return new TextMatcher(knownText.Keys).Find(text)
            .SelectMany(m => lookup[m.Pattern].Keys.SelectMany(d => d.Columns).Select(names.Id))
            .Distinct()
            .ToList();
    }
}
