using DesktopHub.Core.Events;

namespace DesktopHub.Core.Productivity;

public enum SessionKind
{
    PomodoroFocus,
    PomodoroBreak,
    Timer,
    Stopwatch,
}

/// <summary>Sessão encerrada (persistida no histórico). Tempos em UTC.</summary>
public sealed record FocusSession(
    Guid Id,
    SessionKind Kind,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    TimeSpan? Planned,
    TimeSpan Actual,
    bool Completed);

/// <summary>Estado ativo completo, salvo a cada mudança para recuperação após reinício.</summary>
public sealed record ProductivitySnapshot(
    StopwatchState Stopwatch,
    CountdownState? Timer,
    DateTimeOffset? TimerStartedAt,
    PomodoroState Pomodoro,
    DateTimeOffset? PomodoroPhaseStartedAt);

public sealed record ProductivityStats(TimeSpan FocusToday, int PomodorosToday, TimeSpan FocusLast7Days, int SessionsLast7Days);

/// <summary>Porta de persistência (SQLite na Infrastructure).</summary>
public interface IProductivityStore
{
    ProductivitySnapshot? LoadActive();

    void SaveActive(ProductivitySnapshot snapshot);

    void AddSession(FocusSession session);

    IReadOnlyList<FocusSession> RecentSessions(int count);

    IReadOnlyList<FocusSession> SessionsSince(DateTimeOffset since);
}

/// <summary>Algo mudou (iniciar, pausar, fase nova…). Não é publicado a cada segundo — a UI calcula o tempo.</summary>
public sealed record ProductivityChanged(ProductivitySnapshot Snapshot) : IAppEvent;

/// <summary>Um temporizador ou fase do Pomodoro terminou. <see cref="Late"/>: terminou com o app fechado ou suspenso.</summary>
public sealed record TimerCompleted(SessionKind Kind, string Title, string Message, bool Late) : IAppEvent;
