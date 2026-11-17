using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Tests.Guards;

namespace PbiTwin.Core.Tests.EndToEnd;

public sealed class SeedTests : IDisposable
{
    private static readonly string Sample = Path.Combine(RepoPaths.Root, "samples", "ContosoPII");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private TwinResult Run(string seed, string name, RenameMode mode = RenameMode.None)
    {
        var session = new TwinSession(PbipProject.Open(Sample), new CsvTableReader(Path.Combine(Sample, "data"), c => c.SourceColumn));
        return session.Run(session.Scan(), new TwinOptions { Seed = seed, RenameMode = mode }, Path.Combine(_root, name));
    }

    private static string AllText(string folder) => string.Join('\n', Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));

    [Fact]
    public void The_seed_is_written_nowhere()
    {
        const string seed = "QZXV-8JKL-7MNP-4RTW";
        var result = Run(seed, "seeded", RenameMode.Neutral);

        Assert.DoesNotContain(seed, AllText(result.Folder.Root), StringComparison.Ordinal);
        Assert.DoesNotContain(seed, File.ReadAllText(result.CodebookFile!), StringComparison.Ordinal);
        Assert.DoesNotContain(seed, AllText(Path.GetDirectoryName(result.Folder.Root)!).Replace(AllText(result.Folder.Root), string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RenameMode.None)]
    [InlineData(RenameMode.Semantic)]
    public void The_trust_report_holds_no_original_value(RenameMode mode)
    {
        var result = Run("REPORT-NO-VALUES-1", "report-" + mode, mode);
        var report = File.ReadAllText(result.Folder.TrustReportFile);

        // Every distinct text value of the original data (4+ characters) that is not also a table or column name.
        var names = new HashSet<string>(Model(Sample), StringComparer.OrdinalIgnoreCase);
        var values = Directory.EnumerateFiles(Path.Combine(Sample, "data"), "*.csv")
            .SelectMany(f => { using var r = new StreamReader(f); return PbiTwin.Core.Text.Csv.ReadRecords(r).Skip(1).SelectMany(x => x).ToList(); })
            .Where(v => v is { Length: >= 4 } && !double.TryParse(v, out _) && !DateTime.TryParse(v, out _))
            .Select(v => v!)
            .Distinct()
            .Where(v => !names.Contains(v))
            .ToList();

        Assert.True(values.Count > 1000);
        var found = new PbiTwin.Core.Text.TextMatcher(values).Find(report).Select(m => m.Pattern).Distinct().Count();
        Assert.True(found == 0, $"{found} original value(s) appear in the trust report");
    }

    private static IEnumerable<string> Model(string sample)
    {
        var model = new TwinSession(PbipProject.Open(sample), new CsvTableReader(Path.Combine(sample, "data"), c => c.SourceColumn)).Model;
        return model.Tables.SelectMany(t => t.Columns.Select(c => c.Name).Append(t.Name))
            .Concat(model.Tables.SelectMany(t => t.Columns.Select(c => t.Name + "[" + c.Name + "]")));
    }

    [Fact]
    public void The_same_seed_gives_the_same_twin_and_another_seed_a_different_one()
    {
        var a = Run("SEED-NUMBER-ONE-1", "a");
        var b = Run("SEED-NUMBER-ONE-1", "b");
        var c = Run("SEED-NUMBER-TWO-2", "c");

        var file = Path.Combine("data", "DimCustomer.csv");
        Assert.Equal(File.ReadAllText(Path.Combine(a.Folder.Root, file)), File.ReadAllText(Path.Combine(b.Folder.Root, file)));
        Assert.NotEqual(File.ReadAllText(Path.Combine(a.Folder.Root, file)), File.ReadAllText(Path.Combine(c.Folder.Root, file)));
    }

    [Fact]
    public void A_short_seed_is_reported_without_revealing_it()
    {
        var weak = Run("abc", "weak");
        var strong = Run("QZXV-8JKL-7MNP-4RTW", "strong");

        var warning = Assert.Single(weak.Report.Warnings, w => w.StartsWith("The seed is short", StringComparison.Ordinal));
        Assert.DoesNotContain("abc", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(strong.Report.Warnings, w => w.StartsWith("The seed is short", StringComparison.Ordinal));
    }

    [Fact]
    public void Columns_that_look_personal_but_are_kept_are_reported()
    {
        var session = new TwinSession(PbipProject.Open(Sample), new CsvTableReader(Path.Combine(Sample, "data"), c => c.SourceColumn));
        var plan = session.Scan();
        plan.SetRule("DimCustomer[Email]", Rule.Keep);
        plan.SetRule("DimCustomer[FullName]", Rule.Keep);

        var result = session.Run(plan, new TwinOptions { Seed = "QZXV-8JKL-7MNP-4RTW" }, Path.Combine(_root, "kept"));

        Assert.Contains(result.Report.Warnings, w => w.Contains("detected as Email were kept", StringComparison.Ordinal) && w.Contains("DimCustomer[Email]", StringComparison.Ordinal));
        Assert.Contains(result.Report.Warnings, w => w.Contains("detected as PersonName were kept", StringComparison.Ordinal));
        // The leak scan does not call them leaks: kept values are not "original text values that were anonymized".
        Assert.True(result.Report.Passed, result.Report.ToMarkdown());
    }

    [Fact]
    public void Generated_seeds_are_unique_and_well_formed()
    {
        var seeds = Enumerable.Range(0, 2000).Select(_ => SeededHash.NewSeed()).ToList();

        Assert.Equal(2000, seeds.Distinct().Count());
        Assert.All(seeds, s => Assert.Matches("^([A-Z2-9]{4}-){3}[A-Z2-9]{4}$", s));
        Assert.All(seeds, s => Assert.True(s.Length >= SeededHash.RecommendedSeedLength));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
