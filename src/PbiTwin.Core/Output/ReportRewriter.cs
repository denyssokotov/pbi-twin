using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PbiTwin.Core.Output;

/// <summary>
/// Rewrites literals in a PBIR report: filter values (<c>'Ukraine'</c>), date filters
/// (<c>datetime'2024-01-01T00:00:00'</c>), textbox text, titles and page names.
/// </summary>
public static partial class ReportRewriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^datetime'(?<d>[^']+)'$")]
    private static partial Regex DateTimeLiteral();

    /// <summary>Rewrites every JSON file under the report's <c>definition</c> folder. Returns false for legacy reports.</summary>
    public static bool Rewrite(string reportFolder, LiteralRewriter literals, int dateShiftDays)
    {
        ArgumentNullException.ThrowIfNull(literals);

        var definition = Path.Combine(reportFolder, "definition");
        if (!Directory.Exists(definition))
        {
            literals.Warnings.Add("The report uses the legacy (non-PBIR) format; literals in visuals were not rewritten. Save the report as PBIR in Power BI Desktop and run again.");
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(definition, "*.json", SearchOption.AllDirectories))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is null)
            {
                continue;
            }

            var location = Path.GetRelativePath(reportFolder, file).Replace('\\', '/');
            var before = node.ToJsonString();
            Walk(node, null, location, literals, dateShiftDays);
            if (node.ToJsonString() != before)
            {
                File.WriteAllText(file, node.ToJsonString(WriteOptions) + "\n");
            }
        }

        return true;
    }

    private static void Walk(JsonNode node, string? column, string location, LiteralRewriter lit, int shift)
    {
        switch (node)
        {
            case JsonObject obj:
                // Filter conditions: remember which column the literal values belong to.
                if (obj["In"]?["Expressions"] is JsonArray inExpr)
                {
                    column = ColumnProperty(inExpr.FirstOrDefault()) ?? column;
                }
                else if (obj["Comparison"]?["Left"] is { } left)
                {
                    column = ColumnProperty(left) ?? column;
                }

                foreach (var (key, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    if (key == "Literal" && child["Value"] is JsonValue v && v.TryGetValue<string>(out var literal))
                    {
                        child["Value"] = RewriteLiteral(literal, column, location, lit, shift);
                    }
                    else if (key is "value" or "displayName" && child is JsonValue sv && sv.TryGetValue<string>(out var text)
                             && (key == "displayName" || obj.Parent is JsonArray { Parent: JsonObject parent } && parent.ContainsKey("textRuns")))
                    {
                        obj[key] = lit.RewriteText(text, location);
                    }
                    else
                    {
                        Walk(child, column, location, lit, shift);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        Walk(item, column, location, lit, shift);
                    }
                }

                break;
        }
    }

    private static string RewriteLiteral(string literal, string? column, string location, LiteralRewriter lit, int shift)
    {
        if (literal.Length >= 2 && literal[0] == '\'' && literal[^1] == '\'')
        {
            var inner = literal[1..^1].Replace("''", "'", StringComparison.Ordinal);
            var mapped = lit.MapLiteral(inner, null, column, location);
            return mapped == inner ? literal : "'" + mapped.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        var m = DateTimeLiteral().Match(literal);
        if (m.Success && DateTime.TryParse(m.Groups["d"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
        {
            return $"datetime'{PbiTwin.Core.Anonymize.DateShift.Apply(d, shift).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'";
        }

        return literal;
    }

    private static string? ColumnProperty(JsonNode? expr) =>
        expr?["Column"]?["Property"]?.GetValue<string>();
}
