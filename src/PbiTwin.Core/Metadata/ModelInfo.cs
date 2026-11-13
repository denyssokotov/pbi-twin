using Microsoft.AnalysisServices.Tabular;
using TomDataType = Microsoft.AnalysisServices.Tabular.DataType;

namespace PbiTwin.Core.Metadata;

public enum ColumnType
{
    Text,
    Int64,
    Double,
    Decimal,
    DateTime,
    Boolean,
    Other,
}

public sealed record ColumnInfo(
    string Table,
    string Name,
    ColumnType Type,
    string SourceColumn,
    bool IsKey,
    bool IsHidden,
    string? DataCategory,
    bool IsDateOnly)
{
    public string Id => $"{Table}[{Name}]";

    public bool IsNumeric => Type is ColumnType.Int64 or ColumnType.Double or ColumnType.Decimal;
}

public sealed record TableInfo(string Name, IReadOnlyList<ColumnInfo> Columns, bool IsFact);

public sealed record RelationshipInfo(string FromTable, string FromColumn, string ToTable, string ToColumn, bool IsActive)
{
    public string FromId => $"{FromTable}[{FromColumn}]";

    public string ToId => $"{ToTable}[{ToColumn}]";
}

/// <summary>What PbiTwin needs to know about a tabular model, read through TOM.</summary>
public sealed class ModelInfo
{
    private ModelInfo(List<TableInfo> tables, List<(string Table, string Reason)> skipped, List<RelationshipInfo> relationships)
    {
        Tables = tables;
        SkippedTables = skipped;
        Relationships = relationships;
    }

    /// <summary>Tables whose data is extracted and anonymized.</summary>
    public IReadOnlyList<TableInfo> Tables { get; }

    /// <summary>Tables that are recalculated from DAX (or auto-generated) and therefore not extracted.</summary>
    public IReadOnlyList<(string Table, string Reason)> SkippedTables { get; }

    /// <summary>Relationships between extracted data columns.</summary>
    public IReadOnlyList<RelationshipInfo> Relationships { get; }

    public IEnumerable<ColumnInfo> AllColumns => Tables.SelectMany(t => t.Columns);

    public TableInfo Table(string name) => Tables.First(t => t.Name == name);

    public static ModelInfo FromModel(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var skipped = new List<(string, string)>();
        var extracted = new List<Table>();
        foreach (var t in model.Tables)
        {
            var reason = SkipReason(t);
            if (reason is null)
            {
                extracted.Add(t);
            }
            else
            {
                skipped.Add((t.Name, reason));
            }
        }

        var names = extracted.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var relationships = model.Relationships.OfType<SingleColumnRelationship>()
            .Where(r => r.FromColumn is DataColumn && r.ToColumn is DataColumn)
            .Where(r => names.Contains(r.FromTable.Name) && names.Contains(r.ToTable.Name))
            .Select(r => new RelationshipInfo(r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name, r.IsActive))
            .ToList();

        var manySide = relationships.Select(r => r.FromTable).ToHashSet(StringComparer.Ordinal);
        var oneSide = relationships.Select(r => r.ToTable).ToHashSet(StringComparer.Ordinal);

        var tables = extracted
            .Select(t => new TableInfo(
                t.Name,
                t.Columns.OfType<DataColumn>().Select(c => ToInfo(t.Name, c)).ToList(),
                IsFact: manySide.Contains(t.Name) && !oneSide.Contains(t.Name)))
            .ToList();

        return new ModelInfo(tables, skipped, relationships);
    }

    private static string? SkipReason(Table t)
    {
        if (t.Name.StartsWith("LocalDateTable_", StringComparison.Ordinal) || t.Name.StartsWith("DateTableTemplate_", StringComparison.Ordinal))
        {
            return "auto date table";
        }

        if (t.CalculationGroup is not null)
        {
            return "calculation group";
        }

        if (t.Partitions.Count == 0)
        {
            return "no partitions";
        }

        return t.Partitions[0].SourceType switch
        {
            PartitionSourceType.Calculated => "calculated table (recalculated from DAX)",
            PartitionSourceType.CalculationGroup => "calculation group",
            PartitionSourceType.M or PartitionSourceType.Query or PartitionSourceType.Entity or PartitionSourceType.PolicyRange => null,
            _ => "unsupported partition type " + t.Partitions[0].SourceType,
        };
    }

    private static ColumnInfo ToInfo(string table, DataColumn c)
    {
        var type = c.DataType switch
        {
            TomDataType.String => ColumnType.Text,
            TomDataType.Int64 => ColumnType.Int64,
            TomDataType.Double => ColumnType.Double,
            TomDataType.Decimal => ColumnType.Decimal,
            TomDataType.DateTime => ColumnType.DateTime,
            TomDataType.Boolean => ColumnType.Boolean,
            _ => ColumnType.Other,
        };

        var dateOnly = type == ColumnType.DateTime
            && (c.Annotations.Find("UnderlyingDateTimeDataType")?.Value == "Date"
                || c.FormatString is "Short Date" or "Long Date" or "yyyy-mm-dd" or "dd/mm/yyyy" or "mm/dd/yyyy");

        return new ColumnInfo(
            table,
            c.Name,
            type,
            string.IsNullOrEmpty(c.SourceColumn) ? c.Name : c.SourceColumn,
            c.IsKey,
            c.IsHidden,
            string.IsNullOrEmpty(c.DataCategory) ? null : c.DataCategory,
            dateOnly);
    }
}
