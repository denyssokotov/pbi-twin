using System.Globalization;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Classify;

/// <summary>Statistics gathered from a sample of rows. Lives only in memory.</summary>
public sealed class ColumnProfile
{
    public long Rows { get; private set; }

    public long Nulls { get; private set; }

    public int Distinct => _distinct.Count;

    public bool AllDistinct => Distinct == Rows - Nulls && Rows > Nulls;

    public double MaxLength { get; private set; }

    public double AvgLength => _textCount == 0 ? 0 : _lengthSum / _textCount;

    public double AvgWords => _textCount == 0 ? 0 : _wordSum / _textCount;

    public double Min { get; private set; } = double.MaxValue;

    public double Max { get; private set; } = double.MinValue;

    /// <summary>Distinct values in order of first appearance (capped).</summary>
    public IReadOnlyList<object> Examples => _examples;

    private readonly HashSet<object> _distinct = [];
    private readonly List<object> _examples = [];
    private double _lengthSum;
    private double _wordSum;
    private long _textCount;

    public void Add(object? value)
    {
        Rows++;
        if (value is null)
        {
            Nulls++;
            return;
        }

        if (_distinct.Add(value) && _examples.Count < 200)
        {
            _examples.Add(value);
        }

        switch (value)
        {
            case string s:
                _textCount++;
                _lengthSum += s.Length;
                MaxLength = Math.Max(MaxLength, s.Length);
                _wordSum += s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                break;
            case long or double or decimal:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                Min = Math.Min(Min, d);
                Max = Math.Max(Max, d);
                break;
        }
    }

    /// <summary>Share of distinct example strings that satisfy <paramref name="predicate"/>.</summary>
    public double Rate(Func<string, bool> predicate)
    {
        var texts = _examples.OfType<string>().ToList();
        return texts.Count == 0 ? 0 : texts.Count(predicate) / (double)texts.Count;
    }

    public static Dictionary<string, ColumnProfile> Build(TableInfo table, IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);

        var profiles = table.Columns.Select(_ => new ColumnProfile()).ToArray();
        foreach (var row in rows)
        {
            for (var i = 0; i < profiles.Length; i++)
            {
                profiles[i].Add(row[i]);
            }
        }

        return table.Columns.Select((c, i) => (c.Name, profiles[i])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.Ordinal);
    }
}
