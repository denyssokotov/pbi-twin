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
