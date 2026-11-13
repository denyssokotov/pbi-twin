using System.IO;
using Microsoft.Win32;
using PbiTwin.App.Mvvm;
using PbiTwin.Core.Rename;

namespace PbiTwin.App.ViewModels;

/// <summary>Translate DAX between the twin's anonymous names and the real ones, using a codebook.</summary>
public sealed class TranslateViewModel : ViewModelBase
{
    private NameMap? _codebook;
    private string? _codebookPath;
    private string _input = string.Empty;
    private string _output = string.Empty;
    private string _status = "Load the codebook that was saved next to your twin.";

    public TranslateViewModel(string? codebookPath)
    {
        LoadCommand = new RelayCommand(Browse);
        ToRealCommand = new RelayCommand(() => Translate(toReal: true), () => _codebook is not null);
        ToAnonymousCommand = new RelayCommand(() => Translate(toReal: false), () => _codebook is not null);
        if (codebookPath is not null)
        {
            Load(codebookPath);
        }
    }

    public RelayCommand LoadCommand { get; }

    public RelayCommand ToRealCommand { get; }

    public RelayCommand ToAnonymousCommand { get; }

    public string? CodebookPath
    {
        get => _codebookPath;
        private set => Set(ref _codebookPath, value);
    }

    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    public string Output
    {
        get => _output;
        private set => Set(ref _output, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Filter = "PbiTwin codebook (*.codebook.json)|*.codebook.json", Title = "Choose the codebook" };
        if (dialog.ShowDialog() == true)
        {
            Load(dialog.FileName);
        }
    }

    private void Load(string path)
    {
        try
        {
            _codebook = NameMap.FromJson(File.ReadAllText(path));
            CodebookPath = path;
            Status = $"Codebook loaded: {_codebook.Tables.Count} tables, {_codebook.Measures.Count} measures.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _codebook = null;
            Status = "Could not read the codebook: " + ex.Message;
        }
    }

    private void Translate(bool toReal)
    {
        var unresolved = new List<string>();
        Output = toReal ? DaxTranslator.ToReal(Input, _codebook!, unresolved) : DaxTranslator.ToAnonymous(Input, _codebook!, unresolved);
        Status = unresolved.Count == 0
            ? "Translated. All references were found in the codebook."
            : "Translated, but these names were not found (variables or virtual columns are fine): " + string.Join(", ", unresolved.Distinct());
    }
}
