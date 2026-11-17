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

    private static readonly string[] Words = ["Kyiv", "Lviv", "Alpha", "Beta", "Gamma Corp", "O'Brien", "Müller", "Київ", "a,b", "say \"hi\"", "line\nbreak", "Delta #(x)", "Sales", "Region 7", "😀 Team", "שלום עולם", "e\u0301cole Nord", "Straße Ost", "東京 Shop", "Zoë\u00A0Park"];

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

    private string _lastLiteral = string.Empty;
    private string _factSource = string.Empty;
    private string _factFile = string.Empty;

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

        var textKeys = seed % 2 == 0;
        string KeyText(int k) => textKeys ? "K-" + k.ToString(CultureInfo.InvariantCulture) : k.ToString(CultureInfo.InvariantCulture);
        Column(dim, dimKey, textKeys ? DataType.String : DataType.Int64, key: true);
        var dimText = Name(rng, dimCols);
        Column(dim, dimText, DataType.String);
        Column(fact, factKey, textKeys ? DataType.String : DataType.Int64);
        var amount = Name(rng, factCols);
        Column(fact, amount, DataType.Decimal);
        var day = Name(rng, factCols);
        Column(fact, day, DataType.DateTime);
        var note = Name(rng, factCols);
        Column(fact, note, DataType.String);
        var flag = Name(rng, factCols);
        Column(fact, flag, DataType.Boolean);
        var qty = Name(rng, factCols);
        Column(fact, qty, DataType.Int64);
        var rate = Name(rng, factCols);
        Column(fact, rate, DataType.Double);
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

        // A measure that filters by a literal that is an original value of the text column.
        var literal = Words[rng.Next(Words.Length)];
        _lastLiteral = literal;
        fact.Measures.Add(new Measure
        {
            Name = "Filtered " + amount,
            Expression = $"CALCULATE([Total {amount.Replace("]", "]]", StringComparison.Ordinal)}], '{dim.Name.Replace("'", "''", StringComparison.Ordinal)}'[{dimText.Replace("]", "]]", StringComparison.Ordinal)}] = \"{literal.Replace("\"", "\"\"", StringComparison.Ordinal)}\")",
        });

        // An RLS role and a calculated column that mention original values too.
        var dimRef = $"'{dim.Name.Replace("'", "''", StringComparison.Ordinal)}'[{dimText.Replace("]", "]]", StringComparison.Ordinal)}]";
        var role = new ModelRole { Name = "Role " + seed, ModelPermission = ModelPermission.Read };
        role.TablePermissions.Add(new TablePermission { Table = dim, FilterExpression = $"{dimRef} = \"{Words[rng.Next(Words.Length)].Replace("\"", "\"\"", StringComparison.Ordinal)}\"" });
        db.Model.Roles.Add(role);
        dim.Columns.Add(new CalculatedColumn
        {
            Name = "Flag " + seed,
            DataType = DataType.String,
            Expression = $"IF({dimRef} = \"{Words[rng.Next(Words.Length)].Replace("\"", "\"\"", StringComparison.Ordinal)}\", \"{Words[rng.Next(Words.Length)].Replace("\"", "\"\"", StringComparison.Ordinal)}\", {dimRef})",
        });

        var modelFolder = Path.Combine(project, "M.SemanticModel");
        TmdlSerializer.SerializeDatabaseToFolder(db, Path.Combine(modelFolder, "definition"));
        File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"), """{ "version": "4.0" }""");
        Directory.CreateDirectory(Path.Combine(project, "M.Report", "definition"));
        File.WriteAllText(Path.Combine(project, "M.Report", "definition", "report.json"), "{}");
        WriteVisual(Path.Combine(project, "M.Report", "definition", "pages", "p1", "visuals", "v1", "visual.json"), dim.Name, dimText, Words[rng.Next(Words.Length)], Words[rng.Next(Words.Length)]);
        File.WriteAllText(Path.Combine(project, "M.Report", "definition.pbir"), """{ "version": "4.0", "datasetReference": { "byPath": { "path": "../M.SemanticModel" } } }""");
        File.WriteAllText(Path.Combine(project, "M.pbip"), """{ "version": "1.0", "artifacts": [ { "report": { "path": "M.Report" } } ] }""");

        var dims = rng.Next(1, 40);
        WriteCsv(Path.Combine(data, ModelRewriterFileName(dim.Name)), dim.Columns.Select(c => c.Name),
            Enumerable.Range(1, dims).Select(i => new[] { KeyText(i), Words[rng.Next(Words.Length)] }));
        _factFile = ModelRewriterFileName(fact.Name);
        _factSource = Path.Combine(data, _factFile);
        WriteCsv(_factSource, fact.Columns.Select(c => c.Name),
            Enumerable.Range(0, rng.Next(0, 200)).Select(_ => new[]
            {
                KeyText(rng.Next(1, dims + 1)),
                (rng.Next(1, 100000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture),
                (rng.Next(2) == 0
                    ? new DateTime(2023, 1, 1).AddDays(rng.Next(0, 700)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : new DateTime(2023, 1, 1).AddDays(rng.Next(0, 700)).AddMinutes(rng.Next(0, 1440)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                rng.Next(4) == 0 ? string.Empty : Words[rng.Next(Words.Length)],
                rng.Next(5) == 0 ? string.Empty : (rng.Next(2) == 0 ? "true" : "false"),
                rng.Next(5) == 0 ? string.Empty : rng.Next(-50, 5000).ToString(CultureInfo.InvariantCulture),
                rng.Next(5) == 0 ? string.Empty : (rng.NextDouble() * 1000 - 100).ToString("0.######", CultureInfo.InvariantCulture),
            }));

        var session = new TwinSession(PbipProject.Open(project), new CsvTableReader(data, c => c.SourceColumn));
        return session.Run(session.Scan(), new TwinOptions { Seed = "RANDOM-MODEL-" + seed, RenameMode = rename }, Path.Combine(_root, "out" + seed + rename, "Twin"));
    }

    /// <summary>A visual with a filter on the dimension's text column and a textbox that mentions a value.</summary>
    private static void WriteVisual(string path, string entity, string property, string filterValue, string textValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var literal = "'" + filterValue.Replace("'", "''", StringComparison.Ordinal) + "'";
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "v1",
            ["visual"] = new System.Text.Json.Nodes.JsonObject
            {
                ["objects"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["general"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    {
                        ["paragraphs"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        {
                            ["textRuns"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["value"] = "Report for " + textValue }),
                        }),
                    }),
                },
            },
            ["filterConfig"] = new System.Text.Json.Nodes.JsonObject
            {
                ["filters"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["filter"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Where"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        {
                            ["Condition"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["In"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["Expressions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["Column"] = new System.Text.Json.Nodes.JsonObject
                                        {
                                            ["Expression"] = new System.Text.Json.Nodes.JsonObject { ["SourceRef"] = new System.Text.Json.Nodes.JsonObject { ["Entity"] = entity } },
                                            ["Property"] = property,
                                        },
                                    }),
                                    ["Values"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["Literal"] = new System.Text.Json.Nodes.JsonObject { ["Value"] = literal },
                                    })),
                                },
                            },
                        }),
                    },
                }),
            },
        };
        File.WriteAllText(path, node.ToJsonString());
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
        for (var seed = 1; seed <= 40; seed++)
        {
            var result = Build(seed, rename);
            var failed = result.Report.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => c.Name + ": " + c.Detail).ToList();
            Assert.True(failed.Count == 0, $"seed {seed}, {rename}, literal [{_lastLiteral}] rules [{string.Join("; ", result.Report.Columns.Select(c => c.Column + "=" + c.Rule))}]: {string.Join("; ", failed)}\n{string.Join("\n", result.Report.Leaks.Take(5).Select(l => l.File + " | " + l.Columns + " | " + l.Context))}");
            if (rename == RenameMode.None)
            {
                AssertShapeKept(_factSource, Path.Combine(result.Folder.DataFolder, _factFile));
            }

            Assert.Contains(result.Report.Checks, c => c.Name == "Leak scan" && c.Status == CheckStatus.Pass);
            Assert.Contains(result.Report.Checks, c => c.Name == "DAX references" && c.Status == CheckStatus.Pass);
            Assert.Contains(result.Report.Checks, c => c.Name == "Twin opens" && c.Status == CheckStatus.Pass);
        }
    }

    /// <summary>Same rows in the same order; blanks stay blank, values stay values, and true/false are untouched.</summary>
    private static void AssertShapeKept(string original, string twin)
    {
        static List<string?[]> Read(string path)
        {
            using var reader = new StreamReader(path, Csv.Utf8);
            return Csv.ReadRecords(reader).Skip(1).ToList();
        }

        var (a, b) = (Read(original), Read(twin));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            for (var j = 0; j < a[i].Length; j++)
            {
                Assert.True(string.IsNullOrEmpty(a[i][j]) == string.IsNullOrEmpty(b[i][j]), $"row {i}, column {j}: '{a[i][j]}' became '{b[i][j]}'");
            }

            // Columns 4 is the true/false column.
            Assert.Equal(a[i][4], b[i][4]);
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
