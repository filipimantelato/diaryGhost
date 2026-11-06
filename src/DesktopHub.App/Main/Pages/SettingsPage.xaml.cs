using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DesktopHub.App.Main.Pages;

public partial class SettingsPage : UserControl, IDisposable
{
    private readonly SettingsViewModel _viewModel;

    internal SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            _viewModel.SetAccent(hex);
        }
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _viewModel.ResetWidgetPosition();

    private void OpenData_Click(object sender, RoutedEventArgs e) => _viewModel.OpenDataFolder();

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _viewModel.OpenLogsFolder();

    public void Dispose()
    {
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
