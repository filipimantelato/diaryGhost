using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopHub.App.Main.Pages;
using DesktopHub.App.Platform;
using DesktopHub.App.Shell;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main;

/// <summary>
/// Janela completa (Modo A). As páginas são criadas a cada navegação e descartadas ao sair delas,
/// para que nada fique assinado ou ocupando memória enquanto não é exibido.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellServices _services;
    private readonly SettingsService _settings;
    private readonly IEventBus _bus;
    private readonly WidgetController _widget;
    private readonly GlobalHotkeys _hotkeys;
    private readonly StartupState _startup;
    private readonly AppPaths _paths;
    private readonly MediaService _media;
    private readonly Dictionary<RadioButton, string> _navKeys;

    internal MainWindow(ShellServices services)
    {
        _services = services;
        (_settings, _bus, _widget, _hotkeys, _startup, _paths, _media) =
            (services.Settings, services.Bus, services.Widget, services.Hotkeys, services.Startup, services.Paths, services.Media);
        InitializeComponent();

        _navKeys = new()
        {
            [NavDashboard] = MainPages.Dashboard,
            [NavNews] = "news",
            [NavCalendar] = "calendar",
            [NavTasks] = "tasks",
            [NavFinance] = "finance",
            [NavReminders] = "reminders",
            [NavMedia] = "media",
            [NavProductivity] = "productivity",
            [NavIntegrations] = "integrations",
            [NavSettings] = MainPages.Settings,
        };

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        VersionText.Text = $"Versão {version.Split('+')[0]} · Fase 13";

        SourceInitialized += (_, _) => WindowInterop.UseDarkTitleBar(new WindowInteropHelper(this).Handle, !Theme.ThemeManager.IsLight);
        Theme.ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyTextScale();
        UpdateThemeButtons();
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.General)
            {
                ApplyTextScale();
            }
        }, SynchronizationContext.Current));
        NavDashboard.IsChecked = true;
    }

    private readonly List<IDisposable> _subscriptions = [];

    private void OnThemeChanged()
    {
        WindowInterop.UseDarkTitleBar(new WindowInteropHelper(this).Handle, !Theme.ThemeManager.IsLight);
        UpdateThemeButtons();
        // Partes montadas em código guardam o pincel do momento: a página atual é recriada com as cores novas.
        if (_navKeys.FirstOrDefault(p => p.Key.IsChecked == true).Value is { } key)
        {
            (PageHost.Content as IDisposable)?.Dispose();
            PageHost.Content = CreatePage(key);
        }
    }

    private void UpdateThemeButtons()
    {
        ThemeLight.IsChecked = Theme.ThemeManager.IsLight;
        ThemeDark.IsChecked = !Theme.ThemeManager.IsLight;
    }

    // Checked (e não Click): também cobre teclado e leitores de tela.
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        var theme = ReferenceEquals(sender, ThemeLight) ? AppTheme.Light : AppTheme.Dark;
        if (_settings.General.Theme != theme)
        {
            _settings.UpdateGeneral(g => g with { Theme = theme });
        }
    }

    /// <summary>Acessibilidade: amplia toda a janela (texto, ícones e espaçamentos juntos, sem quebrar o layout).</summary>
    private void ApplyTextScale()
    {
        var scale = _settings.General.TextScale;
        ((FrameworkElement)Content).LayoutTransform = scale > 1.01 ? new System.Windows.Media.ScaleTransform(scale, scale) : null;
        MinWidth = 820 * scale;
    }

    public void Navigate(string key)
    {
        var target = _navKeys.FirstOrDefault(p => p.Value == key).Key;
        if (target is not null)
        {
            target.IsChecked = true;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button || !_navKeys.TryGetValue(button, out var key))
        {
            return;
        }
        (PageHost.Content as IDisposable)?.Dispose();
        PageHost.Content = CreatePage(key);
    }

    private FrameworkElement CreatePage(string key) => key switch
    {
        MainPages.Dashboard => new DashboardPage(_services),
        MainPages.Settings => new SettingsPage(new SettingsViewModel(_settings, _bus, _widget, _hotkeys, _paths, _services.Notifications, _services.Agenda, _services.Weather)),
        "productivity" => new ProductivityPage(_services.Productivity, _settings, _bus),
        "news" => new NewsPage(_services.News, _settings, _bus),
        "calendar" => new CalendarPage(_services.Agenda, _bus, _services.Overlays),
        "tasks" => new TasksPage(_services.Agenda, _bus),
        "finance" => new FinancePage(_services.Finance, _bus, _services.FinanceBackup),
        "reminders" => new RemindersPage(_services.Agenda, _services.Reminders, _services.Notifications, _bus),
        "media" => new MediaPage(_media, _settings, _bus),
        "integrations" => new IntegrationsPage(_services),
        _ => new PlaceholderPage("", key, 0, "", []),
    };

    protected override void OnClosed(EventArgs e)
    {
        Theme.ThemeManager.ThemeChanged -= OnThemeChanged;
        _subscriptions.ForEach(s => s.Dispose());
        (PageHost.Content as IDisposable)?.Dispose();
        PageHost.Content = null;
        base.OnClosed(e);
    }
}
