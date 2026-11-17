using System.Globalization;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Verify;

/// <summary>
/// Collects original-vs-twin aggregates while rows stream through, to prove that the twin keeps the shape of
/// the data. Only derived numbers (correlations, relative deviations) ever leave this class.
/// </summary>
public sealed class Metrics
{
    private readonly Dictionary<string, TableMetrics> _tables = new(StringComparer.Ordinal);

    public void Begin(TableInfo table, AnonymizationPlan plan, int dateShiftDays)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(plan);

        var money = table.Columns.Select((c, i) => (c, i)).Where(x => plan[x.c].Rule == Rule.Money).Select(x => x.i).ToArray();
        var date = table.Columns.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Type == ColumnType.DateTime && plan[x.c].Rule == Rule.DateShift);
        _tables[table.Name] = new TableMetrics(table, money, date.c is null ? -1 : date.i, dateShiftDays);
    }

    public void Add(string table, object?[] original, object?[] twin) => _tables[table].Add(original, twin);

    public IEnumerable<TableMetrics> Tables => _tables.Values;

    public sealed class TableMetrics(TableInfo table, int[] money, int dateIndex, int shift)
    {
        private readonly double[] _origTotals = new double[money.Length];
        private readonly double[] _twinTotals = new double[money.Length];
        private readonly SortedDictionary<int, (double[] Orig, double[] Twin)> _monthly = [];

        public TableInfo Table { get; } = table;

        public long Rows { get; private set; }

        public IReadOnlyList<string> MoneyColumns => money.Select(i => Table.Columns[i].Name).ToList();

        public int Months => _monthly.Count;

        /// <summary>Money columns ordered by their original total, largest first (e.g. SalesAmount, CostAmount, ...).</summary>
        private int[] ByTotal => Enumerable.Range(0, money.Length).OrderByDescending(k => Math.Abs(_origTotals[k])).ToArray();

        /// <summary>The money column with the largest total — the one a "Revenue" measure would sum.</summary>
        public string? MainColumn => money.Length == 0 ? null : Table.Columns[money[ByTotal[0]]].Name;

        internal void Add(object?[] original, object?[] twin)
        {
            Rows++;
            for (var k = 0; k < money.Length; k++)
            {
                _origTotals[k] += ToDouble(original[money[k]]);
                _twinTotals[k] += ToDouble(twin[money[k]]);
            }

            if (dateIndex >= 0 && money.Length > 0 && original[dateIndex] is DateTime od && twin[dateIndex] is DateTime td)
            {
                // Bucket the twin by its un-shifted date, so both series use the same calendar months.
                var back = PbiTwin.Core.Anonymize.DateShift.IsPlaceholder(od) ? td : td.AddDays(-shift);
                var o = Bucket((od.Year * 12) + od.Month);
                var t = Bucket((back.Year * 12) + back.Month);
                for (var k = 0; k < money.Length; k++)
                {
                    o.Orig[k] += ToDouble(original[money[k]]);
                    t.Twin[k] += ToDouble(twin[money[k]]);
                }
            }
        }

        private (double[] Orig, double[] Twin) Bucket(int key)
        {
            if (!_monthly.TryGetValue(key, out var b))
            {
                b = (new double[money.Length], new double[money.Length]);
                _monthly[key] = b;
            }

            return b;
        }

        /// <summary>Pearson correlation of monthly totals of <see cref="MainColumn"/> (1.0 = identical trend).</summary>
        public double? MonthlyTrendCorrelation()
        {
            if (_monthly.Count < 3 || money.Length == 0)
            {
                return null;
            }

            var k = ByTotal[0];
            return Pearson(_monthly.Values.Select(v => v.Orig[k]).ToArray(), _monthly.Values.Select(v => v.Twin[k]).ToArray());
        }

        /// <summary>
        /// How much the ratio between the two largest money columns moved (e.g. Cost / Sales, which drives Margin %).
        /// </summary>
        public (string A, string B, double RelativeChange)? MainRatioChange()
        {
            if (money.Length < 2)
            {
                return null;
            }

            var (a, b) = (ByTotal[0], ByTotal[1]);
            if (_origTotals[a] == 0 || _origTotals[b] == 0 || _twinTotals[b] == 0)
            {
                return null;
            }

            var change = Math.Abs(((_twinTotals[a] / _twinTotals[b]) / (_origTotals[a] / _origTotals[b])) - 1);
            return (Table.Columns[money[a]].Name, Table.Columns[money[b]].Name, change);
        }

        private static double ToDouble(object? v) => v switch
        {
            null => 0,
            long l => l,
            double d => d,
            decimal m => (double)m,
            _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        };

        private static double Pearson(double[] x, double[] y)
        {
            var mx = x.Average();
            var my = y.Average();
            double sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < x.Length; i++)
            {
                sxy += (x[i] - mx) * (y[i] - my);
                sxx += (x[i] - mx) * (x[i] - mx);
                syy += (y[i] - my) * (y[i] - my);
            }

            return sxx == 0 || syy == 0 ? 1 : sxy / Math.Sqrt(sxx * syy);
        }
    }
}
