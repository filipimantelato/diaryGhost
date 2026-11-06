namespace DesktopHub.Core.Windowing;

/// <summary>Retângulo em pixels físicos (coordenadas da área de trabalho virtual).</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public long IntersectionArea(PixelRect other)
    {
        long w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        long h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public PixelRect WithPosition(int left, int top) => this with { Left = left, Top = top };
}

/// <summary>
/// Regras puras de posicionamento, independentes do Win32 para poderem ser testadas.
/// Cobrem monitores removidos, mudanças de resolução e janelas fora da tela.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Garante que a janela fique inteira dentro de alguma área de trabalho. Escolhe a área com maior
    /// interseção; sem interseção, a mais próxima pelo centro. Se a janela for maior que a área,
    /// alinha pelo canto superior esquerdo.
    /// </summary>
    public static PixelRect EnsureVisible(PixelRect window, IReadOnlyList<PixelRect> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            return window;
        }

        var target = workAreas[0];
        long bestArea = -1;
        foreach (var area in workAreas)
        {
            var overlap = window.IntersectionArea(area);
            if (overlap > bestArea)
            {
                bestArea = overlap;
                target = area;
            }
        }

        if (bestArea <= 0)
        {
            target = workAreas.MinBy(a => DistanceSquared(Center(a), Center(window)));
        }

        return ClampInto(window, target);
    }

    /// <summary>Posição padrão: centralizado no topo da área de trabalho, com margem.</summary>
    public static PixelRect TopCenter(PixelRect workArea, int width, int height, int margin)
    {
        var left = workArea.Left + ((workArea.Width - width) / 2);
        return ClampInto(new PixelRect(left, workArea.Top + margin, width, height), workArea);
    }

    private static PixelRect ClampInto(PixelRect window, PixelRect area)
    {
        var left = window.Width >= area.Width ? area.Left : Math.Clamp(window.Left, area.Left, area.Right - window.Width);
        var top = window.Height >= area.Height ? area.Top : Math.Clamp(window.Top, area.Top, area.Bottom - window.Height);
        return window.WithPosition(left, top);
    }

    private static (double X, double Y) Center(PixelRect r) => (r.Left + (r.Width / 2.0), r.Top + (r.Height / 2.0));

    private static double DistanceSquared((double X, double Y) a, (double X, double Y) b) =>
        ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y));
}
