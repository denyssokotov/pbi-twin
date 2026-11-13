using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;
using PbiTwin.Core.Tests.Guards;

namespace PbiTwin.Core.Tests.Anonymize;

public class RulesFileTests
{
    private static AnonymizationPlan ScanSample()
    {
        var sample = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
        return new TwinSession(PbipProject.Open(sample), new CsvTableReader(Path.Combine(sample, "data"), c => c.SourceColumn)).Scan();
    }

    [Fact]
    public void Saved_rules_restore_edits_on_a_fresh_scan()
    {
        var edited = ScanSample();
        edited.SetRule("DimCustomer[Segment]", Rule.Keep);
        edited.SetRule("FactSales[Comment]", Rule.Blank);
        edited.Unlink("DimStore[City]");
        var json = RulesFile.Save(edited);

        Assert.DoesNotContain("Kovalenko", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Kyiv", json, StringComparison.Ordinal);

        var fresh = ScanSample();
        Assert.Equal(Rule.Label, fresh["DimCustomer[Segment]"].Rule);

        var result = RulesFile.Apply(fresh, json);
        Assert.Equal(Rule.Keep, fresh["DimCustomer[Segment]"].Rule);
        Assert.Equal(Rule.Blank, fresh["FactSales[Comment]"].Rule);
        Assert.NotSame(fresh["DimStore[City]"].Domain, fresh["DimCustomer[City]"].Domain);
        Assert.Same(fresh["FactSales[CustomerKey]"].Domain, fresh["DimCustomer[CustomerKey]"].Domain);
        Assert.Empty(result.NotInModel);
        Assert.Empty(result.NotInFile);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Unknown_columns_and_invalid_rules_are_reported_not_applied()
    {
        var plan = ScanSample();
        const string json = """
            { "Note": "", "Columns": [
              { "Column": "Gone[Column]", "Rule": "Keep", "SameMappingAs": null },
              { "Column": "FactSales[OrderDate]", "Rule": "Email", "SameMappingAs": null },
              { "Column": "DimProduct[Brand]", "Rule": "keep", "SameMappingAs": null }
            ] }
            """;

        var result = RulesFile.Apply(plan, json);

        Assert.Equal(["Gone[Column]"], result.NotInModel);
        Assert.Equal(["FactSales[OrderDate]: Email"], result.Rejected);
        Assert.Equal(Rule.DateShift, plan["FactSales[OrderDate]"].Rule);
        Assert.Equal(Rule.Keep, plan["DimProduct[Brand]"].Rule);
        Assert.Equal(1, result.Applied);
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("FactSales", "FactSales.csv")]
    [InlineData("Dim Customer", "Dim Customer.csv")]
    [InlineData("A/B", "A%2FB.csv")]
    [InlineData("A_B", "A_B.csv")]
    [InlineData("50%", "50%25.csv")]
    [InlineData("Con", "%43on.csv")]
    [InlineData("nul", "%6Eul.csv")]
    [InlineData("Console", "Console.csv")]
    [InlineData("Sales.", "Sales%2E.csv")]
    [InlineData("Що: продажі?", "Що%3A продажі%3F.csv")]
    public void Csv_file_names_are_valid_and_one_to_one(string table, string expected) =>
        Assert.Equal(expected, ModelRewriter.CsvFileName(table));
}

public class NumberLiteralWarningTests
{
    [Fact]
    public void Warns_when_a_number_is_compared_with_a_changed_column()
    {
        var lit = new LiteralRewriter(new Dictionary<string, Dictionary<Domain, string>>(), ruleOf: (table, column) => column switch
        {
            "CustomerKey" => Rule.Key,
            "Amount" => Rule.Money,
            "Year" => Rule.Keep,
            _ => null,
        });

        lit.RewriteDax("CALCULATE([R], Sales[Year] = 2024)", "a");
        lit.RewriteDax("COUNTROWS(FILTER(Sales, Sales[Amount] > 1000))", "b");
        lit.RewriteDax("CALCULATE([R], Customer[CustomerKey] IN { 1, 2 })", "c");

        Assert.Equal(2, lit.Warnings.Count);
        Assert.StartsWith("b ", lit.Warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("c ", lit.Warnings[1], StringComparison.Ordinal);
    }
}
