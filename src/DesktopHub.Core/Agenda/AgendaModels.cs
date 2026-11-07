using DesktopHub.Core.Events;

namespace DesktopHub.Core.Agenda;

public sealed record AgendaCategory(Guid Id, string Name, string Color);

/// <summary>
/// Evento do calendário local. <see cref="Start"/> é horário de parede no fuso <see cref="TimeZoneId"/>
/// (ex.: "toda segunda às 09:00" continua às 09:00 depois de uma mudança de horário de verão).
/// Eventos de dia inteiro são "flutuantes": valem no fuso em que o usuário está.
/// </summary>
public sealed record CalendarEvent
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required DateTime Start { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromHours(1);
    public bool AllDay { get; init; }
    public required string TimeZoneId { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    /// <summary>Inícios (horário de parede) de ocorrências removidas da série (EXDATE).</summary>
    public IReadOnlyList<DateTime> Exceptions { get; init; } = [];
    public Guid? CategoryId { get; init; }
    public string? Location { get; init; }
    public string? Notes { get; init; }
    /// <summary>Minutos antes do início em que avisar (0 = na hora). Dia inteiro: antes das 09:00 do dia.</summary>
    public IReadOnlyList<int> ReminderMinutes { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Agenda assinada (iCal) de onde o evento veio; esses eventos são somente leitura.</summary>
    public Guid? SubscriptionId { get; init; }
    /// <summary>UID do iCalendar (para exportar/identificar).</summary>
    public string? ExternalUid { get; init; }

    public bool IsReadOnly => SubscriptionId is not null;
}

/// <summary>Agenda assinada pelo endereço iCal (ex.: "endereço secreto" do Google Agenda ou do Outlook).</summary>
public sealed record CalendarSubscription
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid? CategoryId { get; init; }
    /// <summary>Aviso aplicado aos eventos importados (null = sem aviso).</summary>
    public int? ReminderMinutes { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
    public int EventCount { get; init; }
}

/// <param name="Start">Início no horário de parede do evento (identifica a ocorrência na série).</param>
public sealed record EventOccurrence(CalendarEvent Event, DateTime Start, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public bool IsRecurring => Event.Recurrence is not null;
}

public enum TaskPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

/// <summary>
/// Tarefa (e lembrete avulso: uma tarefa com prazo e aviso). Prazo com hora fica no fuso
/// <see cref="TimeZoneId"/>; prazo só com data é flutuante. Tarefas recorrentes têm um único registro:
/// concluir avança <see cref="Due"/> para a próxima ocorrência da regra (ancorada em <see cref="RecurrenceStart"/>).
/// </summary>
public sealed record TaskItem
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public Guid? CategoryId { get; init; }
    public DateTime? Due { get; init; }
    public bool DueHasTime { get; init; }
    public required string TimeZoneId { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    public DateTime? RecurrenceStart { get; init; }
    /// <summary>null = sem aviso; 0 = no horário do prazo.</summary>
    public int? ReminderMinutes { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int CompletedCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsCompleted => CompletedAt is not null;
}

/// <summary>Porta de persistência da agenda. O serviço mantém tudo em memória e grava cada mudança.</summary>
public interface IAgendaStore
{
    IReadOnlyList<CalendarEvent> LoadEvents();
    void UpsertEvent(CalendarEvent value);
    void DeleteEvent(Guid id);

    IReadOnlyList<TaskItem> LoadTasks();
    void UpsertTask(TaskItem value);
    void DeleteTask(Guid id);

    IReadOnlyList<AgendaCategory> LoadCategories();
    void UpsertCategory(AgendaCategory value);
    /// <summary>Remove a categoria e desvincula eventos e tarefas dela.</summary>
    void DeleteCategory(Guid id);

    IReadOnlyList<CalendarSubscription> LoadSubscriptions();
    void UpsertSubscription(CalendarSubscription value);
    /// <summary>Remove a assinatura e os eventos dela.</summary>
    void DeleteSubscription(Guid id);
    /// <summary>Troca, numa transação, todos os eventos de uma assinatura.</summary>
    void ReplaceSubscriptionEvents(Guid subscriptionId, IReadOnlyList<CalendarEvent> events);
}

/// <summary>Eventos, tarefas ou categorias mudaram.</summary>
public sealed record AgendaChanged : IAppEvent;
