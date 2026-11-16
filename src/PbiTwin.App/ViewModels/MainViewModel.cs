using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using Microsoft.Win32;
using PbiTwin.App.Mvvm;
using PbiTwin.Core;
using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Connection;
using PbiTwin.Core.Extract;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;
using PbiTwin.Core.Rename;
using PbiTwin.Core.Verify;
using Rule = PbiTwin.Core.Anonymize.Rule;

namespace PbiTwin.App.ViewModels;

/// <summary>
/// State of the wizard. Contains no anonymization logic: every decision is made by PbiTwin.Core.
/// Original values are only held in memory (scan profile and preview) and never written anywhere.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private WizardStep _current;
    private bool _isBusy;
    private string? _error;

    private InstanceItem? _selectedInstance;
    private string? _pbipPath;
    private ModelInfo? _projectModel;
    private string _matchStatus = "Choose a running model and its PBIP project.";
    private bool _isMatched;

    private AdomdTableReader? _reader;
    private TwinSession? _session;
    private AnonymizationPlan? _plan;

    private string _seed = SeededHash.NewSeed();
    private double _minFactor = 0.37;
    private double _maxFactor = 2.7;
    private double _noisePercent = 3;
    private string _dateShiftText = string.Empty;
    private string _rowLimitText = string.Empty;
    private bool _clearDescriptions = true;
    private string _outputFolder = string.Empty;
    private string _twinName = string.Empty;
    private RenameMode _renameMode = RenameMode.None;
    private string _columnFilter = string.Empty;
    private string _rulesStatus = string.Empty;

    private IReadOnlyList<PreviewTable> _preview = [];
    private string? _selectedPreviewTable;
    private DataView? _previewBefore;
    private DataView? _previewAfter;

    private double _progress;
    private string _progressText = string.Empty;
    private CancellationTokenSource? _cancel;

    private TwinResult? _result;

    public MainViewModel()
    {
        Steps =
        [
            new(WizardStep.Source, "Source"),
            new(WizardStep.Scan, "Scan"),
            new(WizardStep.Options, "Options"),
            new(WizardStep.Preview, "Preview"),
            new(WizardStep.Run, "Run"),
            new(WizardStep.Done, "Done"),
        ];
        Steps[0].IsCurrent = true;

        NextCommand = new RelayCommand(async () => await NextAsync(), CanGoNext);
        BackCommand = new RelayCommand(Back, () => !IsBusy && Current is > WizardStep.Source and not WizardStep.Run);
        RefreshInstancesCommand = new RelayCommand(async () => await RefreshInstancesAsync(), () => !IsBusy);
        BrowsePbipCommand = new RelayCommand(BrowsePbip, () => !IsBusy);
        NewSeedCommand = new RelayCommand(() => Seed = SeededHash.NewSeed());
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy && Current == WizardStep.Run);
        OpenFolderCommand = new RelayCommand(() => Open("explorer.exe", _result!.Folder.Root), () => _result is not null);
        OpenTwinCommand = new RelayCommand(() => OpenFile(_result!.Folder.PbipFile), () => _result is not null);
        OpenReportCommand = new RelayCommand(() => OpenFile(_result!.Folder.TrustReportFile), () => _result is not null);
        StartOverCommand = new RelayCommand(() => GoTo(WizardStep.Options), () => Current == WizardStep.Done);
        TranslateCommand = new RelayCommand(() => OpenTranslator?.Invoke(_result?.CodebookFile));
        SaveRulesCommand = new RelayCommand(SaveRules, () => _plan is not null && !IsBusy);
        LoadRulesCommand = new RelayCommand(LoadRules, () => _plan is not null && !IsBusy);
        ColumnsView = CollectionViewSource.GetDefaultView(Columns);
        ColumnsView.Filter = o => o is ColumnRow r && MatchesFilter(r);
    }

    public IReadOnlyList<StepItem> Steps { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand RefreshInstancesCommand { get; }

    public RelayCommand BrowsePbipCommand { get; }

    public RelayCommand NewSeedCommand { get; }

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenTwinCommand { get; }

    public RelayCommand OpenReportCommand { get; }

    public RelayCommand StartOverCommand { get; }

    public RelayCommand TranslateCommand { get; }

    public RelayCommand SaveRulesCommand { get; }

    public RelayCommand LoadRulesCommand { get; }

    /// <summary>Set by the window: opens the Translate DAX tool, optionally with a codebook.</summary>
    public Action<string?>? OpenTranslator { get; set; }

    public WizardStep Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                foreach (var s in Steps)
                {
                    s.IsCurrent = s.Step == value;
                    s.IsDone = s.Step < value;
                }

                Raise(nameof(NextText));
            }
        }
    }

    public string NextText => Current switch
    {
        WizardStep.Source => "Scan model",
        WizardStep.Preview => "Create twin",
        _ => "Next",
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            Set(ref _error, value);
            Raise(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    // ---------- Source ----------
    public ObservableCollection<InstanceItem> Instances { get; } = [];

    public InstanceItem? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            Set(ref _selectedInstance, value);
            UpdateMatch();
        }
    }

    public string? PbipPath
    {
        get => _pbipPath;
        private set => Set(ref _pbipPath, value);
    }

    public string MatchStatus
    {
        get => _matchStatus;
        private set => Set(ref _matchStatus, value);
    }

    public bool IsMatched
    {
        get => _isMatched;
        private set => Set(ref _isMatched, value);
    }

    // ---------- Scan ----------
    public ObservableCollection<ColumnRow> Columns { get; } = [];

    /// <summary>The Scan grid, filtered by <see cref="ColumnFilter"/>.</summary>
    public ICollectionView ColumnsView { get; }

    public string ColumnFilter
    {
        get => _columnFilter;
        set
        {
            if (Set(ref _columnFilter, value))
            {
                ColumnsView.Refresh();
            }
        }
    }

    public string RulesStatus
    {
        get => _rulesStatus;
        private set => Set(ref _rulesStatus, value);
    }

    public string ScanSummary => _plan is null ? string.Empty
        : $"{Columns.Select(c => c.Table).Distinct().Count()} tables · {Columns.Count} columns · {Columns.Count(c => c.Rule != Rule.Keep)} will be anonymized";

    // ---------- Options ----------
    public string Seed
    {
        get => _seed;
        set => Set(ref _seed, value.Trim());
    }

    public double MinFactor
    {
        get => _minFactor;
        set => Set(ref _minFactor, value);
    }

    public double MaxFactor
    {
        get => _maxFactor;
        set => Set(ref _maxFactor, value);
    }

    public double NoisePercent
    {
        get => _noisePercent;
        set => Set(ref _noisePercent, value);
    }

    /// <summary>Empty = automatic (a multiple of 364 days ± a few weeks).</summary>
    public string DateShiftText
    {
        get => _dateShiftText;
        set => Set(ref _dateShiftText, value);
    }

    /// <summary>Empty = all rows.</summary>
    public string RowLimitText
    {
        get => _rowLimitText;
        set => Set(ref _rowLimitText, value);
    }

    public bool ClearDescriptions
    {
        get => _clearDescriptions;
        set => Set(ref _clearDescriptions, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => Set(ref _outputFolder, value);
    }

    public IReadOnlyList<RenameMode> RenameModes { get; } = [RenameMode.None, RenameMode.Neutral, RenameMode.Semantic];

    public RenameMode RenameMode
    {
        get => _renameMode;
        set
        {
            if (Set(ref _renameMode, value) && value != RenameMode.None && _session is not null && TwinName == _session.Project.Name + "_Twin")
            {
                // The project name may itself be sensitive.
                TwinName = "Twin";
                OutputFolder = Path.Combine(Path.GetDirectoryName(OutputFolder) ?? OutputFolder, TwinName);
            }
        }
    }

    public string TwinName
    {
        get => _twinName;
        set => Set(ref _twinName, value);
    }

    // ---------- Preview ----------
    public IEnumerable<string> PreviewTables => _preview.Select(p => p.Table);

    public string? SelectedPreviewTable
    {
        get => _selectedPreviewTable;
        set
        {
            if (Set(ref _selectedPreviewTable, value))
            {
                var p = _preview.FirstOrDefault(t => t.Table == value);
                PreviewBefore = p is null ? null : ToView(p, p.Before);
                PreviewAfter = p is null ? null : ToView(p, p.After);
            }
        }
    }

    public DataView? PreviewBefore
    {
        get => _previewBefore;
        private set => Set(ref _previewBefore, value);
    }

    public DataView? PreviewAfter
    {
        get => _previewAfter;
        private set => Set(ref _previewAfter, value);
    }

    // ---------- Run ----------
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public ObservableCollection<string> Log { get; } = [];

    // ---------- Done ----------
    public ObservableCollection<CheckItem> Checks { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public string ResultHeadline => _result is null ? string.Empty
        : _result.Report.Passed ? "Your twin is ready." : "The twin was created, but some checks failed. Review before sharing.";

    public bool ResultPassed => _result?.Report.Passed ?? false;

    public string? OutputPath => _result?.Folder.Root;

    public string? CodebookPath => _result?.CodebookFile;

    public bool HasCodebook => _result?.CodebookFile is not null;

    // ---------- Navigation ----------
    public async Task InitializeAsync() => await RefreshInstancesAsync();

    private bool CanGoNext() => !IsBusy && Current switch
    {
        WizardStep.Source => SelectedInstance?.Model is not null && _projectModel is not null,
        WizardStep.Scan or WizardStep.Options or WizardStep.Preview => _plan is not null,
        _ => false,
    };

    private async Task NextAsync()
    {
        Error = null;
        try
        {
            switch (Current)
            {
                case WizardStep.Source:
                    await ScanAsync();
                    GoTo(WizardStep.Scan);
                    break;
                case WizardStep.Scan:
                    if (string.IsNullOrEmpty(OutputFolder))
                    {
                        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        TwinName = _session!.Project.Name + "_Twin";
                        OutputFolder = Path.Combine(docs, "PbiTwin", TwinName);
                    }

                    GoTo(WizardStep.Options);
                    break;
                case WizardStep.Options:
                    // Fail here, not after the preview: these checks need no data.
                    if (!string.IsNullOrWhiteSpace(TwinName))
                    {
                        TwinFolder.ValidateName(TwinName.Trim());
                    }

                    if (string.IsNullOrWhiteSpace(OutputFolder))
                    {
                        throw new ArgumentException("Choose an output folder.");
                    }

                    TwinFolder.EnsureSeparate(OutputFolder, _session!.Project.RootFolder);
                    var options = BuildOptions();
                    _preview = await Task.Run(() => _session!.Preview(_plan!, options));
                    Raise(nameof(PreviewTables));
                    SelectedPreviewTable = _preview.Count > 0 ? _preview[0].Table : null;
                    GoTo(WizardStep.Preview);
                    break;
                case WizardStep.Preview:
                    GoTo(WizardStep.Run);
                    await RunAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (Current == WizardStep.Run)
            {
                GoTo(WizardStep.Preview);
            }

            // After GoTo, which clears the previous error.
            Error = ex is OutOfMemoryException
                ? "Not enough memory. Every distinct text value is kept in memory for the one-to-one mapping (about 0.5 GB per million distinct identifiers). Go back to Options, set a 'Row limit for fact tables', close other programs and try again."
                : Describe(ex);
        }
    }

    private void Back() => GoTo(Current == WizardStep.Done ? WizardStep.Options : Current - 1);

    /// <summary>The message plus inner causes (ADOMD and TOM wrap the useful part). Messages can contain model values, so this is shown, never written to disk.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (!parts.Contains(e.Message))
            {
                parts.Add(e.Message);
            }

            if (e.InnerException is null)
            {
                break;
            }
        }

        // Type and the first stack frames (method names only) tell where it failed.
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        var frames = (inner.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(6)
            .Select(f => f.Trim());
        return string.Join(" → ", parts) + $"\n[{inner.GetType().Name}]\n" + string.Join("\n", frames);
    }

    private void GoTo(WizardStep step)
    {
        Error = null;
        Current = step;
    }

    // ---------- Actions ----------
    private async Task RefreshInstancesAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            List<InstanceItem> items;
            try
            {
                items = await Task.Run(() => DesktopInstances.Find().Select(Describe).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = "Could not look for Power BI Desktop: " + ex.Message;
                return;
            }

            Instances.Clear();
            foreach (var i in items)
            {
                Instances.Add(i);
            }

            SelectedInstance = Instances.FirstOrDefault(i => i.Model is not null);
            if (Instances.Count == 0)
            {
                MatchStatus = "No running Power BI Desktop found. Open your .pbip in Power BI Desktop, then refresh.";
            }
        }
        finally
        {
            IsBusy = false;
        }

        static InstanceItem Describe(DesktopInstance i)
        {
            try
            {
                return new InstanceItem(i, DesktopInstances.Describe(i), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new InstanceItem(i, null, "Not reachable: " + ex.Message);
            }
        }
    }

    private void BrowsePbip()
    {
        var dialog = new OpenFileDialog { Filter = "Power BI project (*.pbip)|*.pbip", Title = "Choose the PBIP of the open report" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var project = PbipProject.Open(dialog.FileName);
            _projectModel = ModelInfo.FromModel(project.LoadDatabase().Model);
            PbipPath = project.PbipFile;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _projectModel = null;
            PbipPath = null;
            Error = "Could not read the project: " + ex.Message;
        }

        UpdateMatch();
    }

    private void UpdateMatch()
    {
        if (SelectedInstance?.Model is not { } model || _projectModel is null)
        {
            IsMatched = false;
            return;
        }

        var (missingInInstance, missingInProject) = TwinSession.Compare(_projectModel, model.Tables);
        IsMatched = missingInInstance.Count == 0 && missingInProject.Count == 0;
        MatchStatus = IsMatched
            ? $"Matched ✓  The project and the running model have the same {model.Tables.Count} tables."
            : "Not matched: " + string.Join("; ", new[]
            {
                missingInInstance.Count > 0 ? "missing in Power BI Desktop: " + string.Join(", ", missingInInstance) : null,
                missingInProject.Count > 0 ? "missing in the project: " + string.Join(", ", missingInProject) : null,
            }.Where(s => s is not null)) + ". Save the report in Power BI Desktop or pick another instance.";
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var instance = SelectedInstance!;
            _reader?.Dispose();
            _reader = new AdomdTableReader(LocalhostGuard.BuildConnectionString(instance.Instance.Port, instance.Model!.DatabaseId));
            var reader = _reader;
            var path = PbipPath!;
            var progress = new Progress<TwinProgress>(p => ProgressText = $"Scanning {p.Table}…");
            (_session, _plan) = await Task.Run(() =>
            {
                var session = new TwinSession(PbipProject.Open(path), reader);
                return (session, session.Scan(progress: progress));
            });

            Columns.Clear();
            foreach (var p in _plan.Columns.OrderBy(p => p.Column.Table, StringComparer.Ordinal))
            {
                Columns.Add(new ColumnRow(p, _plan, DomainChanged, Unlink));
            }

            Raise(nameof(ScanSummary));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool MatchesFilter(ColumnRow r)
    {
        var f = ColumnFilter.Trim();
        return f.Length == 0
            || r.Table.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Column.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Detected.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Rule.ToString().Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveRules()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PbiTwin rules (*.pbitwin-rules.json)|*.pbitwin-rules.json",
            FileName = (_session?.Project.Name ?? "model") + ".pbitwin-rules.json",
            Title = "Save rules (column names and rules only, no data)",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, RulesFile.Save(_plan!));
            RulesStatus = "Rules saved to " + dialog.FileName;
        }
    }

    private void LoadRules()
    {
        var dialog = new OpenFileDialog { Filter = "PbiTwin rules (*.pbitwin-rules.json)|*.pbitwin-rules.json", Title = "Load rules" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var result = RulesFile.Apply(_plan!, File.ReadAllText(dialog.FileName));
            foreach (var row in Columns)
            {
                row.Refresh();
            }

            Raise(nameof(ScanSummary));
            RulesStatus = $"{result.Applied} rules applied."
                + (result.NotInFile.Count > 0 ? $" {result.NotInFile.Count} new columns keep their detected rule." : string.Empty)
                + (result.NotInModel.Count > 0 ? $" {result.NotInModel.Count} columns from the file are no longer in the model." : string.Empty)
                + (result.Rejected.Count > 0 ? $" Rejected: {string.Join(", ", result.Rejected)}." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            Error = "Could not load the rules: " + ex.Message;
        }
    }

    private void Unlink(ColumnRow row)
    {
        var before = row.Plan.Domain;
        _plan!.Unlink(row.Plan.Column.Id);
        DomainChanged(before);
        row.Refresh();
    }

    private void DomainChanged(Domain domain)
    {
        foreach (var row in Columns.Where(r => r.Plan.Domain == domain))
        {
            row.Refresh();
        }

        Raise(nameof(ScanSummary));
    }

    private TwinOptions BuildOptions()
    {
        int? shift = string.IsNullOrWhiteSpace(DateShiftText) ? null
            : int.TryParse(DateShiftText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var d) ? d
            : throw new FormatException("Date shift must be a whole number of days, or empty for automatic.");
        long? limit = string.IsNullOrWhiteSpace(RowLimitText) ? null
            : long.TryParse(RowLimitText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var l) && l > 0 ? l
            : throw new FormatException("Row limit must be a positive whole number, or empty for all rows.");
        if (string.IsNullOrWhiteSpace(Seed))
        {
            throw new FormatException("The seed cannot be empty.");
        }

        return new TwinOptions
        {
            Seed = Seed,
            MinMoneyFactor = MinFactor,
            MaxMoneyFactor = MaxFactor,
            NoisePercent = NoisePercent,
            DateShiftDays = shift,
            FactRowLimit = limit,
            ClearDescriptions = ClearDescriptions,
            RenameMode = RenameMode,
        };
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where the twin is created" };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = Path.Combine(dialog.FolderName, string.IsNullOrWhiteSpace(TwinName) ? "Twin" : TwinName);
        }
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        Log.Clear();
        Progress = 0;
        _cancel = new CancellationTokenSource();
        var options = BuildOptions();
        var lastStep = string.Empty;
        var progress = new Progress<TwinProgress>(p =>
        {
            Progress = p.Percent;
            ProgressText = p.Table is null ? p.Step : $"{p.Step}: {p.Table}";
            var line = p.Table is null ? p.Step : $"{p.Step}: {p.Table}";
            if (line != lastStep)
            {
                Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
                lastStep = line;
            }
        });

        try
        {
            var name = string.IsNullOrWhiteSpace(TwinName) ? null : TwinName.Trim();
            var output = OutputFolder;
            var token = _cancel.Token;
            _result = await Task.Run(() => _session!.Run(_plan!, options, output, name, progress, token));

            Checks.Clear();
            foreach (var c in _result.Report.Checks)
            {
                Checks.Add(new CheckItem(c));
            }

            Warnings.Clear();
            foreach (var w in TrustReport.Condense(_result.Report.Warnings, 12))
            {
                Warnings.Add(w);
            }

            Raise(nameof(ResultHeadline));
            Raise(nameof(ResultPassed));
            Raise(nameof(OutputPath));
            Raise(nameof(CodebookPath));
            Raise(nameof(HasCodebook));
            GoTo(WizardStep.Done);
        }
        catch (OperationCanceledException)
        {
            Log.Add("Cancelled.");
            GoTo(WizardStep.Preview);
        }
        finally
        {
            IsBusy = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    private static DataView ToView(PreviewTable table, IReadOnlyList<object?[]> rows)
    {
        var dt = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var c in table.Columns)
        {
            dt.Columns.Add(c.Name, typeof(string));
        }

        foreach (var r in rows)
        {
            dt.Rows.Add(r.Select(v => v switch
            {
                null => string.Empty,
                DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            }).ToArray<object?>());
        }

        return dt.DefaultView;
    }

    private static void Open(string exe, string argument) => Process.Start(new ProcessStartInfo(exe) { ArgumentList = { argument } });

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public void Dispose()
    {
        _reader?.Dispose();
        _cancel?.Dispose();
    }
}
