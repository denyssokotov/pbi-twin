using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Metadata;
using JsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace PbiTwin.Core.Output;

/// <summary>
/// The output folder: <c>&lt;Twin&gt;.pbip</c>, <c>&lt;Twin&gt;.Report</c>, <c>&lt;Twin&gt;.SemanticModel</c>, <c>data/</c>
/// and <c>TRUST-REPORT.md</c>. The input project is only read, never modified.
/// </summary>
public sealed class TwinFolder
{
    private const string Marker = ".pbitwin";

    /// <summary>Folders never copied: local caches (may contain the original data) and ad-hoc query scripts.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".pbi", "DAXQueries", "TMDLScripts" };

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".abf", ".pbix", ".bak" };

    private TwinFolder(string root, string name)
    {
        Root = root;
        Name = name;
    }

    public string Root { get; }

    public string Name { get; }

    public string DataFolder => Path.Combine(Root, "data");

    public string PbipFile => Path.Combine(Root, Name + ".pbip");

    public string ReportFolder => Path.Combine(Root, Name + ".Report");

    public string SemanticModelFolder => Path.Combine(Root, Name + ".SemanticModel");

    public string TrustReportFile => Path.Combine(Root, "TRUST-REPORT.md");

    /// <summary>Stored next to (not inside) the twin folder, so it is not shared by accident.</summary>
    public string CodebookFile => Path.Combine(Path.GetDirectoryName(Root)!, Name + ".codebook.json");

    /// <summary>The diagram layout lists table names; Power BI recreates it.</summary>
    public void RemoveDiagramLayout()
    {
        var layout = Path.Combine(SemanticModelFolder, "diagramLayout.json");
        if (File.Exists(layout))
        {
            File.Delete(layout);
        }
    }

    /// <summary>Throws an <see cref="ArgumentException"/> with a readable message if the name cannot be a file name.</summary>
    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new ArgumentException($"'{name}' cannot be used as a file name. Use letters, digits, spaces, '-' or '_'.", nameof(name));
        }
    }

    /// <summary>The output must not be, contain or sit inside the input project: it is deleted and rewritten on every run.</summary>
    public static void EnsureSeparate(string outputRoot, string sourceRoot)
    {
        var output = Path.TrimEndingDirectorySeparator(Resolve(outputRoot)) + Path.DirectorySeparatorChar;
        var source = Path.TrimEndingDirectorySeparator(Resolve(sourceRoot)) + Path.DirectorySeparatorChar;
        if (output.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(output, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output folder must be outside the source project folder (and must not contain it). Choose another output folder.");
        }
    }

    /// <summary>The full path with every existing symbolic link or junction followed, so a link into the project cannot hide it.</summary>
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is not null)
                {
                    current = target;
                }
            }
        }

        return current;
    }

    /// <summary>
    /// Creates (or recreates) <paramref name="root"/>. An existing folder is only replaced if PbiTwin created it.
    /// </summary>
    public static TwinFolder Create(string root, string name)
    {
        ValidateName(name);
        root = Path.GetFullPath(root);
        if (Directory.Exists(root))
        {
            var isOurs = File.Exists(Path.Combine(root, Marker));
            if (!isOurs && Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException($"Output folder '{root}' is not empty and was not created by PbiTwin.");
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Marker), "Created by PbiTwin. This folder is replaced on the next run.\n");
        var folder = new TwinFolder(root, name);
        Directory.CreateDirectory(folder.DataFolder);
        return folder;
    }

    /// <summary>Copies the report and semantic model under the twin name and fixes the references between them.</summary>
    public void CopyProject(PbipProject source)
    {
        ArgumentNullException.ThrowIfNull(source);

        CopyDirectory(source.ReportFolder, ReportFolder);
        CopyDirectory(source.SemanticModelFolder, SemanticModelFolder);

        var pbip = JsonNode.Parse(File.ReadAllText(source.PbipFile))!;
        foreach (var artifact in pbip["artifacts"]!.AsArray())
        {
            if (artifact?["report"] is JsonObject report)
            {
                report["path"] = Name + ".Report";
            }
        }

        File.WriteAllText(PbipFile, pbip.ToJsonString(new() { WriteIndented = true }) + "\n");

        var pbirPath = Path.Combine(ReportFolder, "definition.pbir");
        var pbir = JsonNode.Parse(File.ReadAllText(pbirPath))!;
        pbir["datasetReference"]!["byPath"]!["path"] = "../" + Name + ".SemanticModel";
        File.WriteAllText(pbirPath, pbir.ToJsonString(new() { WriteIndented = true }) + "\n");

        foreach (var platform in new[] { Path.Combine(ReportFolder, ".platform"), Path.Combine(SemanticModelFolder, ".platform") })
        {
            if (File.Exists(platform))
            {
                var node = JsonNode.Parse(File.ReadAllText(platform))!;
                if (node["metadata"] is JsonObject meta)
                {
                    meta["displayName"] = Name;
                }

                if (node["config"] is JsonObject config)
                {
                    config["logicalId"] = Guid.NewGuid().ToString();
                }

                File.WriteAllText(platform, node.ToJsonString(new() { WriteIndented = true }) + "\n");
            }
        }
    }

    /// <summary>Replaces the copied model definition with the rewritten TOM database, in the original format.</summary>
    public void WriteModel(Database database, bool tmdl)
    {
        ArgumentNullException.ThrowIfNull(database);

        var definition = Path.Combine(SemanticModelFolder, "definition");
        var bim = Path.Combine(SemanticModelFolder, "model.bim");
        if (tmdl)
        {
            if (Directory.Exists(definition))
            {
                Directory.Delete(definition, recursive: true);
            }

            TmdlSerializer.SerializeDatabaseToFolder(database, definition);
        }
        else
        {
            File.WriteAllText(bim, JsonSerializer.SerializeDatabase(database, new SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true }));
        }
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            // Analysis Services caches hold the original data; links could lead anywhere.
            if (!SkippedExtensions.Contains(Path.GetExtension(file)) && !IsLink(file))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            if (!SkippedFolders.Contains(Path.GetFileName(dir)) && !IsLink(dir))
            {
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
