using System.Windows;
using PbiTwin.App.ViewModels;

namespace PbiTwin.App.Views;

public partial class TranslateWindow : Window
{
    public TranslateWindow(string? codebookPath)
    {
        InitializeComponent();
        DataContext = new TranslateViewModel(codebookPath);
    }
}
