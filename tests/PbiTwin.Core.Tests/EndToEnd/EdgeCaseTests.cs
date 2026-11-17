using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Text;
using PbiTwin.Core.Verify;

namespace PbiTwin.Core.Tests.EndToEnd;

/// <summary>Data that real models contain and samples do not: no rows, only blanks, quotes and line breaks, one row.</summary>
public sealed class EdgeCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    /// <summary>One table T(Id key, Name, Note, Amount, Day) with the given CSV body (header is added).</summary>
    private TwinResult Run(IEnumerable<string> records)
    {
        var project = Path.Combine(_root, "source");
        var data = Path.Combine(project, "data");
        Directory.CreateDirectory(data);

        var db = new Database { Name = "M", ID = "M", CompatibilityLevel = 1567, Model = new Model() };
        var t = new Table { Name = "T" };
        t.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64, SourceColumn = "Id", IsKey = true });
        t.Columns.Add(new DataColumn { Name = "Name", DataType = DataType.String, SourceColumn = "Name" });
        t.Columns.Add(new DataColumn { Name = "Note", DataType = DataType.String, SourceColumn = "Note" });
        t.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal, SourceColumn = "Amount" });
        t.Columns.Add(new DataColumn { Name = "Day", DataType = DataType.DateTime, SourceColumn = "Day" });
        t.Partitions.Add(new Partition { Name = "T", Source = new MPartitionSource { Expression = "let Source = null in Source" } });
        db.Model.Tables.Add(t);

        var modelFolder = Path.Combine(project, "M.SemanticModel");
        TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelFolder, "definition"));
        File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"), """{ "version": "4.0" }""");
        Directory.CreateDirectory(Path.Combine(project, "M.Report", "definition"));
        File.WriteAllText(Path.Combine(project, "M.Report", "definition", "report.json"), "{}");
        File.WriteAllText(Path.Combine(project, "M.Report", "definition.pbir"), """{ "version": "4.0", "datasetReference": { "byPath": { "path": "../M.SemanticModel" } } }""");
        File.WriteAllText(Path.Combine(project, "M.pbip"), """{ "version": "1.0", "artifacts": [ { "report": { "path": "M.Report" } } ] }""");
        File.WriteAllText(Path.Combine(data, "T.csv"), "Id,Name,Note,Amount,Day\n" + string.Join('\n', records) + (records.Any() ? "\n" : string.Empty));

        var session = new TwinSession(PbipProject.Open(project), new CsvTableReader(data, c => c.SourceColumn));
        return session.Run(session.Scan(), new TwinOptions { Seed = "EDGE" }, Path.Combine(_root, "out", "Twin"));
    }

    private static List<string?[]> Records(string csvFile)
    {
        using var reader = new StreamReader(csvFile);
        return Csv.ReadRecords(reader).Skip(1).ToList();
    }

    [Fact]
    public void The_twin_is_opened_again_from_disk_and_a_damaged_one_fails()
    {
        var result = Run(["1,A,n,10,2024-01-01"]);
        Assert.Contains(result.Report.Checks, c => c.Name == "Twin opens" && c.Status == CheckStatus.Pass);

        File.Delete(Path.Combine(result.Folder.ReportFolder, "definition.pbir"));
        Assert.Equal(CheckStatus.Fail, ReopenCheck.Run(result.Folder, 1, 0).Status);

        Directory.Delete(Path.Combine(result.Folder.SemanticModelFolder, "definition"), recursive: true);
        Assert.Equal(CheckStatus.Fail, ReopenCheck.Run(result.Folder, 1, 0).Status);
    }

    [Fact]
    public void Placeholder_dates_survive_a_whole_run_and_stay_put()
    {
        var result = Run(["1,A,n,10,2024-03-01", "2,B,n,20,9999-12-31", "3,C,n,30,0001-01-01", "4,D,n,40,2025-06-01", "5,E,n,50,2025-07-01", "6,F,n,60,2025-08-01"]);

        Assert.DoesNotContain(result.Report.Checks, c => c.Status == CheckStatus.Fail);
        var days = Records(Path.Combine(result.Folder.DataFolder, "T.csv")).Select(r => r[4]!).ToList();
        Assert.Contains("9999-12-31T00:00:00", days);
        Assert.Contains("0001-01-01T00:00:00", days);
        Assert.DoesNotContain("2024-03-01T00:00:00", days);
    }

    [Fact]
    public void A_table_without_rows_is_created_empty()
    {
        var result = Run([]);

        Assert.Empty(Records(Path.Combine(result.Folder.DataFolder, "T.csv")));
        Assert.DoesNotContain(result.Report.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void A_single_row_survives()
    {
        var result = Run(["1,Alice Johnson,hello there,10.50,2024-03-01"]);

        var row = Assert.Single(Records(Path.Combine(result.Folder.DataFolder, "T.csv")));
        Assert.NotEqual("Alice Johnson", row[1]);
        Assert.DoesNotContain(result.Report.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Columns_with_only_blanks_stay_blank()
    {
        var result = Run(Enumerable.Range(1, 30).Select(i => $"{i},Person Number{i},,,"));

        var rows = Records(Path.Combine(result.Folder.DataFolder, "T.csv"));
        Assert.Equal(30, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(string.IsNullOrEmpty(r[2]));
            Assert.True(string.IsNullOrEmpty(r[3]));
            Assert.True(string.IsNullOrEmpty(r[4]));
        });
        Assert.DoesNotContain(result.Report.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Quotes_commas_and_line_breaks_inside_values_keep_the_record_structure()
    {
        var rows = new List<string>();
        for (var i = 1; i <= 40; i++)
        {
            rows.Add($"{i},\"Smith, John \"\"Jack\"\" {i}\",\"line one\nline two {i}\",{i}.25,2024-0{1 + i % 9}-1{i % 9}");
        }

        var result = Run(rows);

        var twin = Records(Path.Combine(result.Folder.DataFolder, "T.csv"));
        Assert.Equal(40, twin.Count);
        Assert.All(twin, r => Assert.Equal(5, r.Length));
        Assert.DoesNotContain(result.Report.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Missing_dates_and_amounts_in_some_rows_stay_missing()
    {
        var result = Run(Enumerable.Range(1, 50).Select(i => $"{i},Person Number{i},some note {i},{(i % 5 == 0 ? string.Empty : "12.5")},{(i % 7 == 0 ? string.Empty : "2024-05-05")}"));

        var rows = Records(Path.Combine(result.Folder.DataFolder, "T.csv"));
        Assert.Equal(10, rows.Count(r => string.IsNullOrEmpty(r[3])));
        Assert.Equal(7, rows.Count(r => string.IsNullOrEmpty(r[4])));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class SentinelDateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1092)]
    [InlineData(-1092)]
    public void Placeholder_dates_at_the_ends_of_the_calendar_neither_crash_nor_move(int shift)
    {
        var anonymizer = new Anonymizer(
            AnonymizationPlanFor(),
            new TwinOptions { Seed = "SENTINEL-DATES-1", DateShiftDays = shift });
        var column = anonymizer.Plan.Columns.Single();
        var noise = anonymizer.NoiseFor("T");

        Assert.Equal(new DateTime(9999, 12, 31), anonymizer.Transform(column, new DateTime(9999, 12, 31), noise));
        Assert.Equal(new DateTime(1900, 1, 1), anonymizer.Transform(column, new DateTime(1900, 1, 1), noise));
        Assert.Equal(DateTime.MinValue, anonymizer.Transform(column, DateTime.MinValue, noise));
        Assert.Equal(DateTime.MaxValue, anonymizer.Transform(column, DateTime.MaxValue, noise));
        Assert.Equal(new DateTime(2024, 3, 1).AddDays(shift), anonymizer.Transform(column, new DateTime(2024, 3, 1), noise));
    }

    private static AnonymizationPlan AnonymizationPlanFor()
    {
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        var t = new Table { Name = "T" };
        t.Columns.Add(new DataColumn { Name = "EndDate", DataType = DataType.DateTime, SourceColumn = "EndDate" });
        t.Partitions.Add(new Partition { Name = "T", Source = new MPartitionSource { Expression = "x" } });
        db.Model.Tables.Add(t);
        return AnonymizationPlan.Create(ModelInfo.FromModel(db.Model), new Dictionary<string, PbiTwin.Core.Classify.ColumnProfile>(), _ => new HashSet<object>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
