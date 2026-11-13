using System.Globalization;
using System.Text;

namespace PbiTwin.SampleGenerator;

/// <summary>Minimal RFC 4180 CSV writer: UTF-8 without BOM, comma separator, invariant culture, LF line endings.</summary>
internal sealed class CsvFile : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvFile(string path, params string[] header)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        Row(header);
    }

    public void Row(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                _writer.Write(',');
            }

            _writer.Write(Escape(Format(values[i])));
        }

        _writer.WriteLine();
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
        double x => x.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    public void Dispose() => _writer.Dispose();
}
