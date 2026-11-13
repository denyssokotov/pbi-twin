using PbiTwin.SampleGenerator;

// Generates the ContosoPII sample dataset: deliberately "sensitive" looking, fully fictional data.
// Deterministic: the same seed always produces byte-identical CSV files.
//
// Usage: dotnet run --project samples/generator [-- <outputFolder>]

var outDir = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "samples", "ContosoPII", "data");
Directory.CreateDirectory(outDir);

var stats = new SampleData(seed: 20240601).Write(outDir);
foreach (var (table, rows) in stats)
{
    Console.WriteLine($"{table,-12} {rows,8:N0} rows");
}

Console.WriteLine($"Written to {Path.GetFullPath(outDir)}");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PbiTwin.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
