using DesktopHub.Core.Events;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Agenda;

/// <summary>
/// Calendário local, tarefas e categorias. Os dados ficam em memória (algumas centenas de itens) e cada
/// mudança é gravada e anunciada com <see cref="AgendaChanged"/>. Ocorrências são calculadas sob demanda.
/// </summary>
public sealed partial class AgendaService
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 4000;
    public const int MaxReminders = 5;
    /// <summary>Aviso mais antecipado aceito: 4 semanas.</summary>
    public const int MaxReminderMinutes = 4 * 7 * 24 * 60;
    /// <summary>Hora de referência dos avisos de eventos de dia inteiro e de tarefas sem horário.</summary>
    public static readonly TimeSpan AllDayReminderTime = TimeSpan.FromHours(9);

    private readonly IAgendaStore _store;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<AgendaService> _logger;
    private readonly Lock _gate = new();
    private List<CalendarEvent> _events = [];
    private List<TaskItem> _tasks = [];
    private List<AgendaCategory> _categories = [];

    public AgendaService(IAgendaStore store, IEventBus bus, TimeProvider time, ILogger<AgendaService> logger)
    {
        _store = store;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Fuso em que o usuário está agora (exibição e itens flutuantes).</summary>
    public TimeZoneInfo LocalZone => _time.LocalTimeZone;

    public DateTime LocalNow => ZonedTime.ToWall(Now, LocalZone);

    public void Start()
    {
        var events = _store.LoadEvents();
        var tasks = _store.LoadTasks();
        var categories = _store.LoadCategories();
        lock (_gate)
        {
            _events = [.. events];
            _tasks = [.. tasks];
            _categories = [.. categories];
        }
        LogLoaded(events.Count, tasks.Count);
    }

    public IReadOnlyList<CalendarEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<TaskItem> Tasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _tasks];
            }
        }
    }

    public IReadOnlyList<AgendaCategory> Categories
    {
        get
        {
            lock (_gate)
            {
                return [.. _categories];
            }
        }
    }

    public AgendaCategory? Category(Guid? id) => id is null ? null : Categories.FirstOrDefault(c => c.Id == id);

    public CalendarEvent? FindEvent(Guid id) => Events.FirstOrDefault(e => e.Id == id);

    public TaskItem? FindTask(Guid id) => Tasks.FirstOrDefault(t => t.Id == id);

    // ---------- Eventos ----------

    public CalendarEvent NewEvent(DateTime start, bool allDay = false) => new()
    {
        Id = Guid.CreateVersion7(Now),
        Title = "",
        Start = allDay ? start.Date : start,
        Duration = allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1),
        AllDay = allDay,
        TimeZoneId = LocalZone.Id,
        ReminderMinutes = allDay ? [] : [10],
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public static string? Validate(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Title))
        {
            return "Dê um título ao evento.";
        }
        if (value.Title.Length > MaxTitleLength)
        {
            return $"O título pode ter até {MaxTitleLength} caracteres.";
        }
        if ((value.Notes?.Length ?? 0) > MaxNotesLength || (value.Location?.Length ?? 0) > MaxTitleLength)
        {
            return "Notas ou local longos demais.";
        }
        if (value.Duration < TimeSpan.Zero || value.Duration > TimeSpan.FromDays(31))
        {
            return "A duração deve estar entre 0 e 31 dias (o fim não pode ser antes do início).";
        }
        if (value.AllDay && value.Duration < TimeSpan.FromDays(1))
        {
            return "Eventos de dia inteiro duram pelo menos um dia.";
        }
        if (value.ReminderMinutes.Count > MaxReminders || value.ReminderMinutes.Any(m => m is < 0 or > MaxReminderMinutes))
        {
            return $"Até {MaxReminders} avisos, de 0 minutos a 4 semanas antes.";
        }
        if (value.Start.Year is < 1900 or > 2200)
        {
            return "Data fora do intervalo suportado.";
        }
        return null;
    }

    public CalendarEvent SaveEvent(CalendarEvent value)
    {
        if (Validate(value) is { } error)
        {
            throw new ArgumentException(error, nameof(value));
        }
        if (value.IsReadOnly || FindEvent(value.Id) is { IsReadOnly: true })
        {
            throw new InvalidOperationException("Eventos de agendas assinadas são somente leitura.");
        }
        var saved = value with
        {
            Title = value.Title.Trim(),
            Location = string.IsNullOrWhiteSpace(value.Location) ? null : value.Location.Trim(),
            Notes = string.IsNullOrWhiteSpace(value.Notes) ? null : value.Notes.Trim(),
            Start = DateTime.SpecifyKind(value.AllDay ? value.Start.Date : value.Start, DateTimeKind.Unspecified),
            ReminderMinutes = [.. value.ReminderMinutes.Distinct().Order()],
            CreatedAt = value.CreatedAt == default ? Now : value.CreatedAt,
            UpdatedAt = Now,
        };
        _store.UpsertEvent(saved);
        lock (_gate)
        {
            _events.RemoveAll(e => e.Id == saved.Id);
            _events.Add(saved);
        }
        Changed();
        return saved;
    }

    public void DeleteEvent(Guid id)
    {
        if (FindEvent(id) is { IsReadOnly: true })
        {
            return; // vem de uma agenda assinada: só a origem altera
        }
        _store.DeleteEvent(id);
        lock (_gate)
        {
            _events.RemoveAll(e => e.Id == id);
        }
        Changed();
    }

    /// <summary>Remove uma única ocorrência de uma série (vira uma exceção, EXDATE).</summary>
    public void DeleteOccurrence(Guid id, DateTime occurrenceStart)
    {
        if (FindEvent(id) is not { } e)
        {
            return;
        }
        if (e.Recurrence is null)
        {
            DeleteEvent(id);
            return;
        }
        SaveEvent(e with { Exceptions = [.. e.Exceptions.Append(DateTime.SpecifyKind(occurrenceStart, DateTimeKind.Unspecified)).Distinct().Order()] });
    }

    /// <summary>Encerra a série antes da ocorrência indicada ("esta e as próximas" ao excluir).</summary>
    public void EndSeriesBefore(Guid id, DateTime occurrenceStart)
    {
        if (FindEvent(id) is not { Recurrence: { } rule } e)
        {
            return;
        }
        if (occurrenceStart <= e.Start)
        {
            DeleteEvent(id);
            return;
        }
        SaveEvent(e with { Recurrence = rule with { Count = null, Until = DateTime.SpecifyKind(occurrenceStart.AddSeconds(-1), DateTimeKind.Unspecified) } });
    }

    public TimeZoneInfo ZoneOf(CalendarEvent e) => e.AllDay ? LocalZone : TimeZones.Find(e.TimeZoneId, LocalZone);

    /// <summary>Ocorrências com início em [fromUtc, toUtc), em ordem.</summary>
    public IEnumerable<EventOccurrence> Expand(CalendarEvent e, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        ArgumentNullException.ThrowIfNull(e);
        var zone = ZoneOf(e);
        foreach (var start in RecurrenceExpander.Occurrences(e.Start, e.Recurrence, e.Exceptions, w => ZonedTime.ToUtc(w, zone)))
        {
            var startUtc = ZonedTime.ToUtc(start, zone);
            if (startUtc >= toUtc)
            {
                yield break;
            }
            if (startUtc < fromUtc)
            {
                continue;
            }
            // Fim também em horário de parede: um evento de 09:00 às 10:00 continua terminando às 10:00.
            yield return new EventOccurrence(e, start, startUtc, ZonedTime.ToUtc(start + e.Duration, zone));
        }
    }

    /// <summary>Ocorrências que se sobrepõem a [fromUtc, toUtc), ordenadas por início.</summary>
    public IReadOnlyList<EventOccurrence> OccurrencesBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var list = new List<EventOccurrence>();
        foreach (var e in Events)
        {
            // Começa antes da janela pelo tamanho do evento, para incluir o que já está em andamento.
            list.AddRange(Expand(e, fromUtc - e.Duration - TimeSpan.FromDays(1), toUtc).Where(o => o.EndUtc > fromUtc || (o.EndUtc == o.StartUtc && o.StartUtc >= fromUtc)));
        }
        return [.. list.OrderBy(o => o.StartUtc).ThenBy(o => o.Event.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>Ocorrências de um intervalo de dias locais [firstDay, lastDay].</summary>
    public IReadOnlyList<EventOccurrence> OccurrencesForDays(DateTime firstDay, DateTime lastDay) =>
        OccurrencesBetween(ZonedTime.ToUtc(firstDay.Date, LocalZone), ZonedTime.ToUtc(lastDay.Date.AddDays(1), LocalZone));

    // ---------- Agendas assinadas e importação ----------

    /// <summary>Troca os eventos de uma assinatura (somente leitura) de uma vez.</summary>
    public void ApplySubscriptionEvents(Guid subscriptionId, IReadOnlyList<CalendarEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _store.ReplaceSubscriptionEvents(subscriptionId, events);
        lock (_gate)
        {
            _events.RemoveAll(e => e.SubscriptionId == subscriptionId);
            _events.AddRange(events);
        }
        Changed();
    }

    /// <summary>Tira da memória os eventos de uma assinatura removida (o banco já os apagou em cascata).</summary>
    public void ForgetSubscriptionEvents(Guid subscriptionId)
    {
        lock (_gate)
        {
            _events.RemoveAll(e => e.SubscriptionId == subscriptionId);
        }
        Changed();
    }

    /// <summary>Importa eventos (de um .ics) como eventos locais editáveis; pula os que já existem pelo UID.</summary>
    public int ImportEvents(IEnumerable<CalendarEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var existing = Events.Where(e => !e.IsReadOnly && e.ExternalUid is not null).Select(e => e.ExternalUid!).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var e in events)
        {
            if (e.ExternalUid is { } uid && !existing.Add(uid))
            {
                continue;
            }
            var local = e with { Id = Guid.CreateVersion7(Now), SubscriptionId = null, CreatedAt = Now, UpdatedAt = Now };
            if (Validate(local) is not null)
            {
                continue;
            }
            _store.UpsertEvent(local);
            lock (_gate)
            {
                _events.Add(local);
            }
            added++;
        }
        if (added > 0)
        {
            Changed();
        }
        return added;
    }

    // ---------- Tarefas ----------

    public TaskItem NewTask() => new()
    {
        Id = Guid.CreateVersion7(Now),
        Title = "",
        TimeZoneId = LocalZone.Id,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public static string? Validate(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Title))
        {
            return "Dê um título à tarefa.";
        }
        if (value.Title.Length > MaxTitleLength || (value.Notes?.Length ?? 0) > MaxNotesLength)
        {
            return $"O título pode ter até {MaxTitleLength} caracteres.";
        }
        if (value.ReminderMinutes is { } m && (m < 0 || m > MaxReminderMinutes))
        {
            return "O aviso deve ser de 0 minutos a 4 semanas antes.";
        }
        if (value.ReminderMinutes is not null && value.Due is null)
        {
            return "Defina um prazo para receber o aviso.";
        }
        if (value.Recurrence is not null && value.Due is null)
        {
            return "Tarefas recorrentes precisam de um prazo.";
        }
        if (value.Due is { Year: < 1900 or > 2200 })
        {
            return "Data fora do intervalo suportado.";
        }
        return null;
    }

    public TaskItem SaveTask(TaskItem value)
    {
        if (Validate(value) is { } error)
        {
            throw new ArgumentException(error, nameof(value));
        }
        var due = value.Due is { } d ? DateTime.SpecifyKind(value.DueHasTime ? d : d.Date, DateTimeKind.Unspecified) : (DateTime?)null;
        var saved = value with
        {
            Title = value.Title.Trim(),
            Notes = string.IsNullOrWhiteSpace(value.Notes) ? null : value.Notes.Trim(),
            Due = due,
            DueHasTime = due is not null && value.DueHasTime,
            RecurrenceStart = value.Recurrence is null ? null : value.RecurrenceStart ?? due,
            CreatedAt = value.CreatedAt == default ? Now : value.CreatedAt,
            UpdatedAt = Now,
        };
        _store.UpsertTask(saved);
        lock (_gate)
        {
            _tasks.RemoveAll(t => t.Id == saved.Id);
            _tasks.Add(saved);
        }
        Changed();
        return saved;
    }

    /// <summary>
    /// Conclui a tarefa. Recorrente: avança para a próxima ocorrência posterior ao prazo atual que ainda não
    /// passou (uma tarefa diária esquecida por três dias vai para hoje ou amanhã, não para anteontem).
    /// </summary>
    public TaskItem? CompleteTask(Guid id)
    {
        if (FindTask(id) is not { } task || task.IsCompleted)
        {
            return null;
        }
        if (task.Recurrence is { } rule && task.Due is { } due)
        {
            var zone = TaskZone(task);
            var today = ZonedTime.ToWall(Now, zone).Date;
            var next = RecurrenceExpander.Occurrences(task.RecurrenceStart ?? due, rule, null, w => ZonedTime.ToUtc(w, zone))
                .Cast<DateTime?>()
                .FirstOrDefault(o => o > due && o >= today);
            if (next is not null)
            {
                LogTaskAdvanced(next.Value);
                return SaveTask(task with { Due = next, CompletedCount = task.CompletedCount + 1 });
            }
        }
        return SaveTask(task with { CompletedAt = Now, CompletedCount = task.CompletedCount + 1 });
    }

    public void ReopenTask(Guid id)
    {
        if (FindTask(id) is { IsCompleted: true } task)
        {
            SaveTask(task with { CompletedAt = null });
        }
    }

    public void DeleteTask(Guid id)
    {
        _store.DeleteTask(id);
        lock (_gate)
        {
            _tasks.RemoveAll(t => t.Id == id);
        }
        Changed();
    }

    public TimeZoneInfo TaskZone(TaskItem t) => t.DueHasTime ? TimeZones.Find(t.TimeZoneId, LocalZone) : LocalZone;

    /// <summary>Instante do prazo; sem horário, o fim do dia (a tarefa só está atrasada no dia seguinte).</summary>
    public DateTimeOffset? DueUtc(TaskItem t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Due is not { } due)
        {
            return null;
        }
        return t.DueHasTime ? ZonedTime.ToUtc(due, TaskZone(t)) : ZonedTime.ToUtc(due.Date.AddDays(1), LocalZone);
    }

    public bool IsOverdue(TaskItem t) => !t.IsCompleted && DueUtc(t) is { } due && due <= Now;

    // ---------- Categorias ----------

    public AgendaCategory SaveCategory(AgendaCategory value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.Name?.Trim() ?? "";
        if (name.Length is 0 or > 40)
        {
            throw new ArgumentException("O nome da categoria deve ter de 1 a 40 caracteres.", nameof(value));
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.Color ?? "", "^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException("Cor inválida.", nameof(value));
        }
        var saved = value with { Name = name };
        _store.UpsertCategory(saved);
        lock (_gate)
        {
            _categories.RemoveAll(c => c.Id == saved.Id);
            _categories.Add(saved);
        }
        Changed();
        return saved;
    }

    public void DeleteCategory(Guid id)
    {
        _store.DeleteCategory(id);
        lock (_gate)
        {
            _categories.RemoveAll(c => c.Id == id);
            _events = [.. _events.Select(e => e.CategoryId == id ? e with { CategoryId = null } : e)];
            _tasks = [.. _tasks.Select(t => t.CategoryId == id ? t with { CategoryId = null } : t)];
        }
        Changed();
    }

    private void Changed() => _bus.Publish(new AgendaChanged());

    [LoggerMessage(Level = LogLevel.Information, Message = "Agenda carregada: {Events} eventos, {Tasks} tarefas")]
    private partial void LogLoaded(int events, int tasks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tarefa recorrente concluída; próximo prazo {Next}")]
    private partial void LogTaskAdvanced(DateTime next);
}
