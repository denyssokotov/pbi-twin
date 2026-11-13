using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Classify;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;
using PbiTwin.Core.Text;
using PbiTwin.Core.Verify;

namespace PbiTwin.Core;

public sealed record TwinProgress(string Step, string? Table, double Percent);

public sealed record PreviewTable(string Table, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<object?[]> Before, IReadOnlyList<object?[]> After);

public sealed record TwinResult(TwinFolder Folder, TrustReport Report);

/// <summary>
/// One anonymization job: <see cref="Scan"/> → (user edits the plan) → <see cref="Preview"/> → <see cref="Run"/>.
/// Original values only ever live in memory; the only files written are the twin's.
/// </summary>
public sealed class TwinSession
{
    private readonly ITableReader _reader;
    private readonly Dictionary<string, List<object?[]>> _samples = new(StringComparer.Ordinal);

    public TwinSession(PbipProject project, ITableReader reader)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(reader);
        Project = project;
        _reader = reader;
        Model = ModelInfo.FromModel(project.LoadDatabase().Model);
    }

    public PbipProject Project { get; }

    public ModelInfo Model { get; }

    /// <summary>Profiles a sample of every table and proposes a rule for every column.</summary>
    public AnonymizationPlan Scan(int sampleRows = 10_000, IProgress<TwinProgress>? progress = null)
    {
        var profiles = new Dictionary<string, ColumnProfile>(StringComparer.Ordinal);
        for (var i = 0; i < Model.Tables.Count; i++)
        {
            var t = Model.Tables[i];
            progress?.Report(new TwinProgress("Scanning", t.Name, 100.0 * i / Model.Tables.Count));
            var rows = _reader.ReadRows(t, t.Columns, sampleRows).ToList();
            _samples[t.Name] = rows.Take(10).ToList();
            foreach (var (column, profile) in ColumnProfile.Build(t, rows))
            {
                profiles[$"{t.Name}[{column}]"] = profile;
            }
        }

        return AnonymizationPlan.Create(Model, profiles);
    }

    /// <summary>Before/after for the first rows of every table, in memory only. Pseudonym numbers may differ from the final run.</summary>
    public IReadOnlyList<PreviewTable> Preview(AnonymizationPlan plan, TwinOptions options)
    {
        var anonymizer = new Anonymizer(plan, options);
        foreach (var domain in plan.Domains.Where(d => Rules.UsesDictionary(d.Rule)))
        {
            var values = domain.Columns.SelectMany(c => Sample(c)).Where(v => v is not null).Cast<object>();
            anonymizer.BuildDictionary(domain, values);
        }

        return Model.Tables.Select(t =>
        {
            var before = _samples.GetValueOrDefault(t.Name) ?? [];
            var noise = anonymizer.NoiseFor(t.Name);
            var after = before.Select(row => t.Columns.Select((c, i) => anonymizer.Transform(plan[c], row[i], noise)).ToArray()).ToList();
            return new PreviewTable(t.Name, t.Columns, before, after);
        }).ToList();

        IEnumerable<object?> Sample(ColumnInfo c)
        {
            var t = Model.Table(c.Table);
            var i = t.Columns.ToList().IndexOf(c);
            return (_samples.GetValueOrDefault(c.Table) ?? []).Select(r => r[i]);
        }
    }

    /// <summary>Builds the twin in <paramref name="outputFolder"/> and verifies it.</summary>
    public TwinResult Run(AnonymizationPlan plan, TwinOptions options, string outputFolder, string? twinName = null,
        IProgress<TwinProgress>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var anonymizer = new Anonymizer(plan, options);
        var facts = new RunFacts();
        var twin = TwinFolder.Create(outputFolder, twinName ?? Project.Name + "_Twin");

        // 1. One-to-one dictionaries from all distinct values (fact tables read only DISTINCT of their keys).
        progress?.Report(new TwinProgress("Building key mappings", null, 0));
        var distinct = new Dictionary<string, HashSet<object>>(StringComparer.Ordinal);
        foreach (var domain in plan.Domains.Where(d => Rules.UsesDictionary(d.Rule)))
        {
            cancellation.ThrowIfCancellationRequested();
            var all = new HashSet<object>();
            foreach (var c in domain.Columns)
            {
                var set = _reader.ReadDistinct(c).ToHashSet();
                distinct[c.Id] = set;
                all.UnionWith(set);
            }

            anonymizer.BuildDictionary(domain, all);
        }

        anonymizer.RememberDictionaries();
        foreach (var r in Model.Relationships)
        {
            if (distinct.TryGetValue(r.FromId, out var from) && distinct.TryGetValue(r.ToId, out var to))
            {
                facts.OriginalOrphans[r] = from.LongCount(v => !to.Contains(v));
            }
        }

        // 2. Stream every table: read → transform → write CSV.
        var totals = Model.Tables.ToDictionary(t => t.Name, t => _reader.CountRows(t), StringComparer.Ordinal);
        var grandTotal = Math.Max(1, totals.Values.Sum(n => Math.Min(n, options.FactRowLimit ?? long.MaxValue)));
        long done = 0;
        foreach (var t in Model.Tables)
        {
            var limit = t.IsFact ? options.FactRowLimit : null;
            var expected = Math.Min(totals[t.Name], limit ?? long.MaxValue);
            var noise = anonymizer.NoiseFor(t.Name);
            var columns = t.Columns.Select(c => plan[c]).ToArray();
            facts.Metrics.Begin(t, plan, anonymizer.Parameters.DateShiftDays);

            long written = 0;
            using (var writer = new CsvWriter(Path.Combine(twin.DataFolder, ModelRewriter.CsvFileName(t.Name)), t.Columns))
            {
                foreach (var row in _reader.ReadRows(t, t.Columns, limit))
                {
                    var output = new object?[row.Length];
                    for (var i = 0; i < row.Length; i++)
                    {
                        output[i] = anonymizer.Transform(columns[i], row[i], noise);
                    }

                    writer.Write(output);
                    facts.Metrics.Add(t.Name, row, output);
                    written++;
                    if (written % 20_000 == 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress?.Report(new TwinProgress("Writing data", t.Name, 100.0 * (done + written) / grandTotal));
                    }
                }
            }

            done += written;
            facts.Counts[t.Name] = new TableCount(t.Name, totals[t.Name], written, limit is not null && totals[t.Name] > limit);
            progress?.Report(new TwinProgress("Writing data", t.Name, 100.0 * done / grandTotal));
        }

        // 3. Copy the project, point it at the CSVs, strip sources, rewrite literals.
        progress?.Report(new TwinProgress("Writing project", null, 100));
        var literals = new LiteralRewriter(anonymizer.KnownText);
        twin.CopyProject(Project);
        var database = Project.LoadDatabase();
        ModelRewriter.Rewrite(database.Model, Model, twin.DataFolder, literals, options.ClearDescriptions);
        twin.WriteModel(database, Project.IsTmdl);
        ReportRewriter.Rewrite(twin.ReportFolder, literals, anonymizer.Parameters.DateShiftDays);

        // 4. Verify by reading the twin back from disk.
        progress?.Report(new TwinProgress("Verifying", null, 100));
        var report = Verifier.Verify(twin, Model, anonymizer, facts, literals);
        File.WriteAllText(twin.TrustReportFile, report.ToMarkdown(), Csv.Utf8);

        return new TwinResult(twin, report);
    }

    /// <summary>Tables in the PBIP that the running model does not have, and vice versa.</summary>
    public (IReadOnlyList<string> MissingInInstance, IReadOnlyList<string> MissingInProject) Compare(IEnumerable<string> instanceTables)
    {
        var instance = instanceTables.Where(n => !n.StartsWith("LocalDateTable_", StringComparison.Ordinal) && !n.StartsWith("DateTableTemplate_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var project = Model.Tables.Select(t => t.Name).Concat(Model.SkippedTables.Select(s => s.Table))
            .Where(n => !n.StartsWith("LocalDateTable_", StringComparison.Ordinal) && !n.StartsWith("DateTableTemplate_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        return (project.Except(instance).Order(StringComparer.Ordinal).ToList(), instance.Except(project).Order(StringComparer.Ordinal).ToList());
    }

    internal static Database LoadForTests(PbipProject p) => p.LoadDatabase();
}
