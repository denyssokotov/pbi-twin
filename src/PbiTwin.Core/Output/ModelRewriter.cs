using System.Text;
using System.Globalization;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Metadata;
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

    public static void Rewrite(Model model, ModelInfo info, string dataFolder, LiteralRewriter literals, bool clearDescriptions)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(literals);

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
                Source = new MPartitionSource { Expression = CsvQuery(t) },
            });

            foreach (var c in table.Columns.OfType<DataColumn>())
            {
                c.SourceColumn = c.Name;
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
            queryOrder.Value = System.Text.Json.JsonSerializer.Serialize(new[] { DataFolderParameter }.Concat(info.Tables.Select(t => t.Name)));
        }

        RewriteDax(model, literals);

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

    public static string CsvFileName(string table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        return new string(table.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".csv";
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

    private static string MString(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void RewriteDax(Model model, LiteralRewriter lit)
    {
        foreach (var table in model.Tables)
        {
            var tn = $"'{table.Name}'";
            foreach (var m in table.Measures)
            {
                var where = $"Measure {tn}[{m.Name}]";
                m.Expression = lit.RewriteDax(m.Expression, where);
                if (m.FormatStringDefinition is not null)
                {
                    m.FormatStringDefinition.Expression = lit.RewriteDax(m.FormatStringDefinition.Expression, where + " format string");
                }

                if (m.DetailRowsDefinition is not null)
                {
                    m.DetailRowsDefinition.Expression = lit.RewriteDax(m.DetailRowsDefinition.Expression, where + " detail rows");
                }

                if (m.KPI is not null)
                {
                    m.KPI.TargetExpression = lit.RewriteDax(m.KPI.TargetExpression, where + " KPI target");
                    m.KPI.StatusExpression = lit.RewriteDax(m.KPI.StatusExpression, where + " KPI status");
                    m.KPI.TrendExpression = lit.RewriteDax(m.KPI.TrendExpression, where + " KPI trend");
                }
            }

            foreach (var c in table.Columns.OfType<CalculatedColumn>())
            {
                c.Expression = lit.RewriteDax(c.Expression, $"Calculated column {tn}[{c.Name}]");
            }

            foreach (var p in table.Partitions)
            {
                if (p.Source is CalculatedPartitionSource cps)
                {
                    cps.Expression = lit.RewriteDax(cps.Expression, $"Calculated table {tn}");
                }
            }

            if (table.CalculationGroup is not null)
            {
                foreach (var item in table.CalculationGroup.CalculationItems)
                {
                    var where = $"Calculation item {tn}[{item.Name}]";
                    item.Expression = lit.RewriteDax(item.Expression, where);
                    if (item.FormatStringDefinition is not null)
                    {
                        item.FormatStringDefinition.Expression = lit.RewriteDax(item.FormatStringDefinition.Expression, where + " format string");
                    }
                }
            }

            if (table.DefaultDetailRowsDefinition is not null)
            {
                table.DefaultDetailRowsDefinition.Expression = lit.RewriteDax(table.DefaultDetailRowsDefinition.Expression, $"Detail rows {tn}");
            }
        }

        foreach (var role in model.Roles)
        {
            foreach (var tp in role.TablePermissions)
            {
                tp.FilterExpression = lit.RewriteDax(tp.FilterExpression, $"Role '{role.Name}' filter on '{tp.Name}'");
            }
        }
    }

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
