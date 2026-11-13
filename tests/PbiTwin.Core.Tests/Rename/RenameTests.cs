using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Tests.Guards;
using PbiTwin.Core.Verify;

namespace PbiTwin.Core.Tests.Rename;

public class DaxRenamerTests
{
    private static NameMap Map()
    {
        var map = new NameMap();
        map.Tables["FactSales"] = "Table01";
        map.Tables["Dim Customer"] = "Table02";
        map.Tables["DimDate"] = "Table03";
        map.Columns["FactSales"] = new(StringComparer.OrdinalIgnoreCase) { ["SalesAmount"] = "Col01", ["Quantity"] = "Col02", ["Price]x"] = "Col03" };
        map.Columns["Dim Customer"] = new(StringComparer.OrdinalIgnoreCase) { ["Segment"] = "Col01", ["Name"] = "Col02" };
        map.Columns["DimDate"] = new(StringComparer.OrdinalIgnoreCase) { ["Date"] = "Col01", ["Year"] = "Col02" };
        map.Measures["Revenue"] = "Measure01";
        map.Measures["Margin %"] = "Measure02";
        return map;
    }

    [Theory]
    [InlineData("SUM(FactSales[SalesAmount])", "SUM(Table01[Col01])")]
    [InlineData("CALCULATE([Revenue], 'Dim Customer'[Segment] = \"Enterprise\")", "CALCULATE([Measure01], 'Table02'[Col01] = \"Enterprise\")")]
    [InlineData("COUNTROWS(FactSales)", "COUNTROWS(Table01)")]
    [InlineData("'FactSales'[Revenue]", "'Table01'[Measure01]")]
    [InlineData("SUM(FactSales[Price]]x])", "SUM(Table01[Col03])")]
    [InlineData("VAR x = [Revenue] // [Revenue] in a comment\nRETURN x", "VAR x = [Measure01] // [Revenue] in a comment\nRETURN x")]
    [InlineData("\"FactSales[SalesAmount] stays text\"", "\"FactSales[SalesAmount] stays text\"")]
    [InlineData("CALCULATE(CALCULATE([Revenue], ALL(DimDate)), KEEPFILTERS(DimDate[Year] = 2024))", "CALCULATE(CALCULATE([Measure01], ALL(Table03)), KEEPFILTERS(Table03[Col02] = 2024))")]
    [InlineData("IF(SELECTEDMEASURENAME() = \"Margin %\", 1, 0)", "IF(SELECTEDMEASURENAME() = \"Measure02\", 1, 0)")]
    [InlineData("IF([Revenue] > 0, \"Margin %\", \"x\")", "IF([Measure01] > 0, \"Margin %\", \"x\")")]
    public void Renames_references(string dax, string expected) => Assert.Equal(expected, DaxRenamer.Rename(dax, Map()));

    [Fact]
    public void Unqualified_columns_resolve_to_the_home_table()
    {
        Assert.Equal("SUMX(Table01, [Col02] * 2)", DaxRenamer.Rename("SUMX(FactSales, [Quantity] * 2)", Map(), homeTable: "FactSales"));
    }

    [Fact]
    public void Virtual_columns_of_calculated_tables_are_left_alone()
    {
        const string dax = "ADDCOLUMNS(CALENDAR(DATE(2020,1,1), DATE(2024,12,31)), \"Year\", YEAR([Date]))";
        var result = DaxRenamer.Rename(dax, Map(), homeTable: "DimDate", anyTableFallback: false);
        Assert.Contains("YEAR([Date])", result, StringComparison.Ordinal);
        Assert.Contains("\"Year\"", result, StringComparison.Ordinal);

        Assert.Contains("[Date]", DaxRenamer.Rename("COUNTROWS(FILTER(CALENDAR(1, 2), [Date] > 1))", Map()), StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_round_trips_and_reports_unknown_names()
    {
        const string real = "VAR s = CALCULATE([Revenue], 'Dim Customer'[Segment] = \"A\") RETURN DIVIDE(s, SUM(FactSales[SalesAmount]))";
        var map = Map();
        var anon = DaxTranslator.ToAnonymous(real, map);

        Assert.DoesNotContain("Revenue", anon, StringComparison.Ordinal);
        Assert.DoesNotContain("Dim Customer", anon, StringComparison.Ordinal);
        Assert.Equal(real, DaxTranslator.ToReal(anon, map));

        var unknown = new List<string>();
        DaxTranslator.ToReal("[Measure99] + Table01[Col01]", map, unknown);
        Assert.Equal(["Measure99"], unknown);
    }

    [Fact]
    public void Quotes_real_table_names_with_spaces_when_translating_back()
    {
        Assert.Equal("COUNTROWS('Dim Customer')", DaxTranslator.ToReal("COUNTROWS(Table02)", Map()));
    }

    [Fact]
    public void Pbir_query_refs_are_rewritten()
    {
        Assert.Equal("Table01.Measure01", ReportRenamer.QueryRef("FactSales.Revenue", Map()));
        Assert.Equal("Sum(Table01.Col01)", ReportRenamer.QueryRef("Sum(FactSales.SalesAmount)", Map()));
        Assert.Equal("Table02.Col01", ReportRenamer.QueryRef("Dim Customer.Segment", Map()));
    }
}

public class DaxReferenceCheckTests
{
    [Fact]
    public void Flags_references_to_missing_objects_only()
    {
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        var t = new Table { Name = "Sales" };
        t.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal, SourceColumn = "Amount" });
        t.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "x" } });
        t.Measures.Add(new Measure { Name = "Total", Expression = "SUM(Sales[Amount])" });
        db.Model.Tables.Add(t);

        Assert.Equal(CheckStatus.Pass, PbiTwin.Core.Verify.DaxReferenceCheck.Run(db.Model).Status);

        t.Measures.Add(new Measure { Name = "Broken", Expression = "[Total] + SUM(Sales[Col07])" });
        var check = PbiTwin.Core.Verify.DaxReferenceCheck.Run(db.Model);
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("[Col07]", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_that_several_tables_have_is_not_reported_as_missing()
    {
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        foreach (var name in new[] { "Staff", "Goals", "Facts" })
        {
            var t = new Table { Name = name };
            if (name != "Facts")
            {
                t.Columns.Add(new DataColumn { Name = "Practice", DataType = DataType.String, SourceColumn = "Practice" });
            }

            t.Partitions.Add(new Partition { Name = name, Source = new MPartitionSource { Expression = "x" } });
            db.Model.Tables.Add(t);
        }

        db.Model.Tables["Facts"].Measures.Add(new Measure { Name = "Debug", Expression = "CONCATENATEX(SUMMARIZE(Staff, Staff[Practice]), [Practice], \", \")" });

        Assert.Equal(CheckStatus.Pass, PbiTwin.Core.Verify.DaxReferenceCheck.Run(db.Model).Status);
    }
}

public sealed class RenamedTwinTests : IDisposable
{
    private static readonly string SampleFolder = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
    private readonly string _out = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private static readonly string[] OriginalNames =
    [
        "FactSales", "DimCustomer", "DimProduct", "DimEmployee", "DimStore", "SalesAmount", "CostAmount", "CustomerKey",
        "Revenue YoY", "Top Segment Revenue", "Region Manager", "ManagerPath", "Line Margin", "Time Intelligence",
    ];

    [Fact]
    public void Semantic_rename_produces_a_working_twin_without_original_names()
    {
        var session = new TwinSession(PbipProject.Open(SampleFolder), new CsvTableReader(Path.Combine(SampleFolder, "data"), c => c.SourceColumn));
        var result = session.Run(session.Scan(), new TwinOptions { Seed = "RENAME-1", RenameMode = RenameMode.Semantic }, Path.Combine(_out, "Twin"));

        Assert.All(result.Report.Checks, c => Assert.Equal(CheckStatus.Pass, c.Status));
        Assert.Equal("Twin", result.Folder.Name);

        // Codebook exists next to the twin, not inside it.
        Assert.NotNull(result.CodebookFile);
        Assert.True(File.Exists(result.CodebookFile));
        Assert.False(result.CodebookFile!.StartsWith(result.Folder.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        // No original object name anywhere in the twin, including the trust report.
        var all = string.Join('\n', Directory.EnumerateFiles(result.Folder.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".csv", StringComparison.Ordinal) || f.Contains("data", StringComparison.Ordinal))
            .Select(f => f.EndsWith(".csv", StringComparison.Ordinal) ? File.ReadLines(f).First() : File.ReadAllText(f)));
        foreach (var name in OriginalNames)
        {
            Assert.DoesNotContain(name, all, StringComparison.OrdinalIgnoreCase);
        }

        // The twin model loads and keeps its structure.
        var model = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(result.Folder.SemanticModelFolder, "definition")).Model;
        Assert.Contains(model.Tables, t => t.Name == "FactA");
        Assert.Equal(6, model.Relationships.Count);
        Assert.Equal("Role01", Assert.Single(model.Roles).Name);
        var codebook = NameMap.FromJson(File.ReadAllText(result.CodebookFile));
        var revenue = codebook.Measure("Revenue");
        var fact = codebook.Table("FactSales");
        Assert.Equal($"SUM({fact}[{codebook.Column("FactSales", "SalesAmount")}])", model.Tables[fact].Measures[revenue].Expression);

        // A measure written against the twin translates back to the original model.
        var aiMeasure = $"CALCULATE([{revenue}], {codebook.Table("DimStore")}[{codebook.Column("DimStore", "Region")}] = \"Region 01\")";
        Assert.Equal("CALCULATE([Revenue], DimStore[Region] = \"Region 01\")", DaxTranslator.ToReal(aiMeasure, codebook));

        // Report references follow the new names.
        var visual = JsonNode.Parse(File.ReadAllText(Directory.EnumerateFiles(result.Folder.ReportFolder, "visual.json", SearchOption.AllDirectories)
            .Single(f => f.Contains("category_bar", StringComparison.Ordinal))))!;
        var category = visual["visual"]!["query"]!["queryState"]!["Category"]!["projections"]![0]!;
        Assert.Equal(codebook.Table("DimProduct"), category["field"]!["Column"]!["Expression"]!["SourceRef"]!["Entity"]!.GetValue<string>());
        Assert.Equal(codebook.Column("DimProduct", "Category"), category["field"]!["Column"]!["Property"]!.GetValue<string>());
        Assert.Equal($"{codebook.Table("DimProduct")}.{codebook.Column("DimProduct", "Category")}", category["queryRef"]!.GetValue<string>());
        var filter = visual["filterConfig"]!["filters"]![0]!["filter"]!;
        Assert.Equal(codebook.Table("DimCustomer"), filter["From"]![0]!["Entity"]!.GetValue<string>());
        Assert.Equal(codebook.Column("DimCustomer", "Country"), filter["Where"]![0]!["Condition"]!["In"]!["Expressions"]![0]!["Column"]!["Property"]!.GetValue<string>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }
}
