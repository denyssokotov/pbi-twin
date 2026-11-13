using System.Globalization;
using PbiTwin.Core;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Connection;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Verify;

// pbitwin-cli — a thin command line over PbiTwin.Core for automation. Exit codes: 0 = all checks passed,
// 1 = error, 2 = twin created but some checks failed.

const string Usage = """
    pbitwin-cli — anonymized twin of a Power BI model, 100% offline.

    Create a twin:
      pbitwin-cli --pbip <file.pbip|folder> --port <n> --out <folder> [options]
      pbitwin-cli --pbip <file.pbip|folder> --csv <folder> --out <folder> [options]

        --port <n>        Power BI Desktop's local port (see: pbitwin-cli list)
        --csv <folder>    read the original data from <Table>.csv files instead (header = source column)
        --seed <text>     reproduce a twin (default: random, printed once, never saved)
        --name <text>     twin name (default: <Project>_Twin, or Twin when renaming)
        --rename <mode>   none | neutral | semantic (default: none)
        --rows <n>        row limit for fact tables
        --noise <pct>     per-row noise for amounts (default: 3)
        --keep-descriptions
        --rules <file>    apply rules saved earlier (column names and rules only)
        --save-rules <file>  save the rules used for this run

    List running Power BI Desktop models:
      pbitwin-cli list

    Translate DAX with a codebook (reads stdin, writes stdout):
      pbitwin-cli translate --codebook <file> --to real|twin
    """;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    return args[0] switch
    {
        "list" => List(),
        "translate" => Translate(Options(args[1..])),
        _ => Create(Options(args)),
    };
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or FormatException or InvalidDataException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static int List()
{
    var instances = DesktopInstances.Find();
    if (instances.Count == 0)
    {
        Console.WriteLine("No running Power BI Desktop found.");
    }

    foreach (var i in instances)
    {
        try
        {
            var m = DesktopInstances.Describe(i);
            Console.WriteLine($"port {i.Port}  {m.Tables.Count} tables  {string.Join(", ", m.Tables.Take(6))}{(m.Tables.Count > 6 ? ", …" : string.Empty)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine($"port {i.Port}  not reachable: {ex.Message}");
        }
    }

    return 0;
}

static int Create(Dictionary<string, string?> o)
{
    var pbip = Required(o, "pbip");
    var output = Required(o, "out");
    var seedGiven = o.TryGetValue("seed", out var seed) && !string.IsNullOrEmpty(seed);
    var options = new TwinOptions
    {
        Seed = seedGiven ? seed! : SeededHash.NewSeed(),
        RenameMode = o.TryGetValue("rename", out var r) ? Enum.Parse<RenameMode>(r ?? "none", ignoreCase: true) : RenameMode.None,
        FactRowLimit = o.TryGetValue("rows", out var rows) ? long.Parse(rows!, CultureInfo.InvariantCulture) : null,
        NoisePercent = o.TryGetValue("noise", out var noise) ? double.Parse(noise!, CultureInfo.InvariantCulture) : 3,
        ClearDescriptions = !o.ContainsKey("keep-descriptions"),
    };

    if (!seedGiven)
    {
        Console.Error.WriteLine($"seed: {options.Seed}   (not saved anywhere; pass --seed to reproduce)");
    }

    ITableReader reader;
    if (o.TryGetValue("csv", out var csv))
    {
        reader = new CsvTableReader(csv!, c => c.SourceColumn);
    }
    else
    {
        var port = int.Parse(Required(o, "port"), CultureInfo.InvariantCulture);
        var model = DesktopInstances.Describe(new DesktopInstance(port, string.Empty));
        reader = new AdomdTableReader(LocalhostGuard.BuildConnectionString(port, model.DatabaseId));
    }

    try
    {
        var session = new TwinSession(PbipProject.Open(pbip), reader);
        var lastStep = string.Empty;
        var progress = new SyncProgress(p =>
        {
            var line = p.Table is null ? p.Step : $"{p.Step}: {p.Table}";
            if (line != lastStep)
            {
                Console.Error.WriteLine(line);
                lastStep = line;
            }
        });

        var plan = session.Scan(progress: progress);
        if (o.TryGetValue("rules", out var rulesFile))
        {
            var applied = RulesFile.Apply(plan, File.ReadAllText(rulesFile!));
            Console.Error.WriteLine($"rules: {applied.Applied} applied, {applied.NotInFile.Count} new columns, {applied.NotInModel.Count} no longer in the model, {applied.Rejected.Count} rejected");
        }

        if (o.TryGetValue("save-rules", out var saveRules))
        {
            File.WriteAllText(saveRules!, RulesFile.Save(plan));
        }

        var result = session.Run(plan, options, output, o.GetValueOrDefault("name"), progress);

        Console.WriteLine();
        foreach (var c in result.Report.Checks)
        {
            Console.WriteLine($"{(c.Status == CheckStatus.Pass ? "PASS" : c.Status == CheckStatus.Warn ? "WARN" : "FAIL")}  {c.Name}: {c.Detail}");
        }

        foreach (var w in result.Report.Warnings)
        {
            Console.WriteLine("note  " + w);
        }

        Console.WriteLine();
        Console.WriteLine("twin:     " + result.Folder.PbipFile);
        Console.WriteLine("report:   " + result.Folder.TrustReportFile);
        if (result.CodebookFile is not null)
        {
            Console.WriteLine("codebook: " + result.CodebookFile + "   (keep private, do not share with the twin)");
        }

        return result.Report.Passed ? 0 : 2;
    }
    finally
    {
        (reader as IDisposable)?.Dispose();
    }
}

static int Translate(Dictionary<string, string?> o)
{
    var codebook = NameMap.FromJson(File.ReadAllText(Required(o, "codebook")));
    var input = Console.In.ReadToEnd();
    var unresolved = new List<string>();
    var to = Required(o, "to").ToLowerInvariant();
    Console.Write(to switch
    {
        "real" => DaxTranslator.ToReal(input, codebook, unresolved),
        "twin" => DaxTranslator.ToAnonymous(input, codebook, unresolved),
        _ => throw new ArgumentException("--to must be 'real' or 'twin'."),
    });

    if (unresolved.Count > 0)
    {
        Console.Error.WriteLine("not in codebook: " + string.Join(", ", unresolved.Distinct()));
    }

    return 0;
}

static Dictionary<string, string?> Options(string[] args)
{
    var o = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'. Run pbitwin-cli --help.");
        }

        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        o[key] = hasValue ? args[++i] : null;
    }

    return o;
}

static string Required(Dictionary<string, string?> o, string key) =>
    o.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : throw new ArgumentException($"--{key} is required. Run pbitwin-cli --help.");

/// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
internal sealed class SyncProgress(Action<TwinProgress> report) : IProgress<TwinProgress>
{
    public void Report(TwinProgress value) => report(value);
}
