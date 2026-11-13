using System.Globalization;
using PbiTwin.App.Mvvm;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Connection;
using PbiTwin.Core.Verify;

namespace PbiTwin.App.ViewModels;

public enum WizardStep
{
    Source,
    Scan,
    Options,
    Preview,
    Run,
    Done,
}

public sealed class StepItem(WizardStep step, string title) : ViewModelBase
{
    private bool _isCurrent;
    private bool _isDone;

    public WizardStep Step { get; } = step;

    public string Title { get; } = title;

    public int Number => (int)Step + 1;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }
}

public sealed record InstanceItem(DesktopInstance Instance, DesktopModel? Model, string? Error)
{
    public string Title => Model is null ? $"Port {Instance.Port}" : $"{Model.Tables.Count} tables · port {Instance.Port}";

    public string Detail => Error ?? $"Database {Model!.DatabaseName}";
}

public sealed record CheckItem(Check Check)
{
    public string Icon => Check.Status switch
    {
        CheckStatus.Pass => "✓",
        CheckStatus.Warn => "!",
        _ => "✕",
    };

    public string Name => Check.Name;

    public string Detail => Check.Detail;

    public bool IsPass => Check.Status == CheckStatus.Pass;

    public bool IsWarn => Check.Status == CheckStatus.Warn;

    public bool IsFail => Check.Status == CheckStatus.Fail;
}

/// <summary>One row of the Scan grid. Changing the rule changes it for the whole shared domain.</summary>
public sealed class ColumnRow(ColumnPlan plan, AnonymizationPlan owner, Action<Domain> domainChanged) : ViewModelBase
{
    public ColumnPlan Plan { get; } = plan;

    public string Table => Plan.Column.Table;

    public string Column => Plan.Column.Name;

    public string Type => Plan.Column.Type.ToString();

    public string Detected => Plan.Classification.Class.ToString();

    public string Confidence => Plan.Classification.Confidence.ToString();

    public string Reason => Plan.Classification.Reason;

    public IReadOnlyList<Rule> AllowedRules => Rules.Allowed(Plan.Column.Type);

    public bool IsShared => Plan.Domain.IsShared;

    public string SharedWith => IsShared
        ? "Same mapping as: " + string.Join(", ", Plan.Domain.Columns.Where(c => c != Plan.Column).Select(c => c.Id))
        : string.Empty;

    /// <summary>A masked example: enough to recognise the kind of data, not the data itself.</summary>
    public string Example => Plan.Profile is { Examples.Count: > 0 } p ? Mask(p.Examples[0]) : string.Empty;

    public Rule Rule
    {
        get => Plan.Rule;
        set
        {
            if (value != Plan.Rule)
            {
                owner.SetRule(Plan.Column.Id, value);
                domainChanged(Plan.Domain);
            }
        }
    }

    public void Refresh() => Raise(nameof(Rule));

    private static string Mask(object value)
    {
        var s = value switch
        {
            DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (value is DateTime)
        {
            return s[..4] + "-••-••";
        }

        var chars = s.ToCharArray();
        var wordStart = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetterOrDigit(chars[i]))
            {
                if (!wordStart)
                {
                    chars[i] = '•';
                }

                wordStart = false;
            }
            else
            {
                wordStart = true;
            }
        }

        return new string(chars.Take(40).ToArray());
    }
}
