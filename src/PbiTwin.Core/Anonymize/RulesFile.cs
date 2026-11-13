using System.Text.Json;

namespace PbiTwin.Core.Anonymize;

/// <summary>What happened when a rules file was applied to a plan.</summary>
public sealed record RulesApplied(int Applied, IReadOnlyList<string> NotInModel, IReadOnlyList<string> NotInFile, IReadOnlyList<string> Rejected);

/// <summary>
/// Saves the reviewed rules of a plan (column → rule, shared mappings) so the next run on the same model starts
/// from them. Contains table and column names only — no data, no seed.
/// </summary>
public static class RulesFile
{
    private sealed record Entry(string Column, string Rule, string? SameMappingAs);

    private sealed record Document(string Note, List<Entry> Columns);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Save(AnonymizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var entries = plan.Columns
            .OrderBy(p => p.Column.Id, StringComparer.Ordinal)
            .Select(p => new Entry(
                p.Column.Id,
                p.Rule.ToString(),
                p.Domain.IsShared ? p.Domain.Columns.Select(c => c.Id).Where(id => id != p.Column.Id).Order(StringComparer.Ordinal).First() : null))
            .ToList();

        return JsonSerializer.Serialize(new Document("PbiTwin rules: column names and rules only, no data.", entries), Options);
    }

    public static RulesApplied Apply(AnonymizationPlan plan, string json)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var doc = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException("Not a PbiTwin rules file.");
        var known = plan.Columns.Select(p => p.Column.Id).ToHashSet(StringComparer.Ordinal);
        var entries = (doc.Columns ?? []).Where(e => known.Contains(e.Column)).ToList();
        var rejected = new List<string>();

        // Shared mappings first (a rule belongs to the whole mapping), then the rules.
        foreach (var e in entries)
        {
            if (e.SameMappingAs is not null && known.Contains(e.SameMappingAs))
            {
                plan.Link(e.Column, e.SameMappingAs);
            }
            else if (e.SameMappingAs is null && plan[e.Column].Domain.IsShared)
            {
                plan.Unlink(e.Column);
            }
        }

        var applied = 0;
        foreach (var e in entries)
        {
            if (Enum.TryParse<Rule>(e.Rule, ignoreCase: true, out var rule)
                && Rules.Allowed(plan[e.Column].Column.Type).Contains(rule))
            {
                plan.SetRule(e.Column, rule);
                applied++;
            }
            else
            {
                rejected.Add($"{e.Column}: {e.Rule}");
            }
        }

        var inFile = (doc.Columns ?? []).Select(e => e.Column).ToHashSet(StringComparer.Ordinal);
        return new RulesApplied(
            applied,
            inFile.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToList(),
            known.Where(id => !inFile.Contains(id)).Order(StringComparer.Ordinal).ToList(),
            rejected);
    }
}
