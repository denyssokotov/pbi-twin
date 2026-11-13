using PbiTwin.Core.Metadata;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Tests.Text;

public class CsvTests
{
    private static readonly ColumnInfo TextCol = new("T", "C", ColumnType.Text, "C", false, false, null, false);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("", "")]
    public void Escape_follows_rfc4180(string input, string expected) => Assert.Equal(expected, Csv.Escape(input));

    [Fact]
    public void Round_trip_through_writer_and_reader()
    {
        var path = Path.GetTempFileName();
        try
        {
            string?[] values = ["plain", "a,b", "say \"hi\"", "multi\nline", null, "ünïcödé ✓"];
            using (var w = new CsvWriter(path, [TextCol]))
            {
                foreach (var v in values)
                {
                    w.Write([v]);
                }
            }

            using var r = new StreamReader(path);
            var records = Csv.ReadRecords(r).Skip(1).Select(x => x[0]).ToList();
            Assert.Equal(values, records);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Values_use_invariant_culture_and_iso_dates()
    {
        var date = new ColumnInfo("T", "D", ColumnType.DateTime, "D", false, false, null, true);
        var dateTime = date with { IsDateOnly = false };
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
        try
        {
            Assert.Equal("1234.5", Csv.Format(1234.5m, TextCol));
            Assert.Equal("0.25", Csv.Format(0.25, TextCol));
            Assert.Equal("2024-11-29", Csv.Format(new DateTime(2024, 11, 29), date));
            Assert.Equal("2024-11-29T13:45:00", Csv.Format(new DateTime(2024, 11, 29, 13, 45, 0), dateTime));
            Assert.Equal(1234.5m, Csv.Parse("1234.5", ColumnType.Decimal));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}

public class DaxTokenizerTests
{
    [Fact]
    public void Splits_strings_names_and_comments()
    {
        const string dax = "CALCULATE([Revenue], 'Dim Customer'[Segment] = \"Enter\"\"prise\") // note \"x\"\n-- \"y\"\n/* \"z\" */ + [A]]B]";
        var tokens = DaxTokenizer.Tokenize(dax);

        Assert.Equal(dax, DaxTokenizer.Join(tokens));
        Assert.Equal(["Enter\"prise"], tokens.Where(t => t.Kind == DaxTokenKind.String).Select(t => t.Value));
        Assert.Equal(["Dim Customer"], tokens.Where(t => t.Kind == DaxTokenKind.QuotedName).Select(t => t.Value));
        Assert.Equal(["Revenue", "Segment", "A]B"], tokens.Where(t => t.Kind == DaxTokenKind.BracketName).Select(t => t.Value));
        Assert.Equal(3, tokens.Count(t => t.Kind == DaxTokenKind.Comment));
    }

    [Fact]
    public void Quote_inside_string_is_not_a_table_name()
    {
        var tokens = DaxTokenizer.Tokenize("\"it's [not] a name\"");
        Assert.Single(tokens);
        Assert.Equal(DaxTokenKind.String, tokens[0].Kind);
    }
}

public class TextMatcherTests
{
    [Fact]
    public void Finds_whole_words_case_insensitively()
    {
        var m = new TextMatcher(["Kyiv", "Olena Kovalenko", "abc"]);

        Assert.Single(m.Find("Delivered to KYIV, today"));
        Assert.Empty(m.Find("Kyivska street"));
        Assert.Single(m.Find("Call Olena Kovalenko."));
        Assert.Empty(m.Find("abc is too short to search"));
        Assert.Equal("to [X], today", m.Replace("to Kyiv, today", _ => "[X]"));
    }

    [Fact]
    public void Prefers_the_longest_pattern()
    {
        var m = new TextMatcher(["Olena", "Olena Kovalenko"]);
        var match = Assert.Single(m.Find("for Olena Kovalenko"));
        Assert.Equal("Olena Kovalenko", match.Pattern);
    }
}
