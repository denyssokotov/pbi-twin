using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PbiTwin.Core.Rename;

/// <summary>
/// Rewrites model references in a PBIR report: <c>Entity</c>, <c>Property</c>, hierarchies and levels in
/// visuals, filters, sorts and bookmarks (resolving <c>From</c> aliases), plus <c>queryRef</c>,
/// <c>nativeQueryRef</c> and selector <c>metadata</c> strings.
/// </summary>
public static class ReportRenamer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Rewrite(string reportFolder, NameMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var definition = Path.Combine(reportFolder, "definition");
        if (!Directory.Exists(definition))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(definition, "*.json", SearchOption.AllDirectories))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is null)
            {
                continue;
            }

            var before = node.ToJsonString();
            Walk(node, new Dictionary<string, string>(StringComparer.Ordinal), map);
            if (node.ToJsonString() != before)
            {
                File.WriteAllText(file, node.ToJsonString(WriteOptions) + "\n");
            }
        }
    }

    private static void Walk(JsonNode node, Dictionary<string, string> aliases, NameMap map)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    Walk(item, aliases, map);
                }
            }

            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        // Aliases of a query ("From": [{ "Name": "d", "Entity": "DimCustomer" }]) apply to the whole object.
        if (obj["From"] is JsonArray from)
        {
            aliases = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
            foreach (var f in from)
            {
                if (Str(f?["Name"]) is { } alias && Str(f?["Entity"]) is { } entity)
                {
                    aliases[alias] = entity;
                }
            }
        }

        // Read everything with the original names before any child is renamed.
        var table = TableOf(obj["Expression"], aliases);
        if (table is not null && Str(obj["Property"]) is { } property)
        {
            obj["Property"] = Member(map, table, property);
        }

        if (table is not null && Str(obj["Hierarchy"]) is { } hierarchy)
        {
            obj["Hierarchy"] = map.Hierarchies.GetValueOrDefault(table)?.GetValueOrDefault(hierarchy) ?? hierarchy;
        }

        if (Str(obj["Level"]) is { } level && obj["Expression"]?["Hierarchy"] is JsonObject h
            && TableOf(h["Expression"], aliases) is { } ht && Str(h["Hierarchy"]) is { } hn)
        {
            obj["Level"] = map.Levels.GetValueOrDefault(ht)?.GetValueOrDefault(hn)?.GetValueOrDefault(level) ?? level;
        }

        if (obj["field"] is JsonObject field && Str(obj["nativeQueryRef"]) is { } native)
        {
            var inner = field.FirstOrDefault().Value as JsonObject;
            if (TableOf(inner?["Expression"], aliases) is { } ft && Str(inner?["Property"]) is { } fp)
            {
                obj["nativeQueryRef"] = native.Replace(fp, Member(map, ft, fp), StringComparison.Ordinal);
            }
        }

        foreach (var key in new[] { "queryRef", "metadata" })
        {
            if (Str(obj[key]) is { } reference)
            {
                obj[key] = QueryRef(reference, map);
            }
        }

        foreach (var (key, child) in obj.ToList())
        {
            if (key == "Entity" && Str(child) is { } entity)
            {
                obj[key] = map.Table(entity);
            }
            else if (child is not null)
            {
                Walk(child, aliases, map);
            }
        }
    }

    private static string? TableOf(JsonNode? expression, Dictionary<string, string> aliases)
    {
        var sourceRef = expression?["SourceRef"];
        return Str(sourceRef?["Entity"]) ?? (Str(sourceRef?["Source"]) is { } alias ? aliases.GetValueOrDefault(alias) : null);
    }

    private static string Member(NameMap map, string table, string name) =>
        map.Columns.TryGetValue(table, out var cols) && cols.TryGetValue(name, out var c) ? c : map.Measure(name);

    /// <summary>"FactSales.Revenue" or "Sum(FactSales.SalesAmount)" → twin names.</summary>
    internal static string QueryRef(string reference, NameMap map)
    {
        var open = reference.LastIndexOf('(');
        var close = reference.IndexOf(')', StringComparison.Ordinal);
        var start = open + 1;
        var end = close > start ? close : reference.Length;
        var inner = reference[start..end];

        foreach (var table in map.Tables.Keys.OrderByDescending(t => t.Length))
        {
            if (inner.StartsWith(table + ".", StringComparison.OrdinalIgnoreCase))
            {
                var member = inner[(table.Length + 1)..];
                return reference[..start] + map.Table(table) + "." + Member(map, table, member) + reference[end..];
            }
        }

        return reference;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
