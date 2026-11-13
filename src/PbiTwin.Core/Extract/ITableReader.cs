using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Extract;

/// <summary>
/// Source of table data. Values are null, string, long, double, decimal, DateTime or bool.
/// </summary>
public interface ITableReader
{
    long CountRows(TableInfo table);

    /// <summary>Streams rows; values are in the order of <paramref name="columns"/>.</summary>
    IEnumerable<object?[]> ReadRows(TableInfo table, IReadOnlyList<ColumnInfo> columns, long? limit = null);

    /// <summary>Distinct non-null values of one column.</summary>
    IEnumerable<object> ReadDistinct(ColumnInfo column);
}
