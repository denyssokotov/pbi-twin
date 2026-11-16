using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Verify;
using TomColumnType = Microsoft.AnalysisServices.Tabular.ColumnType;

namespace PbiTwin.Core.Tests.EndToEnd;

/// <summary>A model with Cyrillic table, column and measure names and Cyrillic values, end to end.</summary>
public sealed class UnicodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private static readonly string[] FirstNames = ["Олена", "Андрій", "Марія", "Тарас", "Ірина", "Богдан", "Соломія", "Остап"];
    private static readonly string[] LastNames = ["Коваленко", "Шевченко", "Мельник", "Бондаренко", "Ткаченко", "Кравчук"];
    private static readonly string[] Cities = ["Київ", "Львів", "Дніпро", "Одеса", "Харків"];

    private string BuildProject()
    {
        var project = Path.Combine(_root, "source");
        var data = Path.Combine(project, "data");
        Directory.CreateDirectory(data);

        var db = new Database { Name = "Звіт", ID = "Звіт", CompatibilityLevel = 1567, Model = new Model() };
        var clients = new Table { Name = "Клієнти" };
        clients.Columns.Add(new DataColumn { Name = "КлієнтID", DataType = DataType.Int64, SourceColumn = "КлієнтID", IsKey = true });
        clients.Columns.Add(new DataColumn { Name = "ПІБ", DataType = DataType.String, SourceColumn = "ПІБ" });
        clients.Columns.Add(new DataColumn { Name = "Місто", DataType = DataType.String, SourceColumn = "Місто" });
        clients.Partitions.Add(new Partition { Name = "Клієнти", Source = new MPartitionSource { Expression = "let Source = null in Source" } });
        var sales = new Table { Name = "Продажі" };
        sales.Columns.Add(new DataColumn { Name = "КлієнтID", DataType = DataType.Int64, SourceColumn = "КлієнтID" });
        sales.Columns.Add(new DataColumn { Name = "Дата", DataType = DataType.DateTime, SourceColumn = "Дата" });
        sales.Columns.Add(new DataColumn { Name = "Сума", DataType = DataType.Decimal, SourceColumn = "Сума" });
        sales.Partitions.Add(new Partition { Name = "Продажі", Source = new MPartitionSource { Expression = "let Source = null in Source" } });
        sales.Measures.Add(new Measure { Name = "Загальна сума", Expression = "SUM('Продажі'[Сума])" });
        sales.Measures.Add(new Measure { Name = "Сума міста", Expression = "CALCULATE([Загальна сума], 'Клієнти'[Місто] = \"Київ\")" });
        db.Model.Tables.Add(clients);
        db.Model.Tables.Add(sales);
        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            FromColumn = sales.Columns["КлієнтID"],
            ToColumn = clients.Columns["КлієнтID"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One,
        });

        var modelFolder = Path.Combine(project, "Звіт.SemanticModel");
        TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelFolder, "definition"));
        File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"), """{ "version": "4.0" }""");

        var report = Path.Combine(project, "Звіт.Report");
        Directory.CreateDirectory(Path.Combine(report, "definition"));
        File.WriteAllText(Path.Combine(report, "definition", "report.json"), "{}");
        File.WriteAllText(Path.Combine(report, "definition.pbir"), """{ "version": "4.0", "datasetReference": { "byPath": { "path": "../Звіт.SemanticModel" } } }""");
        File.WriteAllText(Path.Combine(project, "Звіт.pbip"), """{ "version": "1.0", "artifacts": [ { "report": { "path": "Звіт.Report" } } ] }""");

        var rnd = new Random(7);
        var lines = new List<string> { "КлієнтID,ПІБ,Місто" };
        for (var i = 1; i <= 200; i++)
        {
            lines.Add($"{i},{FirstNames[i % FirstNames.Length]} {LastNames[(i / 3) % LastNames.Length]}{i},{Cities[i % Cities.Length]}");
        }

        File.WriteAllLines(Path.Combine(data, "Клієнти.csv"), lines);
        var sale = new List<string> { "КлієнтID,Дата,Сума" };
        for (var i = 0; i < 2000; i++)
        {
            sale.Add($"{rnd.Next(1, 201)},{new DateTime(2024, 1, 1).AddDays(rnd.Next(0, 360)):yyyy-MM-dd},{(100 + rnd.Next(0, 90000)) / 100.0:0.00}");
        }

        File.WriteAllLines(Path.Combine(data, "Продажі.csv"), sale);
        return project;
    }

    private TwinResult Run(RenameMode mode)
    {
        var project = BuildProject();
        var session = new TwinSession(PbipProject.Open(project), new CsvTableReader(Path.Combine(project, "data"), c => c.SourceColumn));
        return session.Run(session.Scan(), new TwinOptions { Seed = "UA-1", RenameMode = mode }, Path.Combine(_root, "out", "Twin"));
    }

    [Theory]
    [InlineData(RenameMode.None)]
    [InlineData(RenameMode.Neutral)]
    public void Cyrillic_model_is_anonymized_and_verified(RenameMode mode)
    {
        var result = Run(mode);

        Assert.True(result.Report.Passed, result.Report.ToMarkdown());
        var everything = string.Join('\n', Directory.EnumerateFiles(result.Folder.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("TRUST-REPORT.md", StringComparison.Ordinal)).Select(File.ReadAllText));
        foreach (var original in LastNames.Concat(Cities))
        {
            Assert.DoesNotContain(original, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cyrillic_literals_in_dax_follow_their_values()
    {
        var result = Run(RenameMode.None);
        var model = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(result.Folder.SemanticModelFolder, "definition")).Model;
        var measure = model.Tables["Продажі"].Measures["Сума міста"].Expression;

        Assert.DoesNotContain("Київ", measure, StringComparison.Ordinal);
        var pseudonym = measure.Split('"')[1];
        var cities = File.ReadLines(Path.Combine(result.Folder.DataFolder, "Клієнти.csv")).Skip(1).Select(l => l.Split(',')[2]).ToHashSet();
        Assert.Contains(pseudonym, cities);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
