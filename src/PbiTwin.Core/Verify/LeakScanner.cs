using System.Text.RegularExpressions;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Verify;

/// <summary>A leak, described without the leaked value. <c>Context</c> is the surrounding text of a model or report file with every original value cut out.</summary>
public sealed record LeakFinding(string File, int Line, string Columns, string? Context = null);

/// <summary>
/// Searches every output file for every original value (at least 4 characters) of every anonymized text column.
/// The result is either "0 leaks found" or a list of file/line/column — never the value itself.
/// </summary>
public static partial class LeakScanner
{
    // GUIDs name the auto date tables, relationships and bookmarks; their segments are digit runs like 4234.
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    // Inline images (button backgrounds): base64 text is not data.
    [GeneratedRegex("base64,[A-Za-z0-9+/=]+")]
    private static partial Regex Base64();

    private const string CellDelimiters = ",;\"\t|'";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tmdl", ".json", ".pbir", ".pbism", ".pbip", ".platform", ".bim", ".dax", ".m", ".txt", ".xml", ".svg",
    };

    private static readonly HashSet<string> Syntax = new(["true", "false", "null", "none", "blank"], StringComparer.OrdinalIgnoreCase);

    public static (List<LeakFinding> Findings, int ValuesChecked, int FilesScanned) Scan(
        string folder,
        IReadOnlyDictionary<string, Dictionary<Domain, string>> knownText,
        IEnumerable<string>? excludeFiles = null,
        string? ignoredText = null,
        INames? names = null,
        IEnumerable<string>? ownNames = null)
    {
        names ??= IdentityNames.Instance;
        ArgumentNullException.ThrowIfNull(knownText);

        // Table and column names are kept on purpose; a value that is only a piece of one (a job title "Worker" in
        // the column "Worker ID") is structure, not data.
        var nameMatcher = new TextMatcher(ownNames ?? []);

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

                if (line.Contains("base64,", StringComparison.Ordinal))
                {
                    line = Base64().Replace(line, x => new string(' ', x.Length));
                }

                var matches = matcher.Find(line).ToList();
                var isData = ext.Equals(".csv", StringComparison.OrdinalIgnoreCase);
                var nameSpans = matches.Count > 0 && nameMatcher.Count > 0 ? nameMatcher.Find(line).Select(n => (n.Index, End: n.Index + n.Pattern.Length)).ToList() : [];
                foreach (var m in matches)
                {
                    var end = m.Index + m.Pattern.Length;
                    if (nameSpans.Any(n => n.Index <= m.Index && end <= n.End)
                        || (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && IsJsonKey(line, m.Index, end)))
                    {
                        continue;
                    }

                    if (line.Contains('-', StringComparison.Ordinal)
                        && Guid().Matches(line).Any(g => g.Index <= m.Index && end <= g.Index + g.Length && m.Pattern.Length < g.Length))
                    {
                        continue;
                    }

                    var columns = string.Join(", ", lookup[m.Pattern].Keys.SelectMany(d => d.Columns).Select(names.Id).Distinct());
                    var finding = new LeakFinding(rel, lineNo, columns, isData ? null : MaskedContext(line, matches, m.Index));
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

    /// <summary>True for <c>"match": ...</c>: a property name of the file format, not a value.</summary>
    private static bool IsJsonKey(string line, int start, int end)
    {
        if (start == 0 || line[start - 1] != '"' || end >= line.Length || line[end] != '"')
        {
            return false;
        }

        var next = end + 1;
        while (next < line.Length && line[next] == ' ')
        {
            next++;
        }

        return next < line.Length && line[next] == ':';
    }

    /// <summary>About 70 characters either side of <paramref name="at"/>, with every match replaced by ‹value›.</summary>
    private static string MaskedContext(string line, List<TextMatcher.Match> matches, int at)
    {
        var sb = new System.Text.StringBuilder();
        var last = 0;
        var start = 0;
        foreach (var m in matches)
        {
            sb.Append(line, last, m.Index - last);
            if (m.Index == at)
            {
                start = sb.Length;
            }

            sb.Append("‹value›");
            last = m.Index + m.Pattern.Length;
        }

        sb.Append(line, last, line.Length - last);
        var text = sb.ToString();
        var from = Math.Max(0, start - 70);
        var to = Math.Min(text.Length, start + 77);
        return (from > 0 ? "…" : string.Empty) + text[from..to].Trim() + (to < text.Length ? "…" : string.Empty);
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp",
    };

    /// <summary>Files the scan cannot read (neither text nor a picture), as relative paths. They were copied as they are.</summary>
    public static IReadOnlyList<string> UnscannedFiles(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !TextExtensions.Contains(Path.GetExtension(f)) && !ImageExtensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith('.'))
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
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
