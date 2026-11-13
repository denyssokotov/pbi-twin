using System.Globalization;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;

namespace PbiTwin.Core.Verify;

/// <summary>Everything the pipeline learned while writing the twin, handed to the verifier.</summary>
public sealed class RunFacts
{
    public Dictionary<string, TableCount> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>Distinct foreign-key values without a matching key in the original data, per relationship.</summary>
    public Dictionary<RelationshipInfo, long> OriginalOrphans { get; } = [];

    public Metrics Metrics { get; } = new();
}

/// <summary>Checks a finished twin by reading it back from disk. Mandatory after every run.</summary>
public static class Verifier
{
    public static TrustReport Verify(TwinFolder twin, ModelInfo model, Anonymizer anonymizer, RunFacts facts, LiteralRewriter literals)
    {
        ArgumentNullException.ThrowIfNull(twin);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(anonymizer);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(literals);

        var inv = CultureInfo.InvariantCulture;
        var report = new TrustReport { TwinName = twin.Name };
        var reader = new CsvTableReader(twin.DataFolder, c => c.Name);

        // 1. Row counts.
        var countsOk = true;
        foreach (var t in model.Tables)
        {
            var expected = facts.Counts[t.Name];
            var actual = reader.CountRows(new TableInfo(ModelRewriter.CsvFileName(t.Name)[..^4], t.Columns, t.IsFact));
            var limitedOk = expected.Limited && actual == expected.Twin;
            countsOk &= actual == expected.Original || limitedOk;
            report.Tables.Add(expected with { Twin = actual });
        }

        report.Checks.Add(new("Row counts", countsOk ? CheckStatus.Pass : CheckStatus.Fail,
            countsOk ? $"{model.Tables.Count} tables match the original (or the row limit)" : "some tables have a different number of rows"));

        // 2. Referential integrity and 3. key uniqueness.
        var orphansOk = true;
        var uniqueOk = true;
        foreach (var r in model.Relationships)
        {
            var fromColumn = model.Table(r.FromTable).Columns.First(c => c.Name == r.FromColumn);
            var toColumn = model.Table(r.ToTable).Columns.First(c => c.Name == r.ToColumn);
            var toValues = new HashSet<object>();
            long nonNull = 0;
            foreach (var row in reader.ReadRows(Renamed(model.Table(r.ToTable)), [toColumn]))
            {
                if (row[0] is { } v)
                {
                    nonNull++;
                    toValues.Add(v);
                }
            }

            var orphans = reader.ReadDistinct(fromColumn with { Table = Renamed(model.Table(r.FromTable)).Name }).LongCount(v => !toValues.Contains(v));
            long? original = facts.OriginalOrphans.TryGetValue(r, out var o) ? o : null;
            var unique = nonNull == toValues.Count;
            orphansOk &= orphans == 0 || (original is not null && orphans <= original);
            uniqueOk &= unique;
            report.Links.Add(new LinkCheck($"{r.FromId} → {r.ToId}{(r.IsActive ? string.Empty : " (inactive)")}", orphans, original, unique));
        }

        report.Checks.Add(new("Referential integrity", orphansOk ? CheckStatus.Pass : CheckStatus.Fail,
            orphansOk ? $"every foreign key in {model.Relationships.Count} relationships still finds its key" : "some foreign keys lost their match"));
        report.Checks.Add(new("Key uniqueness", uniqueOk ? CheckStatus.Pass : CheckStatus.Fail,
            uniqueOk ? "all one-side keys are unique" : "some one-side keys are no longer unique"));

        // 4. Leak scan over every file of the twin.
        // The output path is written into the DataFolder parameter. It is the user's own path, not data, so it is
        // checked separately and reported as a warning.
        var (leaks, values, files) = LeakScanner.Scan(twin.Root, anonymizer.KnownText, [twin.TrustReportFile], twin.DataFolder);
        if (LeakScanner.ColumnsFoundIn(twin.DataFolder, anonymizer.KnownText) is { Count: > 0 } pathColumns)
        {
            report.Warnings.Add($"The output path (stored in the DataFolder parameter) contains a word that is also a value of {string.Join(", ", pathColumns)}. If the path is sensitive, choose another output folder or change the parameter before sharing.");
        }

        report.Leaks.AddRange(leaks);
        report.LeakValuesChecked = values;
        report.LeakFilesScanned = files;
        report.Checks.Add(new("Leak scan", leaks.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            leaks.Count == 0 ? $"0 leaks found ({values:N0} original values, {files} files)" : $"{leaks.Count} leaks found"));

        // 5. Shape of the data.
        var main = facts.Metrics.Tables.Where(m => m.MoneyColumns.Count > 0).OrderByDescending(m => m.Rows).FirstOrDefault();
        if (main?.MonthlyTrendCorrelation() is { } corr)
        {
            report.Checks.Add(new("Trend preserved", corr >= 0.98 ? CheckStatus.Pass : CheckStatus.Warn,
                string.Create(inv, $"monthly {main.MainColumn} in {main.Table.Name}: correlation {corr:0.0000} over {main.Months} months")));
        }

        if (main?.MainRatioChange() is { } ratio)
        {
            report.Checks.Add(new("Ratios preserved", ratio.RelativeChange <= 0.01 ? CheckStatus.Pass : CheckStatus.Warn,
                string.Create(inv, $"{ratio.A} / {ratio.B} in {main.Table.Name} changed by {ratio.RelativeChange:0.00%} (margins and shares follow)")));
        }

        // Context.
        foreach (var (where, n) in literals.Replacements)
        {
            report.LiteralReplacements[where] = n;
        }

        report.Warnings.AddRange(literals.Warnings);
        report.SkippedTables.AddRange(model.SkippedTables.Select(s => $"{s.Table}: {s.Reason}"));
        foreach (var p in anonymizer.Plan.Columns.OrderBy(p => p.Column.Id, StringComparer.Ordinal))
        {
            report.Columns.Add(new ColumnRuleRow(p.Column.Id, p.Classification.Class.ToString(), p.Rule.ToString(),
                p.Domain.IsShared ? string.Join(", ", p.Domain.Columns.Where(c => c != p.Column).Select(c => c.Id)) : string.Empty));
        }

        return report;

        static TableInfo Renamed(TableInfo t) => t with { Name = ModelRewriter.CsvFileName(t.Name)[..^4] };
    }
}
