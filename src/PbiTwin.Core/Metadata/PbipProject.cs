using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;

namespace PbiTwin.Core.Metadata;

/// <summary>A Power BI Project on disk: the .pbip file, its report folder and the semantic model it points to.</summary>
public sealed class PbipProject
{
    private PbipProject(string pbipFile, string reportFolder, string modelFolder)
    {
        PbipFile = pbipFile;
        ReportFolder = reportFolder;
        SemanticModelFolder = modelFolder;
    }

    public string PbipFile { get; }

    public string Name => Path.GetFileNameWithoutExtension(PbipFile);

    public string RootFolder => Path.GetDirectoryName(PbipFile)!;

    public string ReportFolder { get; }

    public string SemanticModelFolder { get; }

    public bool IsTmdl => Directory.Exists(Path.Combine(SemanticModelFolder, "definition"));

    /// <summary>Opens a project from a .pbip file or a folder containing exactly one.</summary>
    public static PbipProject Open(string path)
    {
        var pbip = path;
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.pbip");
            pbip = files.Length == 1
                ? files[0]
                : throw new InvalidOperationException($"Expected exactly one .pbip file in '{path}', found {files.Length}.");
        }

        if (!File.Exists(pbip))
        {
            throw new FileNotFoundException("PBIP file not found.", pbip);
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(pbip))!;
        var doc = JsonNode.Parse(File.ReadAllText(pbip));
        var reportRel = doc?["artifacts"]?.AsArray()
            .Select(a => a?["report"]?["path"]?.GetValue<string>())
            .FirstOrDefault(p => p is not null)
            ?? throw new InvalidOperationException("The .pbip file does not reference a report.");

        var reportFolder = Path.GetFullPath(Path.Combine(root, reportRel));
        var pbir = JsonNode.Parse(File.ReadAllText(Path.Combine(reportFolder, "definition.pbir")));
        var modelRel = pbir?["datasetReference"]?["byPath"]?["path"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The report is not bound to a local semantic model (byPath). Live connections are not supported.");

        var modelFolder = Path.GetFullPath(Path.Combine(reportFolder, modelRel));
        return new PbipProject(Path.GetFullPath(pbip), reportFolder, modelFolder);
    }

    /// <summary>Loads the semantic model definition (TMDL folder or model.bim) through TOM.</summary>
    public Database LoadDatabase()
    {
        if (IsTmdl)
        {
            return TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(SemanticModelFolder, "definition"));
        }

        var bim = Path.Combine(SemanticModelFolder, "model.bim");
        return File.Exists(bim)
            ? JsonSerializer.DeserializeDatabase(File.ReadAllText(bim))
            : throw new InvalidOperationException("Semantic model has neither a 'definition' TMDL folder nor model.bim.");
    }
}
