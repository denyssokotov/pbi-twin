using System.Text;
using System.Globalization;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using ColumnType = PbiTwin.Core.Metadata.ColumnType;

namespace PbiTwin.Core.Output;

/// <summary>
/// Rewrites a TOM model so it loads the twin's CSV files and contains no trace of the original sources:
/// every data partition reads <c>DataFolder\Table.csv</c>, every original connection, shared query and
/// data source is removed, and string literals in DAX are mapped to pseudonyms.
/// </summary>
public static class ModelRewriter
{
    public const string DataFolderParameter = "DataFolder";

    /// <summary>Annotations that change model behaviour and are therefore kept when cleaning.</summary>
    private static readonly HashSet<string> FunctionalAnnotations = new(StringComparer.Ordinal)
    {
        "__PBI_TimeIntelligenceEnabled",
        "PBI_ResultType",
        "PBI_QueryOrder",
        "UnderlyingDateTimeDataType",
        "SummarizationSetBy",
        "PBI_FormatHint",
        "PBIDesktopVersion",
        "__PBI_SemanticLinkEnabled",
    };

    public static void Rewrite(Model model, ModelInfo info, string dataFolder, LiteralRewriter literals, bool clearDescriptions, INames names)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(literals);
        ArgumentNullException.ThrowIfNull(names);

        model.DefaultMode = ModeType.Import;

        foreach (var t in info.Tables)
        {
            var table = model.Tables[t.Name];
            table.RefreshPolicy = null;
            table.Partitions.Clear();
            table.Partitions.Add(new Partition
            {
                Name = t.Name,
                Mode = ModeType.Import,
                Source = new MPartitionSource { Expression = CsvQuery(Renamed(t, names)) },
            });

            // The CSV header carries the twin's column name.
            foreach (var c in table.Columns.OfType<DataColumn>())
            {
                c.SourceColumn = names.Column(t.Name, c.Name);
            }
        }

        // Shared queries, parameters, legacy data sources and query groups may all hold server names,
        // file paths or credentials. None are needed by the twin.
        model.Expressions.Clear();
        model.DataSources.Clear();
        model.QueryGroups.Clear();
        model.Expressions.Add(new NamedExpression
        {
            Name = DataFolderParameter,
            Kind = ExpressionKind.M,
            Expression = MString(dataFolder) + " meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]",
            Annotations = { new Annotation { Name = "PBI_ResultType", Value = "Text" } },
        });

        var queryOrder = model.Annotations.Find("PBI_QueryOrder");
        if (queryOrder is not null)
        {
            queryOrder.Value = System.Text.Json.JsonSerializer.Serialize(new[] { DataFolderParameter }.Concat(info.Tables.Select(t => names.Table(t.Name))));
        }

        DaxSites.ForEach(model, names, (dax, site) => literals.RewriteDax(dax, site.Location));

        if (clearDescriptions)
        {
            Clean(model);
        }
        else
        {
            foreach (var o in Describables(model))
            {
                o.Description = literals.RewriteText(o.Description, "Description of " + o.Name);
            }
        }
    }

    /// <summary>The table as it appears in the twin (names mapped).</summary>
    public static TableInfo Renamed(TableInfo table, INames names)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(names);
        var name = names.Table(table.Name);
        return table with { Name = name, Columns = table.Columns.Select(c => c with { Table = name, Name = names.Column(table.Name, c.Name) }).ToList() };
    }

    /// <summary>Power Query that reads one twin CSV with explicit types and invariant culture.</summary>
    public static string CsvQuery(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var types = string.Join(", ", table.Columns.Select(c => $"{{{MString(c.Name)}, {MType(c)}}}"));
        var file = MString("\\" + CsvFileName(table.Name));
        var sb = new StringBuilder();
        sb.Append("let\n");
        sb.Append(CultureInfo.InvariantCulture, $"    Source = Csv.Document(File.Contents({DataFolderParameter} & {file}), [Delimiter=\",\", Columns={table.Columns.Count}, Encoding=65001, QuoteStyle=QuoteStyle.Csv]),\n");
        sb.Append("    Promoted = Table.PromoteHeaders(Source, [PromoteAllScalars=true]),\n");
        sb.Append(CultureInfo.InvariantCulture, $"    Typed = Table.TransformColumnTypes(Promoted, {{{types}}}, \"en-US\")\n");
        sb.Append("in\n");
        sb.Append("    Typed");
        return sb.ToString();
    }

    private static readonly HashSet<char> InvalidFileChars =
        [.. Path.GetInvalidFileNameChars(), '\\', '/', ':', '*', '?', '"', '<', '>', '|', '%'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// File name for a table's CSV, valid on Windows and one-to-one: characters a file name cannot hold (and '%'),
    /// a trailing dot or space, and the first letter of reserved names like CON are written as %XX.
    /// </summary>
    public static string CsvFileName(string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var sb = new System.Text.StringBuilder(table.Length + 4);
        for (var i = 0; i < table.Length; i++)
        {
            var c = table[i];
            var last = i == table.Length - 1;
            var reservedStart = i == 0 && ReservedNames.Contains(table.Split('.')[0]);
            if (InvalidFileChars.Contains(c) || c < ' ' || (last && c is '.' or ' ') || reservedStart)
            {
                sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append(".csv").ToString();
    }

    private static string MType(ColumnInfo c) => c.Type switch
    {
        ColumnType.Text => "type text",
        ColumnType.Int64 => "Int64.Type",
        ColumnType.Double => "type number",
        ColumnType.Decimal => "Currency.Type",
        ColumnType.DateTime => c.IsDateOnly ? "type date" : "type datetime",
        ColumnType.Boolean => "type logical",
        _ => "type any",
    };

    /// <summary>
    /// An M text literal. Inside M strings <c>#(</c> starts an escape such as <c>#(lf)</c>, so a literal <c>#(</c>
    /// is written <c>#(#)(</c>; line breaks and tabs are written as escapes too.
    /// </summary>
    internal static string MString(string s) => "\""
        + s.Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("#(", "#(#)(", StringComparison.Ordinal)
            .Replace("\r", "#(cr)", StringComparison.Ordinal)
            .Replace("\n", "#(lf)", StringComparison.Ordinal)
            .Replace("\t", "#(tab)", StringComparison.Ordinal)
        + "\"";

    private static void Clean(Model model)
    {
        foreach (var o in Describables(model))
        {
            o.Description = null;
        }

        foreach (var annotations in AnnotationCollections(model))
        {
            foreach (var a in annotations.ToList().Where(a => !FunctionalAnnotations.Contains(a.Name)))
            {
                annotations.Remove(a);
            }
        }

        foreach (var culture in model.Cultures)
        {
            culture.LinguisticMetadata = null;
        }
    }

    private sealed record Describable(string Name, Func<string?> Get, Action<string?> Set)
    {
        public string? Description
        {
            get => Get();
            set => Set(value);
        }
    }

    private static IEnumerable<Describable> Describables(Model model)
    {
        yield return new("model", () => model.Description, v => model.Description = v);
        foreach (var t in model.Tables)
        {
            yield return new(t.Name, () => t.Description, v => t.Description = v);
            foreach (var c in t.Columns)
            {
                yield return new(c.Name, () => c.Description, v => c.Description = v);
            }

            foreach (var m in t.Measures)
            {
                yield return new(m.Name, () => m.Description, v => m.Description = v);
            }

            foreach (var h in t.Hierarchies)
            {
                yield return new(h.Name, () => h.Description, v => h.Description = v);
                foreach (var l in h.Levels)
                {
                    yield return new(l.Name, () => l.Description, v => l.Description = v);
                }
            }

            foreach (var p in t.Partitions)
            {
                yield return new(p.Name, () => p.Description, v => p.Description = v);
            }

            if (t.CalculationGroup is not null)
            {
                yield return new(t.Name, () => t.CalculationGroup.Description, v => t.CalculationGroup.Description = v);
                foreach (var i in t.CalculationGroup.CalculationItems)
                {
                    yield return new(i.Name, () => i.Description, v => i.Description = v);
                }
            }
        }

        foreach (var r in model.Roles)
        {
            yield return new(r.Name, () => r.Description, v => r.Description = v);
        }

        foreach (var e in model.Expressions)
        {
            yield return new(e.Name, () => e.Description, v => e.Description = v);
        }

        foreach (var p in model.Perspectives)
        {
            yield return new(p.Name, () => p.Description, v => p.Description = v);
        }
    }

    private static IEnumerable<ICollection<Annotation>> AnnotationCollections(Model model)
    {
        yield return model.Annotations;
        foreach (var t in model.Tables)
        {
            yield return t.Annotations;
            foreach (var c in t.Columns)
            {
                yield return c.Annotations;
            }

            foreach (var m in t.Measures)
            {
                yield return m.Annotations;
            }

            foreach (var h in t.Hierarchies)
            {
                yield return h.Annotations;
            }

            foreach (var p in t.Partitions)
            {
                yield return p.Annotations;
            }
        }

        foreach (var e in model.Expressions)
        {
            yield return e.Annotations;
        }

        foreach (var r in model.Roles)
        {
            yield return r.Annotations;
        }
    }
}
