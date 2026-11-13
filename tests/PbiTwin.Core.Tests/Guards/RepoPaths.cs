namespace PbiTwin.Core.Tests.Guards;

internal static class RepoPaths
{
    /// <summary>Walks up from the test binaries to the folder that contains PbiTwin.sln.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PbiTwin.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root (PbiTwin.sln).");
        }
    }

    /// <summary>Source files under <paramref name="relativeDir"/>, skipping bin/ and obj/.</summary>
    public static IEnumerable<string> SourceFiles(string relativeDir, params string[] extensions)
    {
        var root = Path.Combine(Root, relativeDir);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.Contains("/bin/", StringComparison.Ordinal) && !rel.Contains("/obj/", StringComparison.Ordinal)
                    && !rel.StartsWith("bin/", StringComparison.Ordinal) && !rel.StartsWith("obj/", StringComparison.Ordinal);
            });
    }
}
