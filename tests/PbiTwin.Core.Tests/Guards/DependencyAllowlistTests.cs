using System.Xml.Linq;

namespace PbiTwin.Core.Tests.Guards;

/// <summary>Fails if a package outside the approved list is added anywhere in the repository.</summary>
public class DependencyAllowlistTests
{
    private static readonly string[] RuntimeAllowed =
    [
        "Microsoft.AnalysisServices",
        "Microsoft.AnalysisServices.AdomdClient",
    ];

    private static readonly string[] TestOnlyAllowed =
    [
        "xunit",
        "xunit.runner.visualstudio",
        "Microsoft.NET.Test.Sdk",
    ];

    [Fact]
    public void Central_package_list_contains_only_approved_packages()
    {
        var props = XDocument.Load(Path.Combine(RepoPaths.Root, "Directory.Packages.props"));
        var packages = props.Descendants("PackageVersion").Select(e => (string?)e.Attribute("Include")).ToList();

        var unexpected = packages.Except(RuntimeAllowed.Concat(TestOnlyAllowed), StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(unexpected.Count == 0, "Unapproved packages: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Versions_are_pinned_exactly()
    {
        var props = XDocument.Load(Path.Combine(RepoPaths.Root, "Directory.Packages.props"));
        foreach (var e in props.Descendants("PackageVersion"))
        {
            var version = (string?)e.Attribute("Version") ?? string.Empty;
            Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        }
    }

    [Fact]
    public void Shipped_projects_reference_only_runtime_packages()
    {
        foreach (var csproj in RepoPaths.SourceFiles("src", ".csproj"))
        {
            var refs = XDocument.Load(csproj).Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
                .ToList();

            var unexpected = refs.Except(RuntimeAllowed, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(unexpected.Count == 0, $"{Path.GetFileName(csproj)} references unapproved packages: {string.Join(", ", unexpected)}");
        }
    }

    [Fact]
    public void Every_project_has_a_committed_lock_file()
    {
        var projects = RepoPaths.SourceFiles("src", ".csproj").Concat(RepoPaths.SourceFiles("tests", ".csproj"));
        foreach (var csproj in projects)
        {
            var lockFile = Path.Combine(Path.GetDirectoryName(csproj)!, "packages.lock.json");
            Assert.True(File.Exists(lockFile), $"Missing packages.lock.json next to {Path.GetFileName(csproj)}");
        }
    }
}
