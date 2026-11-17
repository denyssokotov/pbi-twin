using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Tests.Extract;

public sealed class CsvReaderErrorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    [Theory]
    [InlineData(ColumnType.Int64, "Kovalenko-Secret")]
    [InlineData(ColumnType.Decimal, "12,5 Kovalenko-Secret")]
    [InlineData(ColumnType.DateTime, "Kovalenko-Secret")]
    [InlineData(ColumnType.Boolean, "Kovalenko-Secret")]
    [InlineData(ColumnType.Int64, "99999999999999999999999")]
    public void A_value_that_does_not_parse_is_reported_without_the_value(ColumnType type, string bad)
    {
        File.WriteAllText(Path.Combine(_dir, "T.csv"), $"A\n1\n\"{bad}\"\n");
        var column = new ColumnInfo("T", "A", type, "A", false, false, null, false);
        var reader = new CsvTableReader(_dir);

        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadRows(new TableInfo("T", [column], false), [column]).ToList());

        Assert.DoesNotContain("Kovalenko", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("999999", ex.ToString(), StringComparison.Ordinal);
        Assert.Matches("line [23],", ex.Message);
        Assert.Contains("'A'", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
