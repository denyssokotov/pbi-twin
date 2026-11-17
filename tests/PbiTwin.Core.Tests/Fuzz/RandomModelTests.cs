using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Text;
using PbiTwin.Core.Verify;

namespace PbiTwin.Core.Tests.Fuzz;

/// <summary>Random models with awkward names and values go through the whole pipeline; no check may fail.</summary>
public sealed class RandomModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pbitwin-tests", Guid.NewGuid().ToString("N"));

    private static readonly string[] Words = ["Kyiv", "Lviv", "Alpha", "Beta", "Gamma Corp", "O'Brien", "Müller", "Київ", "a,b", "say \"hi\"", "line\nbreak", "Delta #(x)", "Sales", "Region 7"];

    private static readonly string[] NameParts = ["Customer", "Amount", "Date", "Region", "Name", "Qty", "Note", "Total", "City", "Status", "Id", "Price"];

    private static readonly string[] Decorations = ["", " ", "_", "-", " #1", "'s", " (old)", "é", " [x]", "#(a)"];

    private static string Name(Random rng, HashSet<string> used)
    {
        while (true)
        {
            var n = NameParts[rng.Next(NameParts.Length)] + Decorations[rng.Next(Decorations.Length)] + (rng.Next(3) == 0 ? rng.Next(100).ToString(CultureInfo.InvariantCulture) : string.Empty);
            n = n.Trim();
            if (n.Length > 0 && used.Add(n.ToLowerInvariant()))
            {
                return n;
            }
        }
    }

    private TwinResult Build(int seed, RenameMode rename)
    {
        var rng = new Random(seed);
        var project = Path.Combine(_root, "source" + seed + rename);
        var data = Path.Combine(project, "data");
        Directory.CreateDirectory(data);
        var db = new Database { Name = "M", ID = "M", CompatibilityLevel = 1567, Model = new Model() };

        var tableNames = new HashSet<string>();
        var dim = new Table { Name = "Dim " + Name(rng, tableNames) };
        var fact = new Table { Name = "Fact " + Name(rng, tableNames) };
        var dimCols = new HashSet<string>();
        var factCols = new HashSet<string>();
        var dimKey = Name(rng, dimCols);
        var factKey = Name(rng, factCols);

        void Column(Table t, string name, DataType type, bool key = false) =>
            t.Columns.Add(new DataColumn { Name = name, DataType = type, SourceColumn = name, IsKey = key });

        Column(dim, dimKey, DataType.Int64, key: true);
        var dimText = Name(rng, dimCols);
        Column(dim, dimText, DataType.String);
        Column(fact, factKey, DataType.Int64);
        var amount = Name(rng, factCols);
        Column(fact, amount, DataType.Decimal);
        var day = Name(rng, factCols);
        Column(fact, day, DataType.DateTime);
        var note = Name(rng, factCols);
        Column(fact, note, DataType.String);
        foreach (var t in new[] { dim, fact })
        {
            t.Partitions.Add(new Partition { Name = t.Name, Source = new MPartitionSource { Expression = "let Source = null in Source" } });
            db.Model.Tables.Add(t);
        }

        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "r",
            FromColumn = fact.Columns[factKey],
            ToColumn = dim.Columns[dimKey],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One,
        });
        fact.Measures.Add(new Measure { Name = "Total " + amount, Expression = $"SUM('{fact.Name.Replace("'", "''", StringComparison.Ordinal)}'[{amount.Replace("]", "]]", StringComparison.Ordinal)}])" });

        var modelFolder = Path.Combine(project, "M.SemanticModel");
        TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelFolder, "definition"));
        File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"), """{ "version": "4.0" }""");
        Directory.CreateDirectory(Path.Combine(project, "M.Report", "definition"));
        File.WriteAllText(Path.Combine(project, "M.Report", "definition", "report.json"), "{}");
        File.WriteAllText(Path.Combine(project, "M.Report", "definition.pbir"), """{ "version": "4.0", "datasetReference": { "byPath": { "path": "../M.SemanticModel" } } }""");
        File.WriteAllText(Path.Combine(project, "M.pbip"), """{ "version": "1.0", "artifacts": [ { "report": { "path": "M.Report" } } ] }""");

        var dims = rng.Next(1, 40);
        WriteCsv(Path.Combine(data, ModelRewriterFileName(dim.Name)), dim.Columns.Select(c => c.Name),
            Enumerable.Range(1, dims).Select(i => new[] { i.ToString(CultureInfo.InvariantCulture), Words[rng.Next(Words.Length)] }));
        WriteCsv(Path.Combine(data, ModelRewriterFileName(fact.Name)), fact.Columns.Select(c => c.Name),
            Enumerable.Range(0, rng.Next(0, 200)).Select(_ => new[]
            {
                rng.Next(1, dims + 1).ToString(CultureInfo.InvariantCulture),
                (rng.Next(1, 100000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture),
                new DateTime(2023, 1, 1).AddDays(rng.Next(0, 700)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rng.Next(4) == 0 ? string.Empty : Words[rng.Next(Words.Length)],
            }));

        var session = new TwinSession(PbipProject.Open(project), new CsvTableReader(data, c => c.SourceColumn));
        return session.Run(session.Scan(), new TwinOptions { Seed = "RANDOM-MODEL-" + seed, RenameMode = rename }, Path.Combine(_root, "out" + seed + rename, "Twin"));
    }

    private static string ModelRewriterFileName(string table) => PbiTwin.Core.Output.ModelRewriter.CsvFileName(table);

    private static void WriteCsv(string path, IEnumerable<string> header, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(Csv.Escape))).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(string.Join(',', r.Select(Csv.Escape))).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), Csv.Utf8);
    }

    [Theory]
    [InlineData(RenameMode.None)]
    [InlineData(RenameMode.Neutral)]
    public void Random_models_produce_twins_where_every_check_passes(RenameMode rename)
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var result = Build(seed, rename);
            Assert.Contains(result.Report.Checks, c => c.Name == "Leak scan" && c.Status == CheckStatus.Pass);
            Assert.Contains(result.Report.Checks, c => c.Name == "Twin opens" && c.Status == CheckStatus.Pass);
            var failed = result.Report.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => c.Name + ": " + c.Detail).ToList();
            Assert.True(failed.Count == 0, $"seed {seed}, {rename}: {string.Join("; ", failed)}\n{string.Join("\n", result.Report.Leaks.Take(5).Select(l => l.File + " " + l.Context))}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
