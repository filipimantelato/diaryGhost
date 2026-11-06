using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopHub.Core.Productivity;

namespace DesktopHub.App.Productivity;

/// <summary>Formatação e geometria compartilhadas entre o widget e a página Produtividade.</summary>
internal static class FocusView
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Contagem regressiva arredonda para cima (mostra 0:01 até o fim, nunca 0:00 antes da hora).</summary>
    public static string Countdown(TimeSpan remaining) => Clock(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds))));

    /// <summary>Tempo decorrido arredonda para baixo.</summary>
    public static string Elapsed(TimeSpan elapsed) => Clock(TimeSpan.FromSeconds(Math.Floor(Math.Max(0, elapsed.TotalSeconds))));

    private static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss", PtBr)
        : t.ToString(@"mm\:ss", PtBr);

    public static string PhaseName(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => "Foco",
        PomodoroPhase.ShortBreak => "Pausa curta",
        _ => "Pausa longa",
    };

    public static string PomodoroStatus(PomodoroState p, DateTimeOffset now)
    {
        var c = p.Countdown;
        return (p.Phase, c.IsRunning, c.HasStarted) switch
        {
            (PomodoroPhase.Focus, true, _) => "Focando",
            (PomodoroPhase.Focus, false, true) => "Foco pausado",
            (PomodoroPhase.Focus, false, false) => "Pronto para focar",
            (_, true, _) => $"{PhaseName(p.Phase)} em andamento",
            (_, false, true) => $"{PhaseName(p.Phase)} pausada",
            _ => $"{PhaseName(p.Phase)} — pronta para começar",
        };
    }

    /// <summary>"●●○○": focos concluídos no conjunto atual.</summary>
    public static string CycleDots(int completed, int total) =>
        string.Concat(Enumerable.Range(0, Math.Max(1, total)).Select(i => i < completed ? "●" : "○"));

    /// <summary>Arco de progresso (fração restante, sentido horário a partir do topo) para um anel de diâmetro <paramref name="size"/>.</summary>
    public static Geometry? RingArc(double fraction, double size, double stroke)
    {
        var r = (size - stroke) / 2;
        var c = size / 2;
        if (fraction <= 0.0005)
        {
            return null;
        }
        if (fraction >= 0.9995)
        {
            var full = new EllipseGeometry(new Point(c, c), r, r);
            full.Freeze();
            return full;
        }
        var angle = fraction * 2 * Math.PI;
        var end = new Point(c + (r * Math.Sin(angle)), c - (r * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise, isStroked: true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
