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
            [NavReminders] = "reminders",
            [NavMedia] = "media",
            [NavProductivity] = "productivity",
            [NavIntegrations] = "integrations",
            [NavSettings] = MainPages.Settings,
        };

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        VersionText.Text = $"Versão {version.Split('+')[0]} · Fase 5";

        SourceInitialized += (_, _) => WindowInterop.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        NavDashboard.IsChecked = true;
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
        "calendar" => new CalendarPage(_services.Agenda, _bus),
        "tasks" => new TasksPage(_services.Agenda, _bus),
        "reminders" => new RemindersPage(_services.Agenda, _services.Reminders, _services.Notifications, _bus),
        "media" => new MediaPage(_media, _settings, _bus),
        "integrations" => new PlaceholderPage("", "Atividades e integrações", 6,
            "Eventos autorizados de ferramentas. Para o Claude Code, via hooks oficiais que chamam um pequeno executável local; conversas privadas nunca são lidas.",
            ["Canal local (named pipe restrito ao seu usuário) com contrato JSON versionado", "Claude Code: tarefa concluída, aguardando confirmação, falha", "Estado de cada integração e último erro"]),
        _ => new PlaceholderPage("", key, 0, "", []),
    };

    protected override void OnClosed(EventArgs e)
    {
        (PageHost.Content as IDisposable)?.Dispose();
        PageHost.Content = null;
        base.OnClosed(e);
    }
}
