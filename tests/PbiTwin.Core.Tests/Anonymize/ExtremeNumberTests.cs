using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Tests.Anonymize;

/// <summary>Numbers at the limits of their types must not stop a run.</summary>
public class ExtremeNumberTests
{
    private static (Anonymizer Anonymizer, ColumnPlan Column) Setup(DataType type, Rule rule, double moneyFactor)
    {
        var db = new Database { CompatibilityLevel = 1567, Model = new() };
        var t = new Table { Name = "T" };
        t.Columns.Add(new DataColumn { Name = "V", DataType = type, SourceColumn = "V" });
        t.Partitions.Add(new Partition { Name = "T", Source = new MPartitionSource { Expression = "x" } });
        db.Model.Tables.Add(t);
        var plan = AnonymizationPlan.Create(ModelInfo.FromModel(db.Model), new Dictionary<string, ColumnProfile>(), _ => new HashSet<object>());
        plan.SetRule("T[V]", rule);

        // The largest factor the options allow, to push values over the edge.
        var options = new TwinOptions { Seed = "EXTREME-NUMBERS-1", MinMoneyFactor = moneyFactor, MaxMoneyFactor = moneyFactor };
        var anonymizer = new Anonymizer(plan, options);
        return (anonymizer, plan["T[V]"]);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(9_000_000_000_000_000_000L)]
    public void Whole_numbers_at_the_limit_do_not_overflow(long value)
    {
        foreach (var rule in new[] { Rule.Money, Rule.Quantity })
        {
            var (anonymizer, column) = Setup(DataType.Int64, rule, 2.7);
            var result = anonymizer.Transform(column, value, anonymizer.NoiseFor("T"));
            Assert.NotNull(result);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void Doubles_at_the_limit_do_not_throw(double value)
    {
        foreach (var rule in new[] { Rule.Money, Rule.Quantity })
        {
            var (anonymizer, column) = Setup(DataType.Double, rule, 2.7);
            Assert.NotNull(anonymizer.Transform(column, value, anonymizer.NoiseFor("T")));
        }
    }

    [Fact]
    public void Decimals_at_the_limit_do_not_throw()
    {
        foreach (var rule in new[] { Rule.Money, Rule.Quantity })
        {
            foreach (var value in new[] { decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m })
            {
                var (anonymizer, column) = Setup(DataType.Decimal, rule, 2.7);
                Assert.NotNull(anonymizer.Transform(column, value, anonymizer.NoiseFor("T")));
            }
        }
    }
}
