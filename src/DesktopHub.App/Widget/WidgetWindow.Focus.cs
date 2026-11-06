using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopHub.App.Productivity;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Widget;

/// <summary>Marca de seleção para chips estáticos (o estilo Chip lê <c>IsSelected</c> do DataContext).</summary>
public sealed record ChipSelection(bool IsSelected);

public partial class WidgetWindow
{
    private readonly ProductivityService _productivity;
    private DispatcherTimer? _focusTimer;
    private ProductivitySnapshot? _focus;

    private void InitializeFocus()
    {
        _focusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _focusTimer.Tick += (_, _) => RenderFocusTick();
        ShowProductivity(_productivity.Snapshot);
    }

    internal void ShowProductivity(ProductivitySnapshot snapshot)
    {
        _focus = snapshot;
        RenderFocusPanel();
        RenderFocusTick();
        RenderCompact();
        UpdateTimers();
        RequestResize();
    }

    /// <summary>Algo que vale mostrar no modo compacto (rodando ou pausado no meio).</summary>
    private (string Glyph, string Text)? ActiveFocusSummary()
    {
        if (_focus is not { } s)
        {
            return null;
        }
        var now = _productivity.Now;
        var p = s.Pomodoro;
        if (p.Countdown.HasStarted)
        {
            var state = p.Countdown.IsRunning ? FocusView.PhaseName(p.Phase) : $"{FocusView.PhaseName(p.Phase)} pausado";
            return ("", $"{FocusView.Countdown(p.Countdown.RemainingAt(now))} · {state}");
        }
        if (s.Timer is { HasStarted: true } t && !t.IsFinishedAt(now))
        {
            return ("", $"{FocusView.Countdown(t.RemainingAt(now))} · {(t.IsRunning ? "Temporizador" : "Temporizador pausado")}");
        }
        if (!s.Stopwatch.IsIdle)
        {
            return ("", $"{FocusView.Elapsed(s.Stopwatch.ElapsedAt(now))} · {(s.Stopwatch.IsRunning ? "Cronômetro" : "Cronômetro pausado")}");
        }
        return null;
    }

    private bool AnythingRunning => _focus is { } s && (s.Pomodoro.Countdown.IsRunning || s.Timer?.IsRunning == true || s.Stopwatch.IsRunning);

    /// <summary>1 Hz só quando há algo rodando e alguma superfície visível mostra o tempo.</summary>
    private void UpdateFocusTimer()
    {
        if (_focusTimer is null)
        {
            return;
        }
        var expanded = _appliedLayout == WidgetLayout.Expanded;
        var showsTime = !expanded || _appliedPanel is WidgetPanel.Focus or WidgetPanel.Home;
        _focusTimer.IsEnabled = IsVisible && showsTime && AnythingRunning;
    }

    /// <summary>Partes que mudam a cada segundo: tempo, anel, cartão do Início, texto do compacto.</summary>
    private void RenderFocusTick()
    {
        if (_focus is not { } s)
        {
            return;
        }
        var now = _productivity.Now;
        switch (_settings.Widget.FocusTool)
        {
            case FocusTool.Pomodoro:
                FocusTime.Text = FocusView.Countdown(s.Pomodoro.Countdown.RemainingAt(now));
                FocusRing.Data = FocusView.RingArc(s.Pomodoro.Countdown.RemainingFractionAt(now), 64, 4);
                break;
            case FocusTool.Timer when s.Timer is { } t:
                FocusTime.Text = FocusView.Countdown(t.RemainingAt(now));
                FocusRing.Data = FocusView.RingArc(t.RemainingFractionAt(now), 64, 4);
                break;
            case FocusTool.Stopwatch:
                var elapsed = s.Stopwatch.ElapsedAt(now);
                FocusTime.Text = FocusView.Elapsed(elapsed);
                // Cronômetro: o anel dá uma volta por minuto.
                FocusRing.Data = s.Stopwatch.IsIdle ? null : FocusView.RingArc(Math.Max(0.001, elapsed.TotalSeconds % 60 / 60), 64, 4);
                break;
        }

        HomeFocusTime.Text = FocusView.Countdown(s.Pomodoro.Countdown.RemainingAt(now));
        if (_appliedLayout == WidgetLayout.Compact && ActiveFocusSummary() is { } summary)
        {
            CompactText.Text = summary.Text;
        }
    }

    /// <summary>Partes que só mudam quando o estado muda (iniciar, pausar, fase nova, trocar ferramenta).</summary>
    private void RenderFocusPanel()
    {
        if (_focus is not { } s)
        {
            return;
        }
        var now = _productivity.Now;
        var tool = _settings.Widget.FocusTool;
        ToolPomodoro.DataContext = new ChipSelection(tool == FocusTool.Pomodoro);
        ToolTimer.DataContext = new ChipSelection(tool == FocusTool.Timer);
        ToolStopwatch.DataContext = new ChipSelection(tool == FocusTool.Stopwatch);

        var showPresets = tool == FocusTool.Timer && s.Timer is null;
        FocusMain.Visibility = showPresets ? Visibility.Collapsed : Visibility.Visible;
        TimerPresets.Visibility = showPresets ? Visibility.Visible : Visibility.Collapsed;
        FocusClear.Visibility = tool == FocusTool.Timer ? Visibility.Visible : Visibility.Collapsed;

        switch (tool)
        {
            case FocusTool.Pomodoro:
                var p = s.Pomodoro;
                var config = _productivity.PomodoroConfig;
                FocusGlyph.Text = p.Phase == PomodoroPhase.Focus ? "" : "";
                FocusStatus.Text = FocusView.PomodoroStatus(p, now);
                FocusDetail.Text = $"{FocusView.CycleDots(p.CompletedFocusInSet, config.CyclesBeforeLongBreak)}  ciclo {Math.Min(p.CompletedFocusInSet + 1, config.CyclesBeforeLongBreak)} de {config.CyclesBeforeLongBreak}";
                SetPrimary(p.Countdown.IsRunning);
                SetSecondary("", "Pular para a próxima fase");
                break;
            case FocusTool.Timer when s.Timer is { } t:
                var finished = t.IsFinishedAt(now);
                FocusGlyph.Text = "";
                FocusStatus.Text = finished ? "Concluído" : t.IsRunning ? "Temporizador" : "Temporizador pausado";
                FocusDetail.Text = $"de {ProductivityService.FormatDuration(t.Duration)}";
                SetPrimary(t.IsRunning, finished ? "Repetir" : null);
                FocusSecondary.Visibility = Visibility.Collapsed;
                break;
            case FocusTool.Stopwatch:
                var sw = s.Stopwatch;
                FocusGlyph.Text = "";
                FocusStatus.Text = sw.IsRunning ? "Cronômetro" : sw.IsIdle ? "Pronto" : "Cronômetro pausado";
                FocusDetail.Text = sw.Laps.Count == 0 ? "" : $"{sw.Laps.Count} volta(s) · última {FocusView.Elapsed(sw.Laps[^1])}";
                SetPrimary(sw.IsRunning);
                SetSecondary("", "Registrar volta");
                FocusSecondary.IsEnabled = !sw.IsIdle;
                break;
        }

        // Cartão Foco do Início (sempre Pomodoro, como no vídeo).
        HomeFocusLabel.Text = FocusView.PhaseName(s.Pomodoro.Phase);
        HomeFocusCaption.Text = FocusView.PomodoroStatus(s.Pomodoro, now);
        HomeFocusPlay.Content = s.Pomodoro.Countdown.IsRunning ? "" : "";
    }

    private void SetPrimary(bool running, string? label = null)
    {
        FocusPrimary.Content = running ? "" : label is null ? "" : "";
        var name = running ? "Pausar" : label ?? "Iniciar";
        FocusPrimary.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(FocusPrimary, name);
    }

    private void SetSecondary(string glyph, string name)
    {
        FocusSecondary.Visibility = Visibility.Visible;
        FocusSecondary.IsEnabled = true;
        FocusSecondary.Content = glyph;
        FocusSecondary.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(FocusSecondary, name);
    }

    // ---------- Comandos ----------

    private void FocusTool_Click(object sender, RoutedEventArgs e)
    {
        var tool = ReferenceEquals(sender, ToolTimer) ? FocusTool.Timer : ReferenceEquals(sender, ToolStopwatch) ? FocusTool.Stopwatch : FocusTool.Pomodoro;
        _settings.UpdateWidget(w => w with { FocusTool = tool });
    }

    private void FocusPrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_settings.Widget.FocusTool)
        {
            case FocusTool.Pomodoro: _productivity.PomodoroToggle(); break;
            case FocusTool.Timer: _productivity.TimerToggle(); break;
            case FocusTool.Stopwatch: _productivity.StopwatchToggle(); break;
        }
    }

    private void FocusSecondary_Click(object sender, RoutedEventArgs e)
    {
        switch (_settings.Widget.FocusTool)
        {
            case FocusTool.Pomodoro: _productivity.PomodoroSkip(); break;
            case FocusTool.Stopwatch: _productivity.StopwatchLap(); break;
        }
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        switch (_settings.Widget.FocusTool)
        {
            case FocusTool.Pomodoro: _productivity.PomodoroReset(); break;
            case FocusTool.Timer: _productivity.TimerReset(); break;
            case FocusTool.Stopwatch: _productivity.StopwatchReset(); break;
        }
    }

    private void FocusClear_Click(object sender, RoutedEventArgs e) => _productivity.TimerClear();

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string minutes } && int.TryParse(minutes, out var m))
        {
            _productivity.TimerStart(TimeSpan.FromMinutes(m));
        }
    }

    private void PomodoroToggle_Click(object sender, RoutedEventArgs e) => _productivity.PomodoroToggle();
}
