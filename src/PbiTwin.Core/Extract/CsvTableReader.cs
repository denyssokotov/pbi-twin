using PbiTwin.Core.Metadata;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Extract;

/// <summary>
/// Reads tables from a folder of <c>&lt;Table&gt;.csv</c> files. Used to read a twin back for verification,
/// and to run the whole pipeline offline against CSV-based models (e.g. the ContosoPII sample).
/// </summary>
public sealed class CsvTableReader(string folder, Func<ColumnInfo, string>? header = null) : ITableReader
{
    private readonly Func<ColumnInfo, string> _header = header ?? (c => c.Name);

    public long CountRows(TableInfo table) => Records(table.Name).LongCount();

    public IEnumerable<object?[]> ReadRows(TableInfo table, IReadOnlyList<ColumnInfo> columns, long? limit = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        using var reader = Open(table.Name);
        using var records = Csv.ReadRecords(reader).GetEnumerator();
        if (!records.MoveNext())
        {
            yield break;
        }

        var headers = records.Current;
        var index = columns.Select(c =>
        {
            var i = Array.IndexOf(headers, _header(c));
            return i >= 0 ? i : throw new InvalidDataException($"Column '{_header(c)}' not found in {table.Name}.csv.");
        }).ToArray();

        long n = 0;
        while ((limit is null || n < limit) && records.MoveNext())
        {
            var r = records.Current;
            var row = new object?[index.Length];
            for (var i = 0; i < index.Length; i++)
            {
                try
                {
                    row[i] = Csv.Parse(index[i] < r.Length ? r[index[i]] : null, columns[i].Type);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    // The framework's message quotes the value; an error must never carry data, so it is left out (and not chained).
                    throw new InvalidDataException($"{table.Name}.csv, line {n + 2}, column '{_header(columns[i])}': the value is not a valid {columns[i].Type}. The value is not shown.");
                }
            }

            n++;
            yield return row;
        }
    }

    public IEnumerable<object> ReadDistinct(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var table = new TableInfo(column.Table, [column], IsFact: false);
        var seen = new HashSet<object>();
        foreach (var row in ReadRows(table, [column]))
        {
            if (row[0] is { } v && seen.Add(v))
            {
                yield return v;
            }
        }
    }

    private IEnumerable<string?[]> Records(string table)
    {
        using var reader = Open(table);
        foreach (var r in Csv.ReadRecords(reader).Skip(1))
        {
            yield return r;
        }
    }

    private StreamReader Open(string table) => new(Path.Combine(folder, table + ".csv"), Csv.Utf8, detectEncodingFromByteOrderMarks: true);
}
