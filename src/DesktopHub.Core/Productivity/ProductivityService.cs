using System.Globalization;
using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Productivity;

/// <summary>
/// Cronômetro, temporizador e Pomodoro. Um único timer (do <see cref="TimeProvider"/>) dorme até o prazo
/// mais próximo — não há tique por segundo aqui; a UI calcula o tempo exibido a partir do estado.
/// O estado ativo é salvo a cada mudança, então reiniciar o app retoma tudo; prazos que venceram com o
/// app fechado ou o PC suspenso são concluídos e notificados como "atrasados".
/// </summary>
public sealed partial class ProductivityService : IDisposable
{
    /// <summary>Atraso a partir do qual uma conclusão é tratada como "aconteceu enquanto estava fechado/suspenso".</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromSeconds(5);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IProductivityStore _store;
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notifications;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<ProductivityService> _logger;
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private ProductivitySnapshot _state;
    private ITimer? _timer;
    private bool _disposed;

    public ProductivityService(IProductivityStore store, SettingsService settings, NotificationCenter notifications,
        IEventBus bus, TimeProvider time, ILogger<ProductivityService> logger)
    {
        _store = store;
        _settings = settings;
        _notifications = notifications;
        _bus = bus;
        _time = time;
        _logger = logger;
        _state = Initial();
    }

    public ProductivitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    public PomodoroConfig PomodoroConfig => _settings.Productivity.Pomodoro;

    public void Start()
    {
        var loaded = _store.LoadActive();
        lock (_gate)
        {
            _state = loaded ?? Initial();
        }
        _subscriptions.Add(_bus.Subscribe<NotificationActivated>(OnNotificationActivated));
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Productivity)
            {
                ApplyConfigToIdlePomodoro();
            }
        }));
        LogRecovered(loaded is not null);
        // Conclui o que venceu enquanto o app estava fechado.
        Mutate(s => s);
    }

    /// <summary>Recalcula o agendamento — chamado ao voltar da suspensão e quando o relógio do sistema muda.</summary>
    public void Reschedule() => Mutate(s => s);

    // ---------- Cronômetro ----------

    public void StopwatchToggle() => Mutate(s => s with
    {
        Stopwatch = s.Stopwatch.IsRunning ? s.Stopwatch.Pause(Now) : s.Stopwatch.Start(Now),
    });

    public void StopwatchLap() => Mutate(s => s with { Stopwatch = s.Stopwatch.Lap(Now) });

    public void StopwatchReset() => Mutate(s =>
    {
        var now = Now;
        var elapsed = s.Stopwatch.ElapsedAt(now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            // Início aproximado (não guardamos pausas intermediárias): fim − tempo decorrido.
            Record(new FocusSession(Guid.CreateVersion7(now), SessionKind.Stopwatch, null, now - elapsed, now, null, elapsed, Completed: true));
        }
        return s with { Stopwatch = StopwatchState.Reset() };
    });

    // ---------- Temporizador ----------

    public void TimerStart(TimeSpan duration)
    {
        _settings.UpdateProductivity(p => p with { LastTimerMinutes = (int)Math.Max(1, Math.Round(duration.TotalMinutes)) });
        Mutate(s =>
        {
            RecordCancelledTimer(s);
            var now = Now;
            return s with { Timer = CountdownState.Ready(duration).Start(now), TimerStartedAt = now };
        });
    }

    public void TimerToggle() => Mutate(s =>
    {
        if (s.Timer is not { } timer)
        {
            return s;
        }
        var now = Now;
        if (timer.IsFinishedAt(now))
        {
            return s with { Timer = timer.Restart().Start(now), TimerStartedAt = now };
        }
        return s with
        {
            Timer = timer.IsRunning ? timer.Pause(now) : timer.Start(now),
            TimerStartedAt = s.TimerStartedAt ?? now,
        };
    });

    public void TimerReset() => Mutate(s =>
    {
        RecordCancelledTimer(s);
        return s with { Timer = s.Timer?.Restart(), TimerStartedAt = null };
    });

    public void TimerClear() => Mutate(s =>
    {
        RecordCancelledTimer(s);
        return s with { Timer = null, TimerStartedAt = null };
    });

    // ---------- Pomodoro ----------

    public void PomodoroToggle() => Mutate(s =>
    {
        var now = Now;
        var c = s.Pomodoro.Countdown;
        return s with
        {
            Pomodoro = s.Pomodoro with { Countdown = c.IsRunning ? c.Pause(now) : c.Start(now) },
            PomodoroPhaseStartedAt = s.PomodoroPhaseStartedAt ?? now,
        };
    });

    /// <summary>Pula a fase atual (um foco pulado não conta como ciclo concluído).</summary>
    public void PomodoroSkip() => Mutate(s =>
    {
        var now = Now;
        RecordPomodoro(s, now, completed: false);
        return s with { Pomodoro = s.Pomodoro.Advance(PomodoroConfig, now, completed: false), PomodoroPhaseStartedAt = null };
    });

    public void PomodoroReset() => Mutate(s =>
    {
        RecordPomodoro(s, Now, completed: false);
        return s with { Pomodoro = PomodoroState.Initial(PomodoroConfig), PomodoroPhaseStartedAt = null };
    });

    public IReadOnlyList<FocusSession> RecentSessions(int count) => _store.RecentSessions(count);

    public ProductivityStats Stats()
    {
        var localNow = _time.GetLocalNow();
        var todayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        var sessions = _store.SessionsSince(todayStart.AddDays(-6));
        var focus = sessions.Where(s => s.Kind == SessionKind.PomodoroFocus).ToList();
        var today = focus.Where(s => s.StartedAt >= todayStart).ToList();
        return new ProductivityStats(
            TimeSpan.FromTicks(today.Sum(s => s.Actual.Ticks)),
            today.Count(s => s.Completed),
            TimeSpan.FromTicks(focus.Sum(s => s.Actual.Ticks)),
            sessions.Count);
    }

    // ---------- Núcleo ----------

    private ProductivitySnapshot Initial() =>
        new(StopwatchState.Idle, null, null, PomodoroState.Initial(_settings.Productivity.Pomodoro), null);

    /// <summary>Aplica uma mudança, conclui prazos vencidos, salva, publica e reagenda — tudo serializado.</summary>
    private void Mutate(Func<ProductivitySnapshot, ProductivitySnapshot> change)
    {
        var completions = new List<TimerCompleted>();
        ProductivitySnapshot snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var updated = CompleteDue(change(_state), completions);
            if (!ReferenceEquals(updated, _state))
            {
                _state = updated;
                _store.SaveActive(updated);
            }
            snapshot = _state;
            ScheduleNext(snapshot);
        }
        _bus.Publish(new ProductivityChanged(snapshot));
        // Fora do lock: notificar pode envolver o sistema (toasts) e outros assinantes.
        foreach (var completed in completions)
        {
            _bus.Publish(completed);
            Notify(completed);
        }
    }

    private ProductivitySnapshot CompleteDue(ProductivitySnapshot s, List<TimerCompleted> completions)
    {
        var now = Now;
        if (s.Timer is { IsRunning: true, Deadline: { } timerDeadline } timer && timer.IsFinishedAt(now))
        {
            var late = now - timerDeadline > LateThreshold;
            Record(new FocusSession(Guid.CreateVersion7(now), SessionKind.Timer, null, s.TimerStartedAt ?? timerDeadline - timer.Duration,
                timerDeadline, timer.Duration, timer.Duration, Completed: true));
            s = s with { Timer = timer.Pause(now), TimerStartedAt = null };
            completions.Add(new TimerCompleted(SessionKind.Timer, "Temporizador concluído",
                $"{FormatDuration(timer.Duration)} terminaram{LateSuffix(timerDeadline, late)}.", late));
        }

        if (s.Pomodoro.Countdown is { IsRunning: true, Deadline: { } phaseDeadline } && s.Pomodoro.Countdown.IsFinishedAt(now))
        {
            var late = now - phaseDeadline > LateThreshold;
            var finished = s.Pomodoro;
            RecordPomodoro(s, phaseDeadline, completed: true);
            // Se venceu com o app fechado, não inicia a próxima fase sozinho: o usuário não estava lá.
            var config = late ? PomodoroConfig with { AutoStartNext = false } : PomodoroConfig;
            var next = finished.Advance(config, phaseDeadline, completed: true);
            s = s with { Pomodoro = next, PomodoroPhaseStartedAt = next.Countdown.IsRunning ? phaseDeadline : null };
            completions.Add(PomodoroCompletion(finished.Phase, next, late, phaseDeadline));
        }
        return s;
    }

    private TimerCompleted PomodoroCompletion(PomodoroPhase finished, PomodoroState next, bool late, DateTimeOffset at)
    {
        var nextText = next.Phase switch
        {
            PomodoroPhase.ShortBreak => $"pausa curta de {PomodoroConfig.ShortBreakMinutes} min",
            PomodoroPhase.LongBreak => $"pausa longa de {PomodoroConfig.LongBreakMinutes} min",
            _ => $"foco de {PomodoroConfig.FocusMinutes} min",
        };
        var started = next.Countdown.IsRunning ? "Começou a " : "Próximo: ";
        return finished == PomodoroPhase.Focus
            ? new TimerCompleted(SessionKind.PomodoroFocus, "Foco concluído", $"{started}{nextText}{LateSuffix(at, late)}.", late)
            : new TimerCompleted(SessionKind.PomodoroBreak, "Pausa concluída", $"{started}{nextText}{LateSuffix(at, late)}.", late);
    }

    private void Notify(TimerCompleted completed)
    {
        var isTimer = completed.Kind == SessionKind.Timer;
        var nextIsWaiting = !isTimer && !Snapshot.Pomodoro.Countdown.IsRunning;
        IReadOnlyList<NotificationAction> actions = isTimer
            ? [new("repeat", "Repetir")]
            : nextIsWaiting ? [new("next", "Iniciar")] : [];
        _notifications.Notify(new NotificationRequest(
            NotificationCategory.Timer,
            NotificationPriority.High,
            completed.Title,
            completed.Message,
            isTimer ? "timer" : "pomodoro",
            DedupKey: $"{completed.Kind}:{completed.Title}:{Now.ToUnixTimeSeconds() / 5}",
            Actions: actions));
    }

    private void OnNotificationActivated(NotificationActivated e)
    {
        switch (e.Tag, e.ActionId)
        {
            case ("timer", "repeat"):
                TimerStart(TimeSpan.FromMinutes(_settings.Productivity.LastTimerMinutes));
                break;
            case ("pomodoro", "next"):
                if (!Snapshot.Pomodoro.Countdown.IsRunning)
                {
                    PomodoroToggle();
                }
                break;
            case ("timer" or "pomodoro", null):
                _bus.Publish(new OpenMainRequested("productivity"));
                break;
        }
    }

    private void ApplyConfigToIdlePomodoro() => Mutate(s =>
        s.Pomodoro.Countdown.HasStarted
            ? s
            : s with { Pomodoro = s.Pomodoro with { Countdown = CountdownState.Ready(PomodoroConfig.DurationOf(s.Pomodoro.Phase)) } });

    private void ScheduleNext(ProductivitySnapshot s)
    {
        _timer?.Dispose();
        _timer = null;
        var deadlines = new[] { s.Timer?.Deadline, s.Pomodoro.Countdown.Deadline }.Where(d => d is not null).Select(d => d!.Value).ToList();
        if (deadlines.Count == 0)
        {
            return;
        }
        var due = deadlines.Min() - Now;
        // +50 ms: acorda logo depois do prazo, não um instante antes.
        _timer = _time.CreateTimer(_ => Mutate(x => x), null, (due < TimeSpan.Zero ? TimeSpan.Zero : due) + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    private void RecordCancelledTimer(ProductivitySnapshot s)
    {
        if (s.Timer is { HasStarted: true } timer && !timer.IsFinishedAt(Now))
        {
            var now = Now;
            Record(new FocusSession(Guid.CreateVersion7(now), SessionKind.Timer, null, s.TimerStartedAt ?? now, now, timer.Duration, timer.ElapsedAt(now), Completed: false));
        }
    }

    private void RecordPomodoro(ProductivitySnapshot s, DateTimeOffset endedAt, bool completed)
    {
        var c = s.Pomodoro.Countdown;
        if (!c.HasStarted)
        {
            return;
        }
        var kind = s.Pomodoro.Phase == PomodoroPhase.Focus ? SessionKind.PomodoroFocus : SessionKind.PomodoroBreak;
        var actual = completed ? c.Duration : c.ElapsedAt(endedAt);
        Record(new FocusSession(Guid.CreateVersion7(endedAt), kind, null, s.PomodoroPhaseStartedAt ?? endedAt - actual, endedAt, c.Duration, actual, completed));
    }

    private void Record(FocusSession session)
    {
        _store.AddSession(session);
        LogSession(session.Kind, session.Actual, session.Completed);
    }

    private static string LateSuffix(DateTimeOffset at, bool late) =>
        late ? $" (às {at.ToLocalTime().ToString("HH:mm", PtBr)}, com o app fechado ou o PC suspenso)" : "";

    public static string FormatDuration(TimeSpan d) => d.TotalMinutes >= 60
        ? (d.Minutes == 0 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalHours} h {d.Minutes} min")
        : d.Seconds == 0 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalMinutes} min {d.Seconds} s";

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _subscriptions.ForEach(s => s.Dispose());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Produtividade iniciada (estado recuperado: {Recovered})")]
    private partial void LogRecovered(bool recovered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sessão registrada: {Kind}, {Actual}, concluída: {Completed}")]
    private partial void LogSession(SessionKind kind, TimeSpan actual, bool completed);
}
