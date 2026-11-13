using System.Globalization;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Metadata;
using ColumnType = Microsoft.AnalysisServices.Tabular.ColumnType;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PbiTwin.Core.Rename;

public enum RenameMode
{
    /// <summary>Keep the original names.</summary>
    None,

    /// <summary><c>Table01</c>, <c>Table01[Col03]</c>, <c>[Measure07]</c>.</summary>
    Neutral,

    /// <summary><c>FactA</c>, <c>DimB</c>, <c>CalcGroupA</c>; columns and measures as in <see cref="Neutral"/>.</summary>
    Semantic,
}

/// <summary>Maps original object names to the names used in the twin (identity when not renaming).</summary>
public interface INames
{
    string Table(string table);

    string Column(string table, string column);

    string Measure(string measure);
}

public sealed class IdentityNames : INames
{
    public static readonly IdentityNames Instance = new();

    public string Table(string table) => table;

    public string Column(string table, string column) => column;

    public string Measure(string measure) => measure;
}

public static class NamesExtensions
{
    public static string Id(this INames names, ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(column);
        return $"{names.Table(column.Table)}[{names.Column(column.Table, column.Name)}]";
    }
}

/// <summary>
/// The codebook: original name → twin name for tables, columns, measures, hierarchies, levels and roles.
/// It contains names only, never data. Keep it private: it is the key to read the twin's model.
/// </summary>
public sealed class NameMap : INames
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public Dictionary<string, string> Tables { get; } = new(Cmp);

    /// <summary>Original table → original column → twin column.</summary>
    public Dictionary<string, Dictionary<string, string>> Columns { get; } = new(Cmp);

    public Dictionary<string, string> Measures { get; } = new(Cmp);

    /// <summary>Original table → original hierarchy → twin hierarchy.</summary>
    public Dictionary<string, Dictionary<string, string>> Hierarchies { get; } = new(Cmp);

    /// <summary>Original table → original hierarchy → original level → twin level.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Levels { get; } = new(Cmp);

    public Dictionary<string, string> Roles { get; } = new(Cmp);

    public string Table(string table) => Tables.GetValueOrDefault(table) ?? table;

    public string Column(string table, string column) =>
        Columns.TryGetValue(table, out var cols) && cols.TryGetValue(column, out var c) ? c : column;

    public string Measure(string measure) => Measures.GetValueOrDefault(measure) ?? measure;

    public static NameMap Create(Model model, RenameMode mode)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (mode == RenameMode.None)
        {
            throw new ArgumentException("Nothing to map when names are kept.", nameof(mode));
        }

        var map = new NameMap();
        var relationships = model.Relationships.OfType<SingleColumnRelationship>().ToList();
        var many = relationships.Select(r => r.FromTable.Name).ToHashSet(Cmp);
        var one = relationships.Select(r => r.ToTable.Name).ToHashSet(Cmp);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var tableWidth = Width(model.Tables.Count);

        for (var i = 0; i < model.Tables.Count; i++)
        {
            var t = model.Tables[i];
            if (mode == RenameMode.Neutral)
            {
                map.Tables[t.Name] = "Table" + Number(i + 1, tableWidth);
            }
            else
            {
                var prefix = t.CalculationGroup is not null ? "CalcGroup"
                    : many.Contains(t.Name) && !one.Contains(t.Name) ? "Fact"
                    : one.Contains(t.Name) ? "Dim"
                    : "Table";
                var n = counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
                map.Tables[t.Name] = prefix + Letters(n);
            }

            var columns = t.Columns.Where(c => c.Type != ColumnType.RowNumber).ToList();
            var colWidth = Width(columns.Count);
            map.Columns[t.Name] = new(Cmp);
            for (var c = 0; c < columns.Count; c++)
            {
                map.Columns[t.Name][columns[c].Name] = "Col" + Number(c + 1, colWidth);
            }

            map.Hierarchies[t.Name] = new(Cmp);
            map.Levels[t.Name] = new(Cmp);
            for (var h = 0; h < t.Hierarchies.Count; h++)
            {
                var hierarchy = t.Hierarchies[h];
                map.Hierarchies[t.Name][hierarchy.Name] = "Hierarchy" + Number(h + 1, 2);
                map.Levels[t.Name][hierarchy.Name] = hierarchy.Levels
                    .Select((l, k) => (l.Name, New: "Level" + Number(k + 1, 2)))
                    .ToDictionary(x => x.Name, x => x.New, Cmp);
            }
        }

        var measures = model.Tables.SelectMany(t => t.Measures).ToList();
        var measureWidth = Width(measures.Count);
        for (var m = 0; m < measures.Count; m++)
        {
            map.Measures[measures[m].Name] = "Measure" + Number(m + 1, measureWidth);
        }

        for (var r = 0; r < model.Roles.Count; r++)
        {
            map.Roles[model.Roles[r].Name] = "Role" + Number(r + 1, 2);
        }

        return map;
    }

    /// <summary>The inverse map: twin name → original name.</summary>
    public NameMap Reverse()
    {
        var r = new NameMap();
        foreach (var (k, v) in Tables)
        {
            r.Tables[v] = k;
        }

        foreach (var (t, cols) in Columns)
        {
            r.Columns[Table(t)] = cols.ToDictionary(kv => kv.Value, kv => kv.Key, Cmp);
        }

        foreach (var (k, v) in Measures)
        {
            r.Measures[v] = k;
        }

        foreach (var (t, hs) in Hierarchies)
        {
            r.Hierarchies[Table(t)] = hs.ToDictionary(kv => kv.Value, kv => kv.Key, Cmp);
        }

        foreach (var (t, hs) in Levels)
        {
            r.Levels[Table(t)] = hs.ToDictionary(kv => Hierarchies[t][kv.Key], kv => kv.Value.ToDictionary(l => l.Value, l => l.Key, Cmp), Cmp);
        }

        foreach (var (k, v) in Roles)
        {
            r.Roles[v] = k;
        }

        return r;
    }

    /// <summary>Tables that have a column with this (original) name.</summary>
    public IEnumerable<string> TablesWithColumn(string column) =>
        Columns.Where(kv => kv.Value.ContainsKey(column)).Select(kv => kv.Key);

    private sealed record CodebookFile(
        string Note,
        Dictionary<string, string> Tables,
        Dictionary<string, Dictionary<string, string>> Columns,
        Dictionary<string, string> Measures,
        Dictionary<string, Dictionary<string, string>> Hierarchies,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> Levels,
        Dictionary<string, string> Roles);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(
        new CodebookFile(
            "PbiTwin codebook: original names -> twin names. Names only, no data. Do NOT share it together with the twin.",
            Tables, Columns, Measures, Hierarchies, Levels, Roles),
        JsonOptions);

    public static NameMap FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<CodebookFile>(json) ?? throw new InvalidDataException("Not a PbiTwin codebook.");
        var map = new NameMap();
        Copy(file.Tables, map.Tables);
        Copy(file.Measures, map.Measures);
        Copy(file.Roles, map.Roles);
        foreach (var (t, cols) in file.Columns ?? [])
        {
            map.Columns[t] = new Dictionary<string, string>(cols, Cmp);
        }

        foreach (var (t, hs) in file.Hierarchies ?? [])
        {
            map.Hierarchies[t] = new Dictionary<string, string>(hs, Cmp);
        }

        foreach (var (t, hs) in file.Levels ?? [])
        {
            map.Levels[t] = hs.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value, Cmp), Cmp);
        }

        return map;

        static void Copy(Dictionary<string, string>? from, Dictionary<string, string> to)
        {
            foreach (var (k, v) in from ?? [])
            {
                to[k] = v;
            }
        }
    }

    private static int Width(int count) => Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);

    private static string Number(int n, int width) => n.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>1 → A, 26 → Z, 27 → AA.</summary>
    private static string Letters(int n)
    {
        var s = string.Empty;
        while (n > 0)
        {
            n--;
            s = (char)('A' + (n % 26)) + s;
            n /= 26;
        }

        return s;
    }
}
