namespace PbiTwin.Core.Tests.Guards;

/// <summary>Every place that opens a connection to Analysis Services must call the localhost guard first.</summary>
public class ConnectionSitesTests
{
    [Fact]
    public void Every_connection_is_preceded_by_the_localhost_guard()
    {
        var sites = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (File: f, Lines: File.ReadAllLines(f)))
            .SelectMany(x => x.Lines.Select((line, i) => (x.File, Line: i, Text: line, x.Lines)))
            .Where(x => x.Text.Contains("new AdomdConnection(", StringComparison.Ordinal) || x.Text.Contains(".Connect(", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sites);
        foreach (var site in sites)
        {
            var before = string.Join('\n', site.Lines.Take(site.Line));
            Assert.True(before.Contains("EnsureLocal(", StringComparison.Ordinal),
                $"{Path.GetFileName(site.File)}:{site.Line + 1} opens a connection without LocalhostGuard.EnsureLocal before it.");
        }
    }
}
