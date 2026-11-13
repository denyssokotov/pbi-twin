using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Tests.Anonymize;

public class AnonymizerTests
{
    private static readonly ColumnInfo Key = new("Dim", "Key", ColumnType.Int64, "Key", true, false, null, false);
    private static readonly ColumnInfo FactKey = new("Fact", "Key", ColumnType.Int64, "Key", false, false, null, false);
    private static readonly ColumnInfo Code = new("Fact", "OrderNumber", ColumnType.Text, "OrderNumber", false, false, null, false);
    private static readonly ColumnInfo Name = new("Dim", "FullName", ColumnType.Text, "FullName", false, false, null, false);
    private static readonly ColumnInfo City = new("Dim", "City", ColumnType.Text, "City", false, false, "City", false);
    private static readonly ColumnInfo Amount = new("Fact", "Amount", ColumnType.Decimal, "Amount", false, false, null, false);
    private static readonly ColumnInfo Qty = new("Fact", "Quantity", ColumnType.Int64, "Quantity", false, false, null, false);
    private static readonly ColumnInfo When = new("Fact", "OrderDate", ColumnType.DateTime, "OrderDate", false, false, null, true);

    private static (AnonymizationPlan Plan, Anonymizer A) Build(string seed = "seed-1", double noise = 3)
    {
        var plan = TestPlans.Create(
            [Key, Name, City],
            [FactKey, Code, Amount, Qty, When],
            relationships: [("Fact", "Key", "Dim", "Key")],
            samples: new()
            {
                [Name.Id] = ["Olena Kovalenko", "Taras Melnyk"],
                [City.Id] = ["Kyiv", "Lviv"],
                [Code.Id] = Enumerable.Range(1, 60).Select(i => (object?)$"SO-2024-{i:000000}").ToArray(),
            });

        var a = new Anonymizer(plan, new TwinOptions { Seed = seed, NoisePercent = noise });
        a.BuildDictionary(plan[Key].Domain, Enumerable.Range(1, 100).Select(i => (object)(long)i));
        a.BuildDictionary(plan[City].Domain, ["Kyiv", "Lviv", "Odesa"]);
        a.BuildDictionary(plan[Code].Domain, Enumerable.Range(1, 60).Select(i => (object)$"SO-2024-{i:000000}"));
        return (plan, a);
    }

    [Fact]
    public void Keys_are_bijective_and_shared_across_the_relationship()
    {
        var (plan, a) = Build();
        var rng = a.NoiseFor("t");

        var mapped = Enumerable.Range(1, 100).Select(i => a.Transform(plan[Key], (long)i, rng)).ToList();
        Assert.Equal(100, mapped.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i).Order(), mapped.Cast<long>().Order());
        Assert.NotEqual(Enumerable.Range(1, 100).Select(i => (object)(long)i), mapped);

        for (var i = 1; i <= 100; i++)
        {
            Assert.Equal(a.Transform(plan[Key], (long)i, rng), a.Transform(plan[FactKey], (long)i, rng));
        }
    }

    [Fact]
    public void Text_keys_get_prefixed_unique_codes()
    {
        var (plan, a) = Build();
        var mapped = Enumerable.Range(1, 60).Select(i => (string)a.Transform(plan[Code], $"SO-2024-{i:000000}", a.NoiseFor("t"))!).ToList();

        Assert.Equal(60, mapped.Distinct().Count());
        Assert.All(mapped, m => Assert.Matches("^ON-0000\\d\\d$", m));
    }

    [Fact]
    public void Same_seed_is_deterministic_and_another_seed_differs()
    {
        var (p1, a1) = Build("alpha");
        var (p2, a2) = Build("alpha");
        var (p3, a3) = Build("beta");

        Assert.Equal(a1.Transform(p1[Name], "Olena Kovalenko", a1.NoiseFor("t")), a2.Transform(p2[Name], "Olena Kovalenko", a2.NoiseFor("t")));
        Assert.Equal(a1.Transform(p1[Key], 7L, a1.NoiseFor("t")), a2.Transform(p2[Key], 7L, a2.NoiseFor("t")));
        Assert.Equal(a1.Transform(p1[Amount], 100m, a1.NoiseFor("t")), a2.Transform(p2[Amount], 100m, a2.NoiseFor("t")));

        var differs = Enumerable.Range(1, 20).Any(i => !Equals(a1.Transform(p1[Key], (long)i, a1.NoiseFor("t")), a3.Transform(p3[Key], (long)i, a3.NoiseFor("t"))));
        Assert.True(differs);
    }

    [Fact]
    public void Person_names_are_consistent_and_fake()
    {
        var (plan, a) = Build();
        var x = (string)a.Transform(plan[Name], "Olena Kovalenko", a.NoiseFor("t"))!;
        var y = (string)a.Transform(plan[Name], "Olena Kovalenko", a.NoiseFor("u"))!;

        Assert.Equal(x, y);
        Assert.NotEqual("Olena Kovalenko", x);
        Assert.Matches("^[A-Z][a-z]+ [A-Z][a-z]+$", x);
        Assert.Contains(a.KnownText, kv => kv.Key == "Olena Kovalenko");
    }

    [Fact]
    public void Money_scales_by_one_shared_factor_and_keeps_zero_and_sign()
    {
        var (plan, a) = Build(noise: 0);
        var rng = a.NoiseFor("t");

        Assert.Equal(0m, a.Transform(plan[Amount], 0m, rng));
        var pos = (decimal)a.Transform(plan[Amount], 100m, rng)!;
        var neg = (decimal)a.Transform(plan[Amount], -100m, rng)!;
        var big = (decimal)a.Transform(plan[Amount], 1000m, rng)!;

        Assert.True(pos > 0);
        Assert.Equal(-pos, neg);
        Assert.Equal(pos * 10, big, 1);
        Assert.InRange((double)pos / 100, 0.37, 2.7);
    }

    [Fact]
    public void Noise_stays_within_the_configured_percentage()
    {
        var (plan, a) = Build(noise: 3);
        var (plan0, a0) = Build(noise: 0);
        var factor = (double)(decimal)a0.Transform(plan0[Amount], 1000m, a0.NoiseFor("t"))! / 1000;
        var rng = a.NoiseFor("t");

        for (var i = 0; i < 500; i++)
        {
            var v = (double)(decimal)a.Transform(plan[Amount], 1000m, rng)! / 1000;
            Assert.InRange(v / factor, 0.969, 1.031);
        }
    }

    [Fact]
    public void Quantities_stay_whole_and_non_zero()
    {
        var (plan, a) = Build();
        var rng = a.NoiseFor("t");
        for (long q = 1; q <= 20; q++)
        {
            var v = (long)a.Transform(plan[Qty], q, rng)!;
            Assert.True(v >= 1);
        }

        Assert.Equal(0L, a.Transform(plan[Qty], 0L, rng));
    }

    [Fact]
    public void Dates_shift_by_whole_weeks_keeping_the_weekday()
    {
        var (plan, a) = Build();
        var d = new DateTime(2024, 11, 29);
        var shifted = (DateTime)a.Transform(plan[When], d, a.NoiseFor("t"))!;

        Assert.NotEqual(d, shifted);
        Assert.Equal(d.DayOfWeek, shifted.DayOfWeek);
        Assert.InRange(Math.Abs((shifted - d).TotalDays), 364 - 28, (3 * 364) + 28);
    }

    [Fact]
    public void Nulls_stay_null()
    {
        var (plan, a) = Build();
        foreach (var c in new[] { Key, Name, City, Amount, Qty, When, Code })
        {
            Assert.Null(a.Transform(plan[c], null, a.NoiseFor("t")));
        }
    }

    [Fact]
    public void Cities_map_one_to_one_to_invented_names()
    {
        var (plan, a) = Build();
        var mapped = new[] { "Kyiv", "Lviv", "Odesa" }.Select(c => (string)a.Transform(plan[City], c, a.NoiseFor("t"))!).ToList();

        Assert.Equal(3, mapped.Distinct().Count());
        Assert.DoesNotContain("Kyiv", mapped);
    }

    [Fact]
    public void Seeds_are_random_and_readable()
    {
        var s1 = SeededHash.NewSeed();
        var s2 = SeededHash.NewSeed();
        Assert.NotEqual(s1, s2);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", s1);
    }
}

/// <summary>Builds plans from in-memory column lists for unit tests.</summary>
internal static class TestPlans
{
    public static AnonymizationPlan Create(
        ColumnInfo[] dim,
        ColumnInfo[] fact,
        (string FromTable, string FromColumn, string ToTable, string ToColumn)[] relationships,
        Dictionary<string, object?[]> samples)
    {
        var db = new Microsoft.AnalysisServices.Tabular.Database { CompatibilityLevel = 1567, Model = new() };
        AddTable(db.Model, dim);
        AddTable(db.Model, fact);
        foreach (var r in relationships)
        {
            db.Model.Relationships.Add(new Microsoft.AnalysisServices.Tabular.SingleColumnRelationship
            {
                FromColumn = db.Model.Tables[r.FromTable].Columns[r.FromColumn],
                ToColumn = db.Model.Tables[r.ToTable].Columns[r.ToColumn],
            });
        }

        var model = ModelInfo.FromModel(db.Model);
        var profiles = new Dictionary<string, ColumnProfile>();
        foreach (var (id, values) in samples)
        {
            var p = new ColumnProfile();
            foreach (var v in values)
            {
                p.Add(v);
            }

            profiles[id] = p;
        }

        return AnonymizationPlan.Create(model, profiles);
    }

    private static void AddTable(Microsoft.AnalysisServices.Tabular.Model model, ColumnInfo[] columns)
    {
        var t = new Microsoft.AnalysisServices.Tabular.Table { Name = columns[0].Table };
        foreach (var c in columns)
        {
            t.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn
            {
                Name = c.Name,
                SourceColumn = c.Name,
                IsKey = c.IsKey,
                DataCategory = c.DataCategory,
                FormatString = c.IsDateOnly ? "yyyy-mm-dd" : null,
                DataType = c.Type switch
                {
                    ColumnType.Int64 => Microsoft.AnalysisServices.Tabular.DataType.Int64,
                    ColumnType.Decimal => Microsoft.AnalysisServices.Tabular.DataType.Decimal,
                    ColumnType.Double => Microsoft.AnalysisServices.Tabular.DataType.Double,
                    ColumnType.DateTime => Microsoft.AnalysisServices.Tabular.DataType.DateTime,
                    ColumnType.Boolean => Microsoft.AnalysisServices.Tabular.DataType.Boolean,
                    _ => Microsoft.AnalysisServices.Tabular.DataType.String,
                },
            });
        }

        t.Partitions.Add(new Microsoft.AnalysisServices.Tabular.Partition
        {
            Name = t.Name,
            Source = new Microsoft.AnalysisServices.Tabular.MPartitionSource { Expression = "let x = 1 in x" },
        });
        model.Tables.Add(t);
    }
}
