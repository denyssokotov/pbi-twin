using Microsoft.AnalysisServices.AdomdClient;
using PbiTwin.Core.Connection;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Extract;

/// <summary>Reads data from a local Power BI Desktop instance with DAX <c>EVALUATE</c> queries.</summary>
public sealed class AdomdTableReader : ITableReader, IDisposable
{
    private readonly AdomdConnection _connection;

    public AdomdTableReader(string connectionString)
    {
        LocalhostGuard.EnsureLocal(connectionString);
        _connection = new AdomdConnection(connectionString);
        _connection.Open();
    }

    public long CountRows(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var cmd = Command($"EVALUATE ROW(\"n\", COUNTROWS({Dax.Table(table.Name)}))");

        // AdomdCommand.ExecuteScalar is not implemented (always NotSupportedException), so read the single row.
        using var reader = cmd.ExecuteReader();
        return reader.Read() && reader.GetValue(0) is { } v and not DBNull
            ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    public IEnumerable<object?[]> ReadRows(TableInfo table, IReadOnlyList<ColumnInfo> columns, long? limit = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        var select = $"SELECTCOLUMNS({Dax.Table(table.Name)}, "
            + string.Join(", ", columns.Select((c, i) => $"\"c{i}\", {Dax.Column(table.Name, c.Name)}")) + ")";
        var query = limit is null ? $"EVALUATE {select}" : $"EVALUATE TOPN({limit.Value}, {select})";

        using var cmd = Command(query);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Normalize(reader.GetValue(i));
            }

            yield return row;
        }
    }

    public IEnumerable<object> ReadDistinct(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        using var cmd = Command($"EVALUATE DISTINCT({Dax.Column(column.Table, column.Name)})");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Normalize(reader.GetValue(0)) is { } v)
            {
                yield return v;
            }
        }
    }

    public void Dispose() => _connection.Dispose();

    private AdomdCommand Command(string dax) => new(dax, _connection) { CommandTimeout = 0 };

    private static object? Normalize(object? v) => v switch
    {
        null or DBNull => null,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        float f => (double)f,
        _ => v,
    };
}

/// <summary>Quoting for DAX identifiers.</summary>
public static class Dax
{
    public static string Table(string name) => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Column(string table, string column) => Table(table) + "[" + column.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
