using DesktopHub.App.Platform;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Weather;
using DesktopHub.Core.Media;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Shell;

/// <summary>Serviços usados pelas telas (janela principal, páginas, bandeja), entregues de uma vez.</summary>
internal sealed record ShellServices(
    SettingsService Settings,
    IEventBus Bus,
    WidgetController Widget,
    GlobalHotkeys Hotkeys,
    StartupState Startup,
    AppPaths Paths,
    MediaService Media,
    ProductivityService Productivity,
    NotificationCenter Notifications,
    AgendaService Agenda,
    ReminderScheduler Reminders,
    NewsService News,
    WeatherService Weather);
