using DesktopHub.Core.Windowing;
using static DesktopHub.App.Platform.NativeMethods;

namespace DesktopHub.App.Platform;

/// <summary>Operações de janela em pixels físicos (independentes da escala de DPI do WPF).</summary>
internal static class WindowInterop
{
    public static void AddExtendedStyle(nint hwnd, long style)
    {
        var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)current | style));
    }

    public static PixelRect GetRect(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void MoveTo(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public static (int X, int Y) GetCursor()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Exclui (ou não) a janela de capturas de tela e gravações. Requer Windows 10 2004+.</summary>
    public static bool SetExcludeFromCapture(nint hwnd, bool exclude) =>
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    /// <summary>Barra de título escura (Windows 11 e Windows 10 20H1+).</summary>
    public static void UseDarkTitleBar(nint hwnd)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    /// <summary>Áreas de trabalho (sem a barra de tarefas) de todos os monitores, em pixels físicos.</summary>
    public static IReadOnlyList<PixelRect> GetWorkAreas() =>
        [.. System.Windows.Forms.Screen.AllScreens.Select(s =>
            new PixelRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))];

    public static PixelRect GetPrimaryWorkArea()
    {
        var wa = (System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0]).WorkingArea;
        return new PixelRect(wa.X, wa.Y, wa.Width, wa.Height);
    }
}
