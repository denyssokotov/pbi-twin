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
public static class LeakScanner
{
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
                    var columns = string.Join(", ", lookup[m.Pattern].Keys.SelectMany(d => d.Columns).Select(names.Id).Distinct());
                    findings.Add(new LeakFinding(rel, lineNo, columns));
                }
            }
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
