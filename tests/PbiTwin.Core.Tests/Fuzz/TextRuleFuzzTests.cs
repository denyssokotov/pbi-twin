using System.Text;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Tests.Fuzz;

/// <summary>Every text rule gets odd strings: empty, blanks, emoji, accents, very long, digits only, control characters.</summary>
public class TextRuleFuzzTests
{
    public static TheoryData<Rule> TextRules => new(
        Rule.Label, Rule.Key, Rule.PersonName, Rule.Email, Rule.Phone, Rule.Iban, Rule.Address, Rule.City, Rule.Country, Rule.FreeText);

    private static string Random(Random rng)
    {
        const string alphabet = " \t\n ​.,-+()/@_0123456789abcXYZáéíõüÿСвітІ😀😀漢字שלום\0́";
        var sb = new StringBuilder();
        for (var i = rng.Next(0, rng.Next(10) == 0 ? 3000 : 40); i > 0; i--)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(TextRules))]
    public void A_text_rule_handles_any_string_and_is_repeatable(Rule rule)
    {
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        var t = new Table { Name = "T" };
        t.Columns.Add(new DataColumn { Name = "V", DataType = DataType.String, SourceColumn = "V" });
        t.Partitions.Add(new Partition { Name = "T", Source = new MPartitionSource { Expression = "x" } });
        db.Model.Tables.Add(t);
        var plan = AnonymizationPlan.Create(ModelInfo.FromModel(db.Model), new Dictionary<string, ColumnProfile>(), _ => new HashSet<object>());
        plan.SetRule("T[V]", rule);

        var rng = new System.Random(77);
        var values = Enumerable.Range(0, 500).Select(_ => Random(rng)).ToList();

        string?[] Run()
        {
            var anonymizer = new Anonymizer(plan, new TwinOptions { Seed = "TEXT-RULE-FUZZ-1" });
            foreach (var domain in plan.Domains.Where(d => Rules.UsesDictionary(d.Rule)))
            {
                anonymizer.BuildDictionary(domain, values.Cast<object>());
            }

            var noise = anonymizer.NoiseFor("T");
            return values.Select(v => (string?)anonymizer.Transform(plan["T[V]"], v, noise)).ToArray();
        }

        var first = Run();
        Assert.Equal(first, Run());
        Assert.All(first, r => Assert.NotNull(r));
    }
}
