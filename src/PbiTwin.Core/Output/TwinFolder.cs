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

    /// <summary>
    /// Creates (or recreates) <paramref name="root"/>. An existing folder is only replaced if PbiTwin created it.
    /// </summary>
    public static TwinFolder Create(string root, string name)
    {
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

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            if (!SkippedFolders.Contains(Path.GetFileName(dir)))
            {
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
