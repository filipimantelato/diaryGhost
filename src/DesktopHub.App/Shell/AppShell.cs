using System.Windows;
using System.Windows.Forms;
using DesktopHub.App.Main;
using DesktopHub.App.Platform;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Finance;
using DesktopHub.Core.Activity;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Weather;
using DesktopHub.Core.Media;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;
using DesktopHub.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WpfApplication = System.Windows.Application;

namespace DesktopHub.App.Shell;

public static class MainPages
{
    public const string Dashboard = "dashboard";
    public const string Settings = "settings";
}

/// <summary>
/// Coordena as três superfícies (janela principal, widget, bandeja) e o ciclo de vida do processo.
/// A janela principal é criada sob demanda e destruída ao fechar: o processo continua com os serviços.
/// </summary>
internal sealed partial class AppShell(
    SettingsService settings,
    IEventBus bus,
    WidgetController widget,
    GlobalHotkeys hotkeys,
    FullscreenMonitor fullscreen,
    StartupState startup,
    AppPaths paths,
    MediaService media,
    IMasterVolume volume,
    ProductivityService productivity,
    NotificationCenter notifications,
    WindowsToastSink toasts,
    AgendaService agenda,
    ReminderScheduler reminders,
    NewsService news,
    WeatherService weather,
    ActivityService activity,
    CalendarSubscriptionService subscriptions,
    FinanceService finance,
    ILogger<AppShell> logger) : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private TrayIconService? _tray;
    private MainWindow? _main;
    private bool _exiting;
    private bool _disposed;

    public bool IsExiting => _exiting;

    private ShellServices Services => new(settings, bus, widget, hotkeys, startup, paths, media, productivity, notifications, agenda, reminders, news, weather, activity, subscriptions, finance);

    public void Start()
    {
        toasts.Register("Diary Ghost", ExtractNotificationIcon());
        notifications.PruneHistory();
        productivity.Start(); // antes do widget: ele já nasce com o estado recuperado
        agenda.Start();
        reminders.Start();    // entrega o que venceu com o app fechado e agenda o próximo aviso
        news.Start();         // só acessa a rede se o módulo estiver ativado, 20 s depois de abrir
        weather.Start();      // só acessa a rede com um local escolhido
        activity.Start();     // canal local do Claude Code, só com a integração ativada
        subscriptions.Start(); // agendas iCal assinadas

        _tray = new TrayIconService(this, Services);
        _subscriptions.Add(bus.Subscribe<OpenMainRequested>(e => OpenMain(e.Page), SynchronizationContext.Current));
        fullscreen.Start();
        widget.Start(startup.Options.NoWidget);
        volume.Start(); // COM do Core Audio fica na thread de UI
        _ = StartMediaAsync();

        // Timers .NET usam um relógio que pode não avançar durante a suspensão: reagenda ao voltar,
        // e também se o relógio/fuso do sistema mudar.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;

        hotkeys.Register(1, "Mostrar/ocultar widget", NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x48 /* H */, "Ctrl+Alt+H", widget.Toggle);
        hotkeys.Register(2, "Abrir o Diary Ghost", NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x44 /* D */, "Ctrl+Alt+D", () => OpenMain());

        SyncAutoStart();
        RegisterLinkProtocol();

        var openMain = startup.Options.ShowMain || (!startup.Options.Minimized && !settings.General.StartMinimized);
        if (openMain)
        {
            OpenMain(startup.Options.Page);
        }

        if (startup.Database?.RecoveredFromCorruption == true)
        {
            _tray.ShowBalloon("Banco de dados recuperado",
                "O arquivo de dados estava corrompido e foi preservado em 'corrupt'. Um banco novo foi criado.", ToolTipIcon.Warning);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            LogResumed();
            productivity.Reschedule();
            reminders.Reschedule();
            news.Reschedule();
            weather.Reschedule();
            subscriptions.Reschedule();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        // O fuso pode ter mudado: TimeZoneInfo.Local é guardado em cache pelo .NET.
        TimeZoneInfo.ClearCachedData();
        productivity.Reschedule();
        reminders.Reschedule();
    }

    /// <summary>O Windows exibe o ícone do toast a partir de um arquivo: grava o logo embutido na pasta de dados.</summary>
    private string ExtractNotificationIcon()
    {
        var path = Path.Combine(paths.Root, "notification-icon.png");
        try
        {
            var resource = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png"));
            using var source = resource!.Stream;
            using var target = File.Create(path);
            source.CopyTo(target);
        }
        catch (IOException ex)
        {
            LogIconFailed(ex);
        }
        return path;
    }

    private async Task StartMediaAsync()
    {
        try
        {
            await media.StartAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Mídia é opcional: o resto do app continua funcionando.
            LogMediaFailed(ex);
        }
    }

    public void OpenMain(string? page = null)
    {
        if (_exiting)
        {
            return;
        }
        if (_main is null)
        {
            _main = new MainWindow(Services);
            _main.Closed += OnMainClosed;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }
        _main.Activate();
        if (page is not null)
        {
            _main.Navigate(page);
        }
    }

    private void OnMainClosed(object? sender, EventArgs e)
    {
        _main = null;
        if (_exiting)
        {
            return;
        }
        if (settings.General.CloseBehavior == CloseBehavior.Exit)
        {
            Exit();
            return;
        }
        if (!settings.General.HasShownTrayHint)
        {
            _tray?.ShowBalloon("O Diary Ghost continua em execução",
                "Widget, atalhos e serviços seguem ativos. Use o ícone da bandeja para abrir ou sair.");
            settings.UpdateGeneral(g => g with { HasShownTrayHint = true });
        }
        LogMainClosed();
    }

    public void Exit()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        LogExiting();
        _main?.Close();
        Dispose();
        WpfApplication.Current.Shutdown();
    }

    /// <summary>
    /// Registra o protocolo <c>diaryghost:</c> apontando para este executável, para que clicar numa notificação
    /// antiga reabra o app mesmo fechado. Instâncias de teste (<c>--data-dir</c>) não mexem no registro.
    /// </summary>
    private void RegisterLinkProtocol()
    {
        if (startup.Options.DataDir is not null || Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            Infrastructure.Setup.PerUserInstaller.RegisterProtocol($@"Software\Classes\{Infrastructure.Setup.InstallLayout.Protocol}", exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LogAutoStartFailed(ex);
        }
    }

    private void SyncAutoStart()
    {
        try
        {
            // O registro é a verdade; se o app mudou de pasta, a entrada é atualizada para o novo caminho.
            if (settings.General.StartWithWindows != AutoStart.IsEnabled() || AutoStart.HasLegacyEntry())
            {
                AutoStart.Apply(settings.General.StartWithWindows);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LogAutoStartFailed(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // eventos estáticos: evitar vazamento
        SystemEvents.TimeChanged -= OnTimeChanged;
        _subscriptions.ForEach(s => s.Dispose());
        hotkeys.Dispose();
        fullscreen.Dispose();
        widget.Dispose();
        _tray?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Janela principal fechada; app segue na bandeja")]
    private partial void LogMainClosed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Encerrando o aplicativo")]
    private partial void LogExiting();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sistema retomou da suspensão; temporizadores reagendados")]
    private partial void LogResumed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível gravar o ícone das notificações")]
    private partial void LogIconFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao iniciar o controle de mídia; demais módulos seguem ativos")]
    private partial void LogMediaFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível sincronizar a inicialização com o Windows")]
    private partial void LogAutoStartFailed(Exception ex);
}
