using System.Text;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;
using PbiTwin.Core.Text;

namespace PbiTwin.Core.Tests.Fuzz;

/// <summary>Seeded random inputs for the text plumbing. The seeds are fixed, so a failure always reproduces.</summary>
public class FuzzTests
{
    private const int Rounds = 400;

    private static string Random(Random rng, string alphabet, int maxLength)
    {
        var sb = new StringBuilder();
        for (var i = rng.Next(maxLength + 1); i > 0; i--)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }

    [Fact]
    public void Tokenizing_and_joining_DAX_gives_back_the_same_text()
    {
        var rng = new Random(1);
        for (var i = 0; i < Rounds; i++)
        {
            var dax = Random(rng, "\"'[]/-*\n\r\t ab1(),=\\Київ", 60);
            Assert.Equal(dax, DaxTokenizer.Join(DaxTokenizer.Tokenize(dax)));
        }
    }

    [Fact]
    public void Rewriting_DAX_with_nothing_to_replace_changes_nothing()
    {
        var rng = new Random(2);
        var literals = new LiteralRewriter(new Dictionary<string, Dictionary<Domain, string>>());
        for (var i = 0; i < Rounds; i++)
        {
            var dax = Random(rng, "\"'[]/-*\n ab1(),=Київ", 60);
            Assert.Equal(dax, literals.RewriteDax(dax, "fuzz"));
        }
    }

    [Fact]
    public void Csv_values_survive_a_write_and_read()
    {
        var rng = new Random(3);
        var columns = new[]
        {
            new ColumnInfo("T", "A", ColumnType.Text, "A", false, false, null, false),
            new ColumnInfo("T", "B", ColumnType.Text, "B", false, false, null, false),
        };
        var path = Path.Combine(Path.GetTempPath(), "pbitwin-fuzz-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var rows = new List<string[]>();
            using (var writer = new CsvWriter(path, columns))
            {
                for (var i = 0; i < Rounds; i++)
                {
                    // At least one visible character: an empty text and a missing value are the same thing in CSV.
                    var row = new[] { "x" + Random(rng, ",\"\n\r ab1;Київ\t", 30) + "😀", "y" + Random(rng, ",\"\n ab1'", 30) };
                    rows.Add(row);
                    writer.Write([row[0], row[1]]);
                }
            }

            using var reader = new StreamReader(path, Csv.Utf8);
            var read = Csv.ReadRecords(reader).Skip(1).ToList();
            Assert.Equal(rows.Count, read.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal(rows[i], read[i]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Matcher_finds_every_whole_word_and_replacing_removes_it()
    {
        var rng = new Random(4);
        var matcher = new TextMatcher(["Kyiv", "Lviv-West", "Іваненко"]);
        for (var i = 0; i < Rounds; i++)
        {
            var text = Random(rng, " ,.-_Kyivabc12ЛвівІваненко", 50);
            var replaced = matcher.Replace(text, _ => "###");
            Assert.Empty(matcher.Find(replaced));
            Assert.Equal(matcher.Find(text).Count(), (replaced.Length - replaced.Replace("###", string.Empty, StringComparison.Ordinal).Length) / 3);
        }
    }
}
