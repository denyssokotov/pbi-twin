using System.Text.RegularExpressions;

namespace PbiTwin.Core.Tests.Guards;

/// <summary>Supply chain: every GitHub Action is pinned to a full commit SHA, not to a tag that can be moved.</summary>
public sealed partial class WorkflowPinningTests
{
    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<ref>\S+)", RegexOptions.Multiline)]
    private static partial Regex Uses();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex Pinned();

    [Fact]
    public void Every_action_is_pinned_to_a_commit()
    {
        var workflows = Directory.GetFiles(Path.Combine(RepoPaths.Root, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);

        var unpinned = workflows
            .SelectMany(f => Uses().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Ref: m.Groups["ref"].Value)))
            .Where(u => !Pinned().IsMatch(u.Ref))
            .Select(u => $"{u.File}: {u.Ref}")
            .ToList();

        Assert.Empty(unpinned);
    }

    [Fact]
    public void Read_only_workflows_do_not_keep_credentials_and_have_minimal_permissions()
    {
        foreach (var name in new[] { "build.yml", "no-network-check.yml" })
        {
            var text = File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", name));
            Assert.Contains("contents: read", text, StringComparison.Ordinal);
            Assert.Contains("persist-credentials: false", text, StringComparison.Ordinal);
            Assert.DoesNotContain("contents: write", text, StringComparison.Ordinal);
        }
    }
}
