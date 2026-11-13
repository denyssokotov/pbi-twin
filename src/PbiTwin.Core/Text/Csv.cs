using System.Globalization;
using System.Text;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Text;

/// <summary>
/// RFC 4180 CSV in the one dialect PbiTwin writes: UTF-8 without BOM, comma, invariant culture,
/// ISO dates, '.' decimal separator, LF line endings, empty field = null.
/// </summary>
public static class Csv
{
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Escape(string s) =>
        s.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    public static string Format(object? value, ColumnInfo column) => value switch
    {
        null => string.Empty,
        string s => s,
        DateTime d when column.IsDateOnly => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static object? Parse(string? s, ColumnType type)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var c = CultureInfo.InvariantCulture;
        return type switch
        {
            ColumnType.Int64 => long.Parse(s, NumberStyles.Integer, c),
            ColumnType.Double => double.Parse(s, NumberStyles.Float, c),
            ColumnType.Decimal => decimal.Parse(s, NumberStyles.Float, c),
            ColumnType.DateTime => DateTime.Parse(s, c, DateTimeStyles.RoundtripKind),
            ColumnType.Boolean => bool.Parse(s),
            _ => s,
        };
    }

    /// <summary>Streams records from a CSV file. Quoted fields may contain commas, quotes and line breaks.</summary>
    public static IEnumerable<string?[]> ReadRecords(TextReader reader)
    {
        var fields = new List<string?>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        var any = false;

        int ch;
        while ((ch = reader.Read()) != -1)
        {
            any = true;
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        sb.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    quoted = true;
                    break;
                case ',':
                    fields.Add(EndField(sb, quoted));
                    quoted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(EndField(sb, quoted));
                    quoted = false;
                    yield return fields.ToArray();
                    fields.Clear();
                    any = false;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        if (any)
        {
            fields.Add(EndField(sb, quoted));
            yield return fields.ToArray();
        }
    }

    private static string? EndField(StringBuilder sb, bool quoted)
    {
        var s = sb.ToString();
        sb.Clear();
        return s.Length == 0 && !quoted ? null : s;
    }
}

/// <summary>Writes one table to CSV, row by row.</summary>
public sealed class CsvWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly IReadOnlyList<ColumnInfo> _columns;

    public CsvWriter(string path, IReadOnlyList<ColumnInfo> columns)
    {
        _columns = columns;
        _writer = new StreamWriter(path, append: false, Csv.Utf8) { NewLine = "\n" };
        _writer.WriteLine(string.Join(',', columns.Select(c => Csv.Escape(c.Name))));
    }

    public void Write(object?[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (i > 0)
            {
                _writer.Write(',');
            }

            _writer.Write(Csv.Escape(Csv.Format(row[i], _columns[i])));
        }

        _writer.WriteLine();
    }

    public void Dispose() => _writer.Dispose();
}
