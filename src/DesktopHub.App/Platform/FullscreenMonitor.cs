using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopHub.Core.Events;
using Microsoft.Extensions.Logging;
using static DesktopHub.App.Platform.NativeMethods;

namespace DesktopHub.App.Platform;

/// <summary>
/// Detecta quando o app em primeiro plano está em tela cheia (jogos, vídeo, apresentações) sem polling:
/// reage a eventos do sistema (<c>SetWinEventHook</c>) de troca de foco, fim de mover/redimensionar e
/// restauração. Uma única reavaliação adiada cobre jogos que entram em tela cheia logo após abrir.
/// </summary>
internal sealed unsafe partial class FullscreenMonitor : IDisposable
{
    private static FullscreenMonitor? s_instance;
    private readonly IEventBus _bus;
    private readonly ILogger<FullscreenMonitor> _logger;
    private readonly DispatcherTimer _delayedRecheck;
    private readonly List<nint> _hooks = [];
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public FullscreenMonitor(IEventBus bus, ILogger<FullscreenMonitor> logger)
    {
        _bus = bus;
        _logger = logger;
        _delayedRecheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _delayedRecheck.Tick += (_, _) =>
        {
            _delayedRecheck.Stop();
            Evaluate();
        };
    }

    public bool IsFullscreen { get; private set; }

    /// <summary>Deve ser chamado na thread de UI (os callbacks fora de contexto chegam pela fila de mensagens dela).</summary>
    public void Start()
    {
        if (s_instance is not null)
        {
            return;
        }
        s_instance = this;
        Hook(EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZEEND);
        Evaluate();
    }

    private void Hook(uint evt)
    {
        var hook = SetWinEventHook(evt, evt, 0, &OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == 0)
        {
            LogHookFailed(evt);
            return;
        }
        _hooks.Add(hook);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // OBJID_WINDOW = 0, CHILDID_SELF = 0: só eventos da janela, não de elementos internos.
        if (idObject != 0 || idChild != 0 || s_instance is not { } monitor)
        {
            return;
        }
        try
        {
            monitor.Evaluate();
            monitor._delayedRecheck.Stop();
            monitor._delayedRecheck.Start();
        }
        catch (Exception ex)
        {
            // Nenhuma exceção pode atravessar a fronteira nativa.
            monitor.LogEvaluateFailed(ex);
        }
    }

    private void Evaluate()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != 0)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownProcessId)
            {
                return; // nossas próprias janelas não alteram o estado
            }
        }

        var fullscreen = IsExclusiveOrPresentation() || IsWindowCoveringMonitor(hwnd);
        if (fullscreen != IsFullscreen)
        {
            IsFullscreen = fullscreen;
            _bus.Publish(new ForegroundFullscreenChanged(fullscreen));
        }
    }

    private static bool IsExclusiveOrPresentation() =>
        SHQueryUserNotificationState(out var state) == 0 &&
        state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    private static bool IsWindowCoveringMonitor(nint hwnd)
    {
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
        {
            return false;
        }
        var cls = NativeMethods.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return false;
        }

        // Janelas maximizadas com barra de título não contam (evita falso positivo com barra de tarefas oculta).
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var r))
        {
            return false;
        }
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
        {
            return false;
        }
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
        _delayedRecheck.Stop();
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }
        _hooks.Clear();
        if (ReferenceEquals(s_instance, this))
        {
            s_instance = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível registrar o hook de evento {EventId}; detecção de tela cheia parcial")]
    private partial void LogHookFailed(uint eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao avaliar a janela em primeiro plano")]
    private partial void LogEvaluateFailed(Exception ex);
}
