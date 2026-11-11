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

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && Enum.TryParse<DesktopHub.Core.Settings.AppTheme>(text, out var theme))
        {
            _viewModel.SetTheme(theme);
        }
    }

    private void TextScale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            _viewModel.SetTextScale(scale);
        }
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _viewModel.ResetWidgetPosition();

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string minutesText } && int.TryParse(minutesText, out var minutes))
        {
            _viewModel.PauseNotifications(minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e) => _viewModel.ResumeNotifications();

    private void CitySearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _ = SearchCityAsync();
            e.Handled = true;
        }
    }

    private void CitySearch_Click(object sender, RoutedEventArgs e) => _ = SearchCityAsync();

    private async Task SearchCityAsync()
    {
        var query = CitySearchBox.Text.Trim();
        CityResults.Children.Clear();
        CitySearchMessage.Visibility = Visibility.Visible;
        if (query.Length < 2)
        {
            CitySearchMessage.Text = "Digite pelo menos 2 letras.";
            return;
        }
        CitySearchMessage.Text = "Buscando…";
        try
        {
            var places = await _viewModel.Weather.SearchAsync(query);
            CitySearchMessage.Text = places.Count == 0 ? "Nenhuma cidade encontrada." : "Escolha:";
            foreach (var place in places)
            {
                var button = new Button { Style = (Style)FindResource("Chip"), Content = place.DisplayName, Margin = new Thickness(0, 0, 6, 6), HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) =>
                {
                    _viewModel.Weather.SetLocation(place);
                    CityResults.Children.Clear();
                    CitySearchMessage.Text = $"Local definido: {place.DisplayName}. A previsão aparece no Dashboard e no widget.";
                    CitySearchBox.Clear();
                };
                CityResults.Children.Add(button);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CitySearchMessage.Text = "Não foi possível buscar agora (sem conexão?). Tente de novo.";
        }
    }

    private void ClearLocation_Click(object sender, RoutedEventArgs e) => _viewModel.Weather.SetLocation(null);

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AddCategory(NewCategoryBox.Text))
        {
            NewCategoryBox.Clear();
        }
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            _viewModel.DeleteCategory(id);
        }
    }

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
