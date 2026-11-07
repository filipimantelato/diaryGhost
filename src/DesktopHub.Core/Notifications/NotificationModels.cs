using DesktopHub.Core.Events;

namespace DesktopHub.Core.Notifications;

public enum NotificationCategory
{
    Timer,
    Reminder,
    CalendarEvent,
    News,
    Weather,
    Integration,
    Sync,
    System,
}

/// <summary>
/// Prioridade decide o que atravessa pausas, horário de silêncio e jogos em tela cheia:
/// Low/Normal (ex.: notícia comum) não interrompem; High (temporizador, lembrete) passa por tela cheia e
/// silêncio, mas respeita a pausa manual; Critical (alerta meteorológico grave) sempre passa.
/// </summary>
public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Critical,
}

public sealed record NotificationAction(string Id, string Label);

/// <summary>
/// Lista de escolha exibida na notificação (ex.: "Adiar por: 5/10/30/60 min"). O valor escolhido chega
/// junto da ação como <c>"{acao}:{valor}"</c> em <see cref="NotificationActivated.ActionId"/>.
/// </summary>
public sealed record NotificationSelection(string Id, string DefaultOptionId, IReadOnlyList<NotificationAction> Options);

/// <param name="Tag">Identifica a origem para tratar ações (ex.: "timer", "pomodoro").</param>
/// <param name="DedupKey">Notificações com a mesma chave em 10 min são descartadas como duplicadas.</param>
public sealed record NotificationRequest(
    NotificationCategory Category,
    NotificationPriority Priority,
    string Title,
    string? Body,
    string Tag,
    string? DedupKey = null,
    IReadOnlyList<NotificationAction>? Actions = null,
    NotificationSelection? Selection = null);

public enum DeliveryOutcome
{
    Shown,
    SuppressedPaused,
    SuppressedQuietHours,
    SuppressedFullscreen,
    SuppressedCategoryOff,
    Duplicate,
    RateLimited,
    BlockedByWindows,
    Failed,
}

public sealed record NotificationRecord(
    Guid Id,
    NotificationCategory Category,
    NotificationPriority Priority,
    string Title,
    string? Body,
    string? DedupKey,
    DateTimeOffset CreatedAt,
    DeliveryOutcome Outcome);

/// <summary>Estado do canal de notificações do Windows para este app.</summary>
public enum NotificationChannelStatus
{
    Unknown,
    Enabled,
    DisabledByUser,
    Unavailable,
}

/// <summary>Porta: exibe a notificação no sistema (toasts do Windows na Infrastructure).</summary>
public interface INotificationSink
{
    NotificationChannelStatus Status { get; }

    /// <returns>Shown, BlockedByWindows ou Failed.</returns>
    DeliveryOutcome Show(Guid id, NotificationRequest request);

    /// <summary>O usuário clicou na notificação (actionId null) ou num botão dela.</summary>
    event Action<Guid, string?>? Activated;
}

/// <summary>Porta: histórico persistido (usado também para deduplicar e limitar frequência).</summary>
public interface INotificationHistory
{
    void Add(NotificationRecord record);

    bool ExistsSince(string dedupKey, DateTimeOffset since);

    int CountShownSince(NotificationCategory category, DateTimeOffset since);

    IReadOnlyList<NotificationRecord> Recent(int count);

    int PruneBefore(DateTimeOffset cutoff);
}

/// <summary>O usuário acionou uma notificação. <see cref="ActionId"/> null = clique no corpo.</summary>
public sealed record NotificationActivated(string Tag, string? ActionId) : IAppEvent;

/// <summary>Uma notificação foi processada (exibida ou suprimida) — para histórico na UI.</summary>
public sealed record NotificationProcessed(NotificationRecord Record) : IAppEvent;

/// <summary>Página do app que o clique numa notificação abre (via link <c>diaryghost:</c>, que funciona com o app fechado).</summary>
public static class NotificationPages
{
    public static string? ForTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag switch
        {
            "timer" or "pomodoro" => "productivity",
            "news" => "news",
            "activity" => "integrations",
            "reminder-missed" => "reminders",
            _ when tag.StartsWith("reminder:Task", StringComparison.Ordinal) => "tasks",
            _ when tag.StartsWith("reminder:", StringComparison.Ordinal) => "calendar",
            _ => null,
        };
    }
}
