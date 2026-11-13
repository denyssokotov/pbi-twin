using System.Security.Cryptography;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Tests.Guards;
using PbiTwin.Core.Verify;
using Xunit.Abstractions;

namespace PbiTwin.Core.Tests.EndToEnd;

/// <summary>
/// Runs the full pipeline on the ContosoPII sample, reading the sample CSVs in place of a live
/// Power BI Desktop. Requires the generated data: <c>dotnet run --project samples/generator</c>.
/// </summary>
public sealed class SampleTwinTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string SampleFolder = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
    private static readonly string DataFolder = Path.Combine(SampleFolder, "data");
    // Set PBITWIN_TEST_OUTPUT to keep the generated twins for inspection.
    private readonly string _out = Environment.GetEnvironmentVariable("PBITWIN_TEST_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private static TwinSession Session()
    {
        Assert.True(File.Exists(Path.Combine(DataFolder, "FactSales.csv")),
            "Sample data missing. Run: dotnet run --project samples/generator");
        return new TwinSession(PbipProject.Open(SampleFolder), new CsvTableReader(DataFolder, c => c.SourceColumn));
    }

    private static TwinOptions Options => new() { Seed = "TEST-SEED-0001" };

    [Fact]
    public void Twin_of_sample_passes_all_checks_with_zero_leaks()
    {
        var session = Session();
        var plan = session.Scan();
        var result = session.Run(plan, Options, Path.Combine(_out, "a"));

        output.WriteLine(result.Report.ToMarkdown());

        Assert.Empty(result.Report.Leaks);
        Assert.All(result.Report.Checks, c => Assert.Equal(CheckStatus.Pass, c.Status));
        Assert.True(File.Exists(result.Folder.TrustReportFile));
    }

    [Fact]
    public void Plan_detects_the_sensitive_columns()
    {
        var plan = Session().Scan();

        Assert.Equal(Rule.PersonName, plan["DimCustomer[FullName]"].Rule);
        Assert.Equal(Rule.Email, plan["DimCustomer[Email]"].Rule);
        Assert.Equal(Rule.Phone, plan["DimCustomer[Phone]"].Rule);
        Assert.Equal(Rule.Iban, plan["DimCustomer[IBAN]"].Rule);
        Assert.Equal(Rule.Address, plan["DimCustomer[Address]"].Rule);
        Assert.Equal(Rule.City, plan["DimCustomer[City]"].Rule);
        Assert.Equal(Rule.Country, plan["DimCustomer[Country]"].Rule);
        Assert.Equal(Rule.Label, plan["DimCustomer[Segment]"].Rule);
        Assert.Equal(Rule.Label, plan["DimProduct[ProductName]"].Rule);
        Assert.Equal(Rule.Money, plan["FactSales[SalesAmount]"].Rule);
        Assert.Equal(Rule.Money, plan["DimEmployee[Salary]"].Rule);
        Assert.Equal(Rule.Quantity, plan["FactSales[Quantity]"].Rule);
        Assert.Equal(Rule.Keep, plan["FactSales[Discount]"].Rule);
        Assert.Equal(Rule.DateShift, plan["FactSales[OrderDate]"].Rule);
        Assert.Equal(Rule.FreeText, plan["FactSales[Comment]"].Rule);
        Assert.Equal(Rule.Key, plan["FactSales[OrderNumber]"].Rule);

        // Relationship domains and the self-reference link.
        Assert.Same(plan["FactSales[CustomerKey]"].Domain, plan["DimCustomer[CustomerKey]"].Domain);
        Assert.Same(plan["DimEmployee[ManagerKey]"].Domain, plan["DimEmployee[EmployeeKey]"].Domain);
        Assert.Same(plan["DimCustomer[City]"].Domain, plan["DimStore[City]"].Domain);
    }

    [Fact]
    public void Twin_model_has_no_original_sources_and_rewritten_literals()
    {
        var session = Session();
        var result = session.Run(session.Scan(), Options, Path.Combine(_out, "b"));
        var model = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(result.Folder.SemanticModelFolder, "definition")).Model;

        Assert.Equal(["DataFolder"], model.Expressions.Select(e => e.Name));
        var tmdl = string.Join('\n', Directory.EnumerateFiles(result.Folder.SemanticModelFolder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("sql-prod-07", tmdl, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyServer", tmdl, StringComparison.Ordinal);

        var rls = model.Roles["Region Manager"].TablePermissions["DimStore"].FilterExpression;
        Assert.DoesNotContain("\"West\"", rls, StringComparison.Ordinal);
        Assert.Matches("DimStore\\[Region\\] = \"Region \\d+\"", rls);

        var top = model.Tables["FactSales"].Measures["Top Segment Revenue"].Expression;
        Assert.Matches("DimCustomer\\[Segment\\] = \"Segment \\d+\"", top);
        Assert.DoesNotContain("Kyiv", model.Tables["FactSales"].Measures["Capital Stores Revenue"].Expression, StringComparison.Ordinal);

        var report = string.Join('\n', Directory.EnumerateFiles(result.Folder.ReportFolder, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("Ukraine", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Olena", report, StringComparison.Ordinal);

        // The literal in the RLS role and the value in the twin data are the same pseudonym.
        var west = rls.Split('"')[1];
        Assert.Contains("," + west + "\n", File.ReadAllText(Path.Combine(result.Folder.DataFolder, "DimStore.csv")), StringComparison.Ordinal);
    }

    [Fact]
    public void Same_seed_gives_identical_twin_data()
    {
        var session = Session();
        var plan = session.Scan();
        var a = session.Run(plan, Options, Path.Combine(_out, "c1"));
        var b = session.Run(plan, Options, Path.Combine(_out, "c2"));

        foreach (var file in Directory.GetFiles(a.Folder.DataFolder))
        {
            var other = Path.Combine(b.Folder.DataFolder, Path.GetFileName(file));
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(file)), SHA256.HashData(File.ReadAllBytes(other)));
        }
    }

    [Fact]
    public void Fact_row_limit_keeps_integrity()
    {
        var session = Session();
        var result = session.Run(session.Scan(), Options with { FactRowLimit = 5_000 }, Path.Combine(_out, "d"));

        Assert.Equal(5_000, result.Report.Tables.Single(t => t.Table == "FactSales").Twin);
        Assert.Equal(2_000, result.Report.Tables.Single(t => t.Table == "DimCustomer").Twin);
        Assert.True(result.Report.Passed, result.Report.ToMarkdown());
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("PBITWIN_TEST_OUTPUT") is null && Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }
}
