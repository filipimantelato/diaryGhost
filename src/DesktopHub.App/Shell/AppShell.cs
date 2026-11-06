using System.Windows;
using System.Windows.Forms;
using DesktopHub.App.Main;
using DesktopHub.App.Platform;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;
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
    ILogger<AppShell> logger) : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private TrayIconService? _tray;
    private MainWindow? _main;
    private bool _exiting;
    private bool _disposed;

    public bool IsExiting => _exiting;

    public void Start()
    {
        _tray = new TrayIconService(this, widget, settings, media);
        _subscriptions.Add(bus.Subscribe<OpenMainRequested>(e => OpenMain(e.Page), SynchronizationContext.Current));
        fullscreen.Start();
        widget.Start(startup.Options.NoWidget);
        volume.Start(); // COM do Core Audio fica na thread de UI
        _ = StartMediaAsync();

        hotkeys.Register(1, "Mostrar/ocultar widget", NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x48 /* H */, "Ctrl+Alt+H", widget.Toggle);
        hotkeys.Register(2, "Abrir o Desktop Hub", NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x44 /* D */, "Ctrl+Alt+D", () => OpenMain());

        SyncAutoStart();

        var openMain = startup.Options.ShowMain || (!startup.Options.Minimized && !settings.General.StartMinimized);
        if (openMain)
        {
            OpenMain();
        }

        if (startup.Database?.RecoveredFromCorruption == true)
        {
            _tray.ShowBalloon("Banco de dados recuperado",
                "O arquivo de dados estava corrompido e foi preservado em 'corrupt'. Um banco novo foi criado.", ToolTipIcon.Warning);
        }
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
            _main = new MainWindow(this, settings, bus, widget, hotkeys, startup, paths, media);
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
            _tray?.ShowBalloon("O Desktop Hub continua em execução",
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

    private void SyncAutoStart()
    {
        try
        {
            // O registro é a verdade; se o app mudou de pasta, a entrada é atualizada para o novo caminho.
            if (settings.General.StartWithWindows != AutoStart.IsEnabled())
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao iniciar o controle de mídia; demais módulos seguem ativos")]
    private partial void LogMediaFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível sincronizar a inicialização com o Windows")]
    private partial void LogAutoStartFailed(Exception ex);
}
