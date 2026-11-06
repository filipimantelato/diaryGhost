using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopHub.App.Productivity;
using DesktopHub.Core.Events;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

public sealed record LapRow(string Label, string Time);

public sealed record HistoryRow(string When, string What, string Result);

/// <summary>
/// Página Produtividade: Pomodoro (com configuração), temporizador, cronômetro, estatísticas e histórico.
/// O relógio da página só roda (1 Hz) enquanto algo está em andamento e a página está aberta.
/// </summary>
public partial class ProductivityPage : UserControl, IDisposable
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ProductivityService _productivity;
    private readonly SettingsService _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _tick;
    private bool _syncing;

    internal ProductivityPage(ProductivityService productivity, SettingsService settings, IEventBus bus)
    {
        _productivity = productivity;
        _settings = settings;
        InitializeComponent();
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => RenderTimes();
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<ProductivityChanged>(_ => Render(), ui));
        _subscriptions.Add(bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Productivity)
            {
                Render();
            }
        }, ui));
        CustomMinutes.Text = _settings.Productivity.LastTimerMinutes.ToString(PtBr);
        Render();
    }

    private void Render()
    {
        var s = _productivity.Snapshot;
        var now = _productivity.Now;
        var config = _productivity.PomodoroConfig;

        // Pomodoro
        PomodoroStatus.Text = FocusView.PomodoroStatus(s.Pomodoro, now);
        PomodoroDots.Text = $"{FocusView.CycleDots(s.Pomodoro.CompletedFocusInSet, config.CyclesBeforeLongBreak)}   {s.Pomodoro.CompletedFocusInSet} de {config.CyclesBeforeLongBreak} focos até a pausa longa";
        SetPlayPause(PomodoroPrimary, s.Pomodoro.Countdown.IsRunning, "Pomodoro");
        FocusMinutesText.Text = $"{config.FocusMinutes} min";
        ShortMinutesText.Text = $"{config.ShortBreakMinutes} min";
        LongMinutesText.Text = $"{config.LongBreakMinutes} min";
        CyclesText.Text = config.CyclesBeforeLongBreak.ToString(PtBr);
        _syncing = true;
        AutoStartNext.IsChecked = config.AutoStartNext;
        _syncing = false;

        // Temporizador
        TimerActive.Visibility = s.Timer is null ? Visibility.Collapsed : Visibility.Visible;
        TimerSetup.Visibility = s.Timer is null ? Visibility.Visible : Visibility.Collapsed;
        if (s.Timer is { } t)
        {
            var finished = t.IsFinishedAt(now);
            TimerStatus.Text = finished ? $"Concluído · {ProductivityService.FormatDuration(t.Duration)}"
                : $"{(t.IsRunning ? "Em andamento" : "Pausado")} · {ProductivityService.FormatDuration(t.Duration)}";
            SetPlayPause(TimerPrimary, t.IsRunning, "temporizador", finished ? "" : null);
        }

        // Cronômetro
        var sw = s.Stopwatch;
        StopwatchStatus.Text = sw.IsRunning ? "Em andamento" : sw.IsIdle ? "Pronto" : "Pausado";
        SetPlayPause(StopwatchPrimary, sw.IsRunning, "cronômetro");
        StopwatchLapButton.IsEnabled = !sw.IsIdle;
        LapList.ItemsSource = sw.Laps.Select((lap, i) => new LapRow($"Volta {i + 1}", FocusView.Elapsed(lap))).Reverse().ToList();

        // Estatísticas e histórico (consultas leves: só ao mudar estado, não a cada segundo)
        var stats = _productivity.Stats();
        StatFocusToday.Text = ProductivityService.FormatDuration(stats.FocusToday.Ticks == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Math.Round(stats.FocusToday.TotalMinutes)));
        StatPomodorosToday.Text = stats.PomodorosToday.ToString(PtBr);
        StatFocusWeek.Text = ProductivityService.FormatDuration(TimeSpan.FromMinutes(Math.Round(stats.FocusLast7Days.TotalMinutes)));
        StatSessionsWeek.Text = stats.SessionsLast7Days.ToString(PtBr);
        var history = _productivity.RecentSessions(10);
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.ItemsSource = history.Select(ToRow).ToList();

        RenderTimes();
        var running = s.Pomodoro.Countdown.IsRunning || s.Timer?.IsRunning == true || sw.IsRunning;
        _tick.IsEnabled = running;
    }

    private void RenderTimes()
    {
        var s = _productivity.Snapshot;
        var now = _productivity.Now;
        PomodoroTime.Text = FocusView.Countdown(s.Pomodoro.Countdown.RemainingAt(now));
        if (s.Timer is { } t)
        {
            TimerTime.Text = FocusView.Countdown(t.RemainingAt(now));
        }
        StopwatchTime.Text = FocusView.Elapsed(s.Stopwatch.ElapsedAt(now));
    }

    private static HistoryRow ToRow(FocusSession s)
    {
        var what = s.Kind switch
        {
            SessionKind.PomodoroFocus => "Foco (Pomodoro)",
            SessionKind.PomodoroBreak => "Pausa (Pomodoro)",
            SessionKind.Timer => "Temporizador",
            _ => "Cronômetro",
        };
        var result = s.Completed ? FocusView.Elapsed(s.Actual) : $"interrompido em {FocusView.Elapsed(s.Actual)}";
        return new HistoryRow(s.StartedAt.ToLocalTime().ToString("dd/MM HH:mm", PtBr), what + (s.Label is null ? "" : $" — {s.Label}"), result);
    }

    private static void SetPlayPause(Button button, bool running, string what, string? idleGlyph = null)
    {
        button.Content = running ? "" : idleGlyph ?? "";
        var name = running ? $"Pausar {what}" : idleGlyph is null ? $"Iniciar {what}" : $"Repetir {what}";
        button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(button, name);
    }

    // ---------- Pomodoro ----------

    private void PomodoroPrimary_Click(object sender, RoutedEventArgs e) => _productivity.PomodoroToggle();

    private void PomodoroSkip_Click(object sender, RoutedEventArgs e) => _productivity.PomodoroSkip();

    private void PomodoroReset_Click(object sender, RoutedEventArgs e) => _productivity.PomodoroReset();

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || tag.Split(':') is not [var field, var deltaText] ||
            !int.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            return;
        }
        _settings.UpdateProductivity(p => p with
        {
            Pomodoro = field switch
            {
                "focus" => p.Pomodoro with { FocusMinutes = p.Pomodoro.FocusMinutes + delta },
                "short" => p.Pomodoro with { ShortBreakMinutes = p.Pomodoro.ShortBreakMinutes + delta },
                "long" => p.Pomodoro with { LongBreakMinutes = p.Pomodoro.LongBreakMinutes + delta },
                "cycles" => p.Pomodoro with { CyclesBeforeLongBreak = p.Pomodoro.CyclesBeforeLongBreak + delta },
                _ => p.Pomodoro,
            },
        });
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _settings.UpdateProductivity(p => p with { Pomodoro = p.Pomodoro with { AutoStartNext = AutoStartNext.IsChecked == true } });
        }
    }

    // ---------- Temporizador ----------

    private void TimerPrimary_Click(object sender, RoutedEventArgs e) => _productivity.TimerToggle();

    private void TimerReset_Click(object sender, RoutedEventArgs e) => _productivity.TimerReset();

    private void TimerClear_Click(object sender, RoutedEventArgs e) => _productivity.TimerClear();

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && int.TryParse(text, out var minutes))
        {
            _productivity.TimerStart(TimeSpan.FromMinutes(minutes));
        }
    }

    private void CustomStart_Click(object sender, RoutedEventArgs e) => StartCustom();

    private void CustomMinutes_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartCustom();
        }
    }

    private void StartCustom()
    {
        // Entrada do usuário validada: inteiro entre 1 e 1440 minutos (24 h).
        var ok = int.TryParse(CustomMinutes.Text.Trim(), NumberStyles.Integer, PtBr, out var minutes) && minutes is >= 1 and <= 1440;
        CustomError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (ok)
        {
            _productivity.TimerStart(TimeSpan.FromMinutes(minutes));
        }
    }

    // ---------- Cronômetro ----------

    private void StopwatchPrimary_Click(object sender, RoutedEventArgs e) => _productivity.StopwatchToggle();

    private void StopwatchLap_Click(object sender, RoutedEventArgs e) => _productivity.StopwatchLap();

    private void StopwatchReset_Click(object sender, RoutedEventArgs e) => _productivity.StopwatchReset();

    public void Dispose()
    {
        _tick.Stop();
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
