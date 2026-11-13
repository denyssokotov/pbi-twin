namespace PbiTwin.Core.Metadata;

/// <summary>Disjoint sets over string ids (column ids like <c>Table[Column]</c>).</summary>
public sealed class UnionFind
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    public string Find(string x)
    {
        if (!_parent.TryGetValue(x, out var p))
        {
            _parent[x] = x;
            return x;
        }

        if (p == x)
        {
            return x;
        }

        var root = Find(p);
        _parent[x] = root;
        return root;
    }

    public void Union(string a, string b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb)
        {
            // Deterministic root choice keeps domain ids stable between runs.
            if (string.CompareOrdinal(ra, rb) < 0)
            {
                _parent[rb] = ra;
            }
            else
            {
                _parent[ra] = rb;
            }
        }
    }

    /// <summary>All sets with more than one member, each sorted, ordered by their first member.</summary>
    public List<List<string>> Groups() =>
        _parent.Keys
            .GroupBy(Find, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Order(StringComparer.Ordinal).ToList())
            .OrderBy(g => g[0], StringComparer.Ordinal)
            .ToList();
}
