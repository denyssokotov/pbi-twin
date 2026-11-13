using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Anonymize;

/// <summary>
/// Columns that share one mapping. Columns joined by relationships form a domain, so a key gets the same
/// pseudonym in the dimension and in every fact table. Columns of the same kind (all cities, all "Region"
/// columns, ...) share a domain too, so a literal like "West" maps to one value everywhere.
/// </summary>
public sealed class Domain(string id, Rule rule)
{
    public string Id { get; } = id;

    public Rule Rule { get; set; } = rule;

    public List<ColumnInfo> Columns { get; } = [];

    /// <summary>Prefix for Label / text Key pseudonyms, e.g. "Store" or "ON".</summary>
    public string Label { get; set; } = "Value";

    public bool IsShared => Columns.Count > 1;

    public override string ToString() => $"{Id} ({Rule}, {Columns.Count} columns)";
}

public sealed record ColumnPlan(ColumnInfo Column, Classification Classification, ColumnProfile? Profile)
{
    public required Domain Domain { get; set; }

    public Rule Rule => Domain.Rule;
}

/// <summary>The rule for every column. Built by heuristics, then reviewed and edited by the user.</summary>
public sealed class AnonymizationPlan
{
    private readonly Dictionary<string, ColumnPlan> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Domain> _domains = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ColumnPlan> Columns => _columns.Values;

    public IEnumerable<Domain> Domains => _domains.Values.Where(d => d.Columns.Count > 0);

    public ColumnPlan this[ColumnInfo column] => _columns[column.Id];

    public ColumnPlan this[string columnId] => _columns[columnId];

    /// <summary>Changes the rule of a column — and of every column in its domain.</summary>
    public void SetRule(string columnId, Rule rule)
    {
        var plan = _columns[columnId];
        if (!Rules.Allowed(plan.Column.Type).Contains(rule))
        {
            throw new ArgumentException($"Rule {rule} is not valid for a {plan.Column.Type} column.", nameof(rule));
        }

        plan.Domain.Rule = rule;
    }

    /// <summary>Moves a column into the domain of another column (e.g. ManagerKey → EmployeeKey).</summary>
    public void Link(string columnId, string targetColumnId)
    {
        var plan = _columns[columnId];
        var target = _columns[targetColumnId].Domain;
        if (plan.Domain == target)
        {
            return;
        }

        plan.Domain.Columns.Remove(plan.Column);
        plan.Domain = target;
        target.Columns.Add(plan.Column);
    }

    /// <summary>Gives a column its own domain again.</summary>
    public void Unlink(string columnId)
    {
        var plan = _columns[columnId];
        if (!plan.Domain.IsShared)
        {
            return;
        }

        plan.Domain.Columns.Remove(plan.Column);
        plan.Domain = AddDomain("col:" + columnId, plan.Domain.Rule, plan.Column);
    }

    /// <param name="model">Tables, columns and relationships.</param>
    /// <param name="profiles">Sample statistics per column id.</param>
    /// <param name="distinctValues">
    /// Optional: all distinct values of a column. Used to confirm self-references (e.g. ManagerKey ⊆ EmployeeKey)
    /// on the full data instead of on sampled examples.
    /// </param>
    public static AnonymizationPlan Create(ModelInfo model, IReadOnlyDictionary<string, ColumnProfile> profiles,
        Func<ColumnInfo, IReadOnlySet<object>>? distinctValues = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profiles);

        var plan = new AnonymizationPlan();

        var uf = new UnionFind();
        foreach (var r in model.Relationships)
        {
            uf.Union(r.FromId, r.ToId);
        }

        var keyDomainOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in uf.Groups())
        {
            foreach (var id in group)
            {
                keyDomainOf[id] = "key:" + group[0];
            }
        }

        foreach (var column in model.AllColumns)
        {
            profiles.TryGetValue(column.Id, out var profile);
            var inRelationship = keyDomainOf.TryGetValue(column.Id, out var keyDomain);
            var classification = Classifier.Classify(column, profile ?? new ColumnProfile(), inRelationship);
            var rule = Rules.Default(classification.Class, column.Type);

            var domainId = keyDomain ?? classification.Class switch
            {
                ColumnClass.City or ColumnClass.Country or ColumnClass.PersonName or ColumnClass.Email
                    or ColumnClass.Phone or ColumnClass.Iban or ColumnClass.Address or ColumnClass.FreeText
                    => "class:" + classification.Class,
                ColumnClass.Category or ColumnClass.Label => "label:" + column.Name.ToUpperInvariant(),
                _ => "col:" + column.Id,
            };

            var domain = plan.AddDomain(domainId, rule, column);
            plan._columns[column.Id] = new ColumnPlan(column, classification, profile) { Domain = domain };
        }

        plan.LinkSelfReferences(model, distinctValues);
        return plan;
    }

    private Domain AddDomain(string id, Rule rule, ColumnInfo column)
    {
        if (!_domains.TryGetValue(id, out var domain))
        {
            domain = new Domain(id, rule) { Label = LabelFor(column) };
            _domains[id] = domain;
        }

        domain.Columns.Add(column);
        return domain;
    }

    /// <summary>
    /// A key-like column without a relationship (e.g. DimEmployee[ManagerKey]) whose sampled values all occur
    /// in the unique key column of the same table joins that key's domain, so PATH() and lookups keep working.
    /// </summary>
    private void LinkSelfReferences(ModelInfo model, Func<ColumnInfo, IReadOnlySet<object>>? distinctValues)
    {
        foreach (var table in model.Tables)
        {
            var keys = table.Columns.Select(c => _columns[c.Id])
                .Where(p => p.Rule == Rule.Key && p.Profile is { AllDistinct: true } && (p.Column.IsKey || p.Domain.IsShared))
                .ToList();

            foreach (var p in table.Columns.Select(c => _columns[c.Id]))
            {
                if (p.Rule != Rule.Key || p.Domain.IsShared || p.Profile is null || p.Profile.Distinct == 0)
                {
                    continue;
                }

                var target = keys.FirstOrDefault(k => k != p && k.Column.Type == p.Column.Type
                    && (distinctValues is null
                        ? k.Profile!.Distinct >= p.Profile.Distinct && p.Profile.Examples.All(k.Profile.Examples.ToHashSet().Contains)
                        : distinctValues(p.Column) is { Count: > 0 } values && values.IsSubsetOf(distinctValues(k.Column))));

                if (target is not null)
                {
                    Link(p.Column.Id, target.Column.Id);
                }
            }
        }
    }

    /// <summary>"StoreName" → "Store", "Subcategory" → "Subcategory", "OrderNumber" (text key) → "ON".</summary>
    private static string LabelFor(ColumnInfo column)
    {
        var words = SplitWords(column.Name);
        if (words.Count > 1 && words[^1].Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }

        return words.Count == 0 ? "Value" : string.Join(' ', words);
    }

    internal static string Initials(string name)
    {
        var initials = new string(SplitWords(name).Select(w => char.ToUpperInvariant(w[0])).Where(char.IsAsciiLetter).Take(4).ToArray());
        return initials.Length == 0 ? "ID" : initials;
    }

    private static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
