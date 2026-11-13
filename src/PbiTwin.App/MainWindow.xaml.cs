using System.Windows;
using PbiTwin.App.ViewModels;

namespace PbiTwin.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        vm.OpenTranslator = codebook => new Views.TranslateWindow(codebook) { Owner = this }.Show();
        Loaded += async (_, _) => await vm.InitializeAsync();
        Closed += (_, _) => vm.Dispose();
    }
}
