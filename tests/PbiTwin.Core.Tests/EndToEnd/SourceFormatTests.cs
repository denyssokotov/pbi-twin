using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Classify;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Tests.Guards;
using PbiTwin.Core.Verify;
using ColumnType = PbiTwin.Core.Metadata.ColumnType;
using TomJson = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace PbiTwin.Core.Tests.EndToEnd;

/// <summary>Real-world variations the sample does not cover: model.bim projects, SQL/DirectQuery sources, incremental refresh.</summary>
public sealed class SourceFormatTests : IDisposable
{
    private static readonly string Sample = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
    private static readonly string Data = Path.Combine(Sample, "data");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Copies the sample project (without data) and lets the test change its model.</summary>
    private string CopySample(Action<Model> change, bool asBim)
    {
        var project = Path.Combine(_root, "source");
        foreach (var file in Directory.EnumerateFiles(Sample, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Sample, file);
            if (rel.StartsWith("data", StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(project, rel))!);
            File.Copy(file, Path.Combine(project, rel));
        }

        var modelFolder = Path.Combine(project, "ContosoPII.SemanticModel");
        var db = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelFolder, "definition"));
        db.Name = "ContosoPII";
        db.ID = "ContosoPII";
        change(db.Model);

        Directory.Delete(Path.Combine(modelFolder, "definition"), recursive: true);
        if (asBim)
        {
            File.WriteAllText(Path.Combine(modelFolder, "model.bim"), TomJson.SerializeDatabase(db));
            File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"),
                """{ "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json", "version": "1.0" }""");
        }
        else
        {
            TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelFolder, "definition"));
        }

        return project;
    }

    private TwinResult Run(string project, TwinOptions? options = null)
    {
        var session = new TwinSession(PbipProject.Open(project), new CsvTableReader(Data, c => c.SourceColumn));
        return session.Run(session.Scan(), options ?? new TwinOptions { Seed = "FORMATS-1" }, Path.Combine(_root, "out", "Twin"));
    }

    [Fact]
    public void Model_bim_projects_are_supported_and_stay_model_bim()
    {
        var result = Run(CopySample(_ => { }, asBim: true));

        Assert.True(result.Report.Passed, result.Report.ToMarkdown());
        var bim = Path.Combine(result.Folder.SemanticModelFolder, "model.bim");
        Assert.True(File.Exists(bim));
        Assert.False(Directory.Exists(Path.Combine(result.Folder.SemanticModelFolder, "definition")));
        Assert.Equal(["DataFolder"], TomJson.DeserializeDatabase(File.ReadAllText(bim)).Model.Expressions.Select(e => e.Name));
    }

    [Fact]
    public void Sql_directquery_and_incremental_refresh_sources_are_stripped()
    {
        var project = CopySample(model =>
        {
            model.DataSources.Add(new ProviderDataSource
            {
                Name = "SqlProd",
                ConnectionString = "Provider=MSOLEDBSQL;Data Source=sql-prod-07.contoso-pii.local;Initial Catalog=SalesDW;User ID=etl;Password=Sup3rS3cret!",
            });

            var store = model.Tables["DimStore"];
            store.Partitions.Clear();
            store.Partitions.Add(new Partition
            {
                Name = "DimStore",
                Mode = ModeType.DirectQuery,
                Source = new QueryPartitionSource { DataSource = model.DataSources["SqlProd"], Query = "SELECT * FROM dbo.Stores WHERE Secret = 'x'" },
            });

            var fact = model.Tables["FactSales"];
            fact.RefreshPolicy = new BasicRefreshPolicy
            {
                IncrementalGranularity = RefreshGranularityType.Day,
                IncrementalPeriods = 10,
                RollingWindowGranularity = RefreshGranularityType.Year,
                RollingWindowPeriods = 3,
                SourceExpression = "let Source = Sql.Database(\"sql-prod-07.contoso-pii.local\", \"SalesDW\") in Source",
            };
        }, asBim: false);

        var result = Run(project);
        Assert.True(result.Report.Passed, result.Report.ToMarkdown());

        var tmdl = string.Join('\n', Directory.EnumerateFiles(result.Folder.SemanticModelFolder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var secret in new[] { "sql-prod-07", "SalesDW", "Sup3rS3cret", "dbo.Stores", "SqlProd" })
        {
            Assert.DoesNotContain(secret, tmdl, StringComparison.Ordinal);
        }

        var model = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(result.Folder.SemanticModelFolder, "definition")).Model;
        Assert.Empty(model.DataSources);
        Assert.Null(model.Tables["FactSales"].RefreshPolicy);
        var partition = Assert.Single(model.Tables["DimStore"].Partitions);
        Assert.Equal(ModeType.Import, partition.Mode);
        Assert.IsType<MPartitionSource>(partition.Source);
    }

    [Fact]
    public void Renamed_twin_values_do_not_reveal_column_names()
    {
        var result = Run(CopySample(_ => { }, asBim: false), new TwinOptions { Seed = "FORMATS-2", RenameMode = RenameMode.Neutral });
        var codebook = NameMap.FromJson(File.ReadAllText(result.CodebookFile!));

        var customers = File.ReadAllLines(Path.Combine(result.Folder.DataFolder, codebook.Table("DimCustomer") + ".csv"));
        var header = customers[0].Split(',');
        var segment = Array.IndexOf(header, codebook.Column("DimCustomer", "Segment"));
        Assert.All(customers.Skip(1), line => Assert.StartsWith(codebook.Column("DimCustomer", "Segment") + " ", line.Split(',')[segment], StringComparison.Ordinal));

        var sales = File.ReadLines(Path.Combine(result.Folder.DataFolder, codebook.Table("FactSales") + ".csv")).Skip(1).First();
        Assert.StartsWith("ID-", sales, StringComparison.Ordinal);

        var all = string.Join('\n', Directory.EnumerateFiles(result.Folder.DataFolder).Select(File.ReadAllText));
        Assert.DoesNotContain("Segment", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Store ", all, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public class SelfReferenceTests
{
    [Fact]
    public void Self_reference_beyond_the_sampled_examples_is_linked_using_full_data()
    {
        var key = new ColumnInfo("Emp", "EmployeeKey", ColumnType.Int64, "EmployeeKey", true, false, null, false);
        var manager = new ColumnInfo("Emp", "ManagerKey", ColumnType.Int64, "ManagerKey", false, false, null, false);
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        var t = new Table { Name = "Emp" };
        t.Columns.Add(new DataColumn { Name = "EmployeeKey", DataType = DataType.Int64, SourceColumn = "EmployeeKey", IsKey = true });
        t.Columns.Add(new DataColumn { Name = "ManagerKey", DataType = DataType.Int64, SourceColumn = "ManagerKey" });
        t.Partitions.Add(new Partition { Name = "Emp", Source = new MPartitionSource { Expression = "x" } });
        db.Model.Tables.Add(t);
        var model = ModelInfo.FromModel(db.Model);

        // 1000 employees; managers are 900..950, beyond the first 200 sampled examples.
        var keys = new ColumnProfile();
        var managers = new ColumnProfile();
        for (long i = 1; i <= 1000; i++)
        {
            keys.Add(i);
            managers.Add(i <= 950 ? 900 + (i % 51) : null);
        }

        var profiles = new Dictionary<string, ColumnProfile> { [key.Id] = keys, [manager.Id] = managers };

        var sampledOnly = AnonymizationPlan.Create(model, profiles);
        Assert.NotSame(sampledOnly[key].Domain, sampledOnly[manager].Domain);

        var full = new Dictionary<string, IReadOnlySet<object>>
        {
            [key.Id] = Enumerable.Range(1, 1000).Select(i => (object)(long)i).ToHashSet(),
            [manager.Id] = Enumerable.Range(900, 51).Select(i => (object)(long)i).ToHashSet(),
        };
        var withData = AnonymizationPlan.Create(model, profiles, c => full[c.Id]);
        Assert.Same(withData[key].Domain, withData[manager].Domain);
    }
}
