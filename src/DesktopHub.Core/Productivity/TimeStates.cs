namespace DesktopHub.Core.Productivity;

// Máquinas de estado puras e imutáveis. Nada aqui conta ticks: cada estado guarda o tempo acumulado e,
// se estiver rodando, o instante (UTC) em que começou. O tempo exibido é sempre calculado a partir de
// "agora". Por isso não há deriva, e o estado sobrevive a reinício do app, suspensão e troca de fuso.
//
// Relógio: UTC de parede (TimeProvider.GetUtcNow). Ele avança durante a suspensão — um temporizador de
// 25 min iniciado antes de suspender termina 25 min depois no relógio real, como esperado. Um ajuste
// manual do relógio do sistema afeta a contagem; isso é aceito e documentado (ARCHITECTURE.md §5).

/// <summary>Cronômetro: conta para cima, com voltas (tempos acumulados no momento de cada volta).</summary>
public sealed record StopwatchState(TimeSpan Accumulated, DateTimeOffset? RunningSince, IReadOnlyList<TimeSpan> Laps)
{
    public static StopwatchState Idle { get; } = new(TimeSpan.Zero, null, []);

    public bool IsRunning => RunningSince is not null;

    public bool IsIdle => !IsRunning && Accumulated == TimeSpan.Zero;

    public TimeSpan ElapsedAt(DateTimeOffset now) =>
        Accumulated + (RunningSince is { } since && now > since ? now - since : TimeSpan.Zero);

    public StopwatchState Start(DateTimeOffset now) => IsRunning ? this : this with { RunningSince = now };

    public StopwatchState Pause(DateTimeOffset now) => IsRunning ? this with { Accumulated = ElapsedAt(now), RunningSince = null } : this;

    public StopwatchState Lap(DateTimeOffset now) => IsIdle ? this : this with { Laps = [.. Laps, ElapsedAt(now)] };

    public static StopwatchState Reset() => Idle;
}

/// <summary>Contagem regressiva (base do temporizador e de cada fase do Pomodoro).</summary>
public sealed record CountdownState(TimeSpan Duration, TimeSpan Accumulated, DateTimeOffset? RunningSince)
{
    public static CountdownState Ready(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A duração precisa ser positiva.");
        }
        return new(duration, TimeSpan.Zero, null);
    }

    public bool IsRunning => RunningSince is not null;

    public bool HasStarted => IsRunning || Accumulated > TimeSpan.Zero;

    public TimeSpan ElapsedAt(DateTimeOffset now)
    {
        var elapsed = Accumulated + (RunningSince is { } since && now > since ? now - since : TimeSpan.Zero);
        return elapsed > Duration ? Duration : elapsed;
    }

    public TimeSpan RemainingAt(DateTimeOffset now) => Duration - ElapsedAt(now);

    public bool IsFinishedAt(DateTimeOffset now) => RemainingAt(now) <= TimeSpan.Zero;

    /// <summary>Instante em que termina, se estiver rodando.</summary>
    public DateTimeOffset? Deadline => RunningSince is { } since ? since + (Duration - Accumulated) : null;

    /// <summary>Fração restante (1 → 0), para o anel de progresso.</summary>
    public double RemainingFractionAt(DateTimeOffset now) => RemainingAt(now).TotalMilliseconds / Duration.TotalMilliseconds;

    public CountdownState Start(DateTimeOffset now) => IsRunning || IsFinishedAt(now) ? this : this with { RunningSince = now };

    public CountdownState Pause(DateTimeOffset now) => IsRunning ? this with { Accumulated = ElapsedAt(now), RunningSince = null } : this;

    public CountdownState Restart() => this with { Accumulated = TimeSpan.Zero, RunningSince = null };
}

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

public sealed record PomodoroConfig
{
    public int FocusMinutes { get; init; } = 25;
    public int ShortBreakMinutes { get; init; } = 5;
    public int LongBreakMinutes { get; init; } = 15;
    public int CyclesBeforeLongBreak { get; init; } = 4;
    /// <summary>Iniciar a próxima fase automaticamente ao terminar a atual.</summary>
    public bool AutoStartNext { get; init; }

    public PomodoroConfig Sanitize() => this with
    {
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180),
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60),
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120),
        CyclesBeforeLongBreak = Math.Clamp(CyclesBeforeLongBreak, 1, 12),
    };

    public TimeSpan DurationOf(PomodoroPhase phase) => TimeSpan.FromMinutes(phase switch
    {
        PomodoroPhase.Focus => FocusMinutes,
        PomodoroPhase.ShortBreak => ShortBreakMinutes,
        _ => LongBreakMinutes,
    });
}

/// <summary>Pomodoro: fase atual, focos concluídos no conjunto e a contagem da fase.</summary>
public sealed record PomodoroState(PomodoroPhase Phase, int CompletedFocusInSet, CountdownState Countdown)
{
    public static PomodoroState Initial(PomodoroConfig config) => new(PomodoroPhase.Focus, 0, CountdownState.Ready(config.DurationOf(PomodoroPhase.Focus)));

    /// <summary>
    /// Vai para a próxima fase. <paramref name="completed"/> é falso quando o usuário pulou a fase:
    /// um foco pulado não conta como ciclo concluído.
    /// </summary>
    public PomodoroState Advance(PomodoroConfig config, DateTimeOffset now, bool completed)
    {
        var (next, count) = Phase switch
        {
            PomodoroPhase.Focus when completed && CompletedFocusInSet + 1 >= config.CyclesBeforeLongBreak => (PomodoroPhase.LongBreak, CompletedFocusInSet + 1),
            PomodoroPhase.Focus => (PomodoroPhase.ShortBreak, CompletedFocusInSet + (completed ? 1 : 0)),
            PomodoroPhase.LongBreak => (PomodoroPhase.Focus, 0),
            _ => (PomodoroPhase.Focus, CompletedFocusInSet),
        };
        var countdown = CountdownState.Ready(config.DurationOf(next));
        // Só encadeia automaticamente quando a fase terminou de verdade; pular espera o usuário.
        if (config.AutoStartNext && completed)
        {
            countdown = countdown.Start(now);
        }
        return new PomodoroState(next, count, countdown);
    }
}
