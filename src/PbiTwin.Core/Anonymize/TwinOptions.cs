using PbiTwin.Core.Rename;

namespace PbiTwin.Core.Anonymize;

/// <summary>User-adjustable settings for one run.</summary>
public sealed record TwinOptions
{
    /// <summary>Secret seed. Random by default; set it to reproduce a twin. Never written to disk.</summary>
    public string Seed { get; init; } = SeededHash.NewSeed();

    /// <summary>The money factor is drawn (log-uniformly, from the seed) from this range.</summary>
    public double MinMoneyFactor { get; init; } = 0.37;

    public double MaxMoneyFactor { get; init; } = 2.7;

    /// <summary>Per-row multiplicative noise for money and quantity values, in percent.</summary>
    public double NoisePercent { get; init; } = 3;

    /// <summary>Fixed date shift in days; null draws a multiple of 364 days ± a few weeks from the seed.</summary>
    public int? DateShiftDays { get; init; }

    /// <summary>Maximum rows per fact table; dimension tables are always copied in full.</summary>
    public long? FactRowLimit { get; init; }

    /// <summary>Remove descriptions and non-functional annotations from the model (always on when renaming).</summary>
    public bool ClearDescriptions { get; init; } = true;

    /// <summary>Rename tables, columns, measures, hierarchies and roles; a codebook is saved next to the twin.</summary>
    public RenameMode RenameMode { get; init; } = RenameMode.None;
}

/// <summary>Numbers derived from the seed. Kept in memory only — never shown in reports.</summary>
public sealed record TwinParameters(double MoneyFactor, double QuantityFactor, int DateShiftDays, double Noise)
{
    public static TwinParameters From(TwinOptions options, SeededHash hash)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hash);

        if (options.MinMoneyFactor <= 0 || options.MaxMoneyFactor < options.MinMoneyFactor)
        {
            throw new ArgumentException("Money factor range must be positive and ordered.", nameof(options));
        }

        if (options.NoisePercent is < 0 or >= 50)
        {
            throw new ArgumentException("Noise must be between 0 and 50 percent.", nameof(options));
        }

        var money = LogUniform(options.MinMoneyFactor, options.MaxMoneyFactor, hash.Unit("param", "money"));
        var quantity = LogUniform(0.6, 1.8, hash.Unit("param", "quantity"));

        var shift = options.DateShiftDays ?? DrawShift(hash);
        return new TwinParameters(money, quantity, shift, options.NoisePercent / 100);
    }

    private static double LogUniform(double min, double max, double u) => min * Math.Pow(max / min, u);

    /// <summary>364 days is exactly 52 weeks, so weekdays are preserved; seasonality moves by at most a few weeks.</summary>
    private static int DrawShift(SeededHash hash)
    {
        int[] years = [-3, -2, -1, 1, 2, 3];
        var k = years[(int)(hash.UInt64("param", "years") % (ulong)years.Length)];
        var weeks = (int)(hash.UInt64("param", "weeks") % 9) - 4;
        return (364 * k) + (7 * weeks);
    }
}
