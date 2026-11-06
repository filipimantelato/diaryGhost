using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using static DesktopHub.App.Platform.NativeMethods;

namespace DesktopHub.App.Platform;

public sealed record HotkeyBinding(int Id, string Label, string Gesture, bool Registered);

/// <summary>
/// Atalhos globais via <c>RegisterHotKey</c>, recebidos por uma janela somente-mensagens (sem UI).
/// Se outro programa já usa a combinação, o registro falha e isso é exposto nas configurações.
/// </summary>
internal sealed partial class GlobalHotkeys : IDisposable
{
    private static readonly nint HWND_MESSAGE = -3;
    private readonly ILogger<GlobalHotkeys> _logger;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<HotkeyBinding> _bindings = [];
    private HwndSource? _source;

    public GlobalHotkeys(ILogger<GlobalHotkeys> logger) => _logger = logger;

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public void Register(int id, string label, uint modifiers, uint virtualKey, string gesture, Action action)
    {
        _source ??= CreateSource();
        var ok = RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey);
        if (ok)
        {
            _actions[id] = action;
        }
        else
        {
            LogRegisterFailed(gesture, label);
        }
        _bindings.Add(new HotkeyBinding(id, label, gesture, ok));
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("DesktopHub.Hotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        source.AddHook(WndProc);
        return source;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Atalho {Gesture} ({Label}) indisponível: já está em uso por outro programa")]
    private partial void LogRegisterFailed(string gesture, string label);
}
