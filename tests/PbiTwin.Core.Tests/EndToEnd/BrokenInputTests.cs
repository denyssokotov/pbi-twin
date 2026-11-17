using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Tests.Guards;

namespace PbiTwin.Core.Tests.EndToEnd;

/// <summary>Broken or unusual input must end in an exception with a message a person can act on, never a crash with no hint.</summary>
public sealed class BrokenInputTests : IDisposable
{
    private static readonly string Sample = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private string CopyProject()
    {
        var project = Path.Combine(_root, "p");
        foreach (var file in Directory.EnumerateFiles(Sample, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(Sample, f).StartsWith("data", StringComparison.Ordinal)))
        {
            var target = Path.Combine(project, Path.GetRelativePath(Sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return project;
    }

    [Fact]
    public void A_run_that_fails_halfway_leaves_no_half_written_twin_behind()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Sample, "data"), "*.csv"))
        {
            File.Copy(f, Path.Combine(data, Path.GetFileName(f)));
        }

        var session = new TwinSession(PbipProject.Open(Sample), new CsvTableReader(data, c => c.SourceColumn));
        var plan = session.Scan();

        // The data changes after the scan: the fact table now has a bad cell, found only while streaming it.
        var fact = Path.Combine(data, "FactSales.csv");
        var lines = File.ReadAllLines(fact);
        var amount = lines[0].Split(',').ToList().IndexOf("SalesAmount");
        var cells = lines[^2].Split(',');
        cells[amount] = "not-a-number";
        lines[^2] = string.Join(',', cells);
        File.WriteAllLines(fact, lines);

        var output = Path.Combine(_root, "out", "Twin");
        Assert.Throws<InvalidDataException>(() => session.Run(plan, new TwinOptions { Seed = "HALFWAY-FAIL-123", RenameMode = PbiTwin.Core.Rename.RenameMode.Neutral }, output));

        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(_root, "out", "Twin.codebook.json")));
    }

    [Fact]
    public void A_missing_pbip_is_a_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => PbipProject.Open(Path.Combine(_root, "nothing.pbip")));
    }

    [Fact]
    public void A_folder_with_two_pbip_files_is_explained()
    {
        var folder = Path.Combine(_root, "two");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.pbip"), "{}");
        File.WriteAllText(Path.Combine(folder, "b.pbip"), "{}");

        var ex = Assert.Throws<InvalidOperationException>(() => PbipProject.Open(folder));
        Assert.Contains("exactly one .pbip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pbip_without_a_report_is_explained()
    {
        var project = CopyProject();
        File.WriteAllText(Path.Combine(project, "ContosoPII.pbip"), """{ "version": "1.0", "artifacts": [] }""");

        var ex = Assert.Throws<InvalidOperationException>(() => PbipProject.Open(project));
        Assert.Contains("does not reference a report", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_with_a_live_connection_is_explained()
    {
        var project = CopyProject();
        File.WriteAllText(Path.Combine(project, "ContosoPII.Report", "definition.pbir"),
            """{ "version": "4.0", "datasetReference": { "byConnection": { "connectionString": "x" } } }""");

        var ex = Assert.Throws<InvalidOperationException>(() => PbipProject.Open(project));
        Assert.Contains("not bound to a local semantic model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_pbip_file_throws_a_json_exception_that_names_the_problem()
    {
        var project = CopyProject();
        File.WriteAllText(Path.Combine(project, "ContosoPII.pbip"), "{ not json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PbipProject.Open(project));
    }

    [Fact]
    public void A_csv_without_the_expected_column_is_explained()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        foreach (var f in Directory.GetFiles(Path.Combine(Sample, "data")))
        {
            File.Copy(f, Path.Combine(data, Path.GetFileName(f)));
        }

        File.WriteAllText(Path.Combine(data, "DimStore.csv"), "Wrong,Header\n1,2\n");
        var session = new TwinSession(PbipProject.Open(Sample), new CsvTableReader(data, c => c.SourceColumn));

        var ex = Assert.ThrowsAny<Exception>(() => session.Scan());
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void A_missing_csv_file_is_a_file_error_not_a_crash()
    {
        var data = Path.Combine(_root, "empty");
        Directory.CreateDirectory(data);
        var session = new TwinSession(PbipProject.Open(Sample), new CsvTableReader(data, c => c.SourceColumn));

        Assert.ThrowsAny<IOException>(() => session.Scan());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
