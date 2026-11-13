using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PbiTwin.App.ViewModels;

namespace PbiTwin.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Show unexpected errors instead of closing the window. Nothing is logged to disk: a message could
        // contain values from the model.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Something went wrong:\n\n" + args.Exception.Message + "\n\nThe original report was not changed. You can go back and try again.",
                "PbiTwin", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}

/// <summary>Visible when the bound <see cref="WizardStep"/> equals the converter parameter.</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WizardStep step && parameter is string name && Enum.TryParse<WizardStep>(name, out var expected) && step == expected
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
