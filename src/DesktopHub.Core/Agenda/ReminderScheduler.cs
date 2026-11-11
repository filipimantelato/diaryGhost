using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Agenda;

/// <summary>
/// Entrega os avisos de eventos e tarefas. Não há varredura periódica: um único timer dorme até o próximo
/// aviso (no máximo <see cref="MaxSleep"/>, por segurança). Cada avaliação cobre o intervalo desde a última
/// (a "marca d'água", persistida): o que venceu com o app fechado ou o PC suspenso é entregue ao voltar,
/// marcado como atrasado; muitos atrasados viram um resumo. Avisos entregues ficam registrados pela chave
/// estável, então nada se repete após reiniciar.
/// </summary>
public sealed partial class ReminderScheduler : IDisposable
{
    public const string TagPrefix = "reminder:";
    public const string MissedTag = "reminder-missed";
    public static readonly TimeSpan LateThreshold = TimeSpan.FromMinutes(1);
    /// <summary>Atrasados mais antigos que isto são ignorados (ficar 3 semanas fora não gera uma enxurrada).</summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(6);
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(60);
    public static readonly IReadOnlyList<int> SnoozeMinutes = [5, 10, 30, 60];
    /// <summary>Acima disto, os atrasados são entregues num único resumo.</summary>
    public const int MaxIndividualLate = 3;

    private readonly AgendaService _agenda;
    private readonly IReminderLog _log;
    private readonly NotificationCenter _notifications;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<ReminderScheduler> _logger;
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IReadOnlyList<IReminderSource> _sources;
    private ITimer? _timer;
    private bool _disposed;

    public ReminderScheduler(AgendaService agenda, IReminderLog log, NotificationCenter notifications, IEventBus bus,
        TimeProvider time, ILogger<ReminderScheduler> logger, IEnumerable<IReminderSource>? sources = null)
    {
        _sources = [.. sources ?? []];
        _agenda = agenda;
        _log = log;
        _notifications = notifications;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>Próximo despertar agendado (para diagnóstico e testes).</summary>
    public DateTimeOffset? NextWakeUp { get; private set; }

    public void Start()
    {
        _log.PruneBefore(_time.GetUtcNow() - LogRetention);
        _subscriptions.Add(_bus.Subscribe<AgendaChanged>(_ => Evaluate()));
        _subscriptions.Add(_bus.Subscribe<NotificationActivated>(OnActivated));
        foreach (var source in _sources)
        {
            _subscriptions.Add(source.Watch(Evaluate));
        }
        Evaluate();
    }

    /// <summary>Reavalia: ao voltar da suspensão, quando o relógio/fuso muda e quando a agenda muda.</summary>
    public void Reschedule() => Evaluate();

    // ---------- Ações ----------

    public void Snooze(string key, TimeSpan duration)
    {
        if (_log.Find(key) is not { } entry || duration <= TimeSpan.Zero)
        {
            return;
        }
        var until = _time.GetUtcNow() + duration;
        _log.Upsert(entry with { State = ReminderState.Snoozed, SnoozedUntil = until, UpdatedAt = _time.GetUtcNow() });
        LogSnoozed(duration);
        Evaluate();
    }

    public void Dismiss(string key)
    {
        if (_log.Find(key) is { } entry)
        {
            _log.Upsert(entry with { State = ReminderState.Dismissed, SnoozedUntil = null, UpdatedAt = _time.GetUtcNow() });
            Evaluate();
        }
    }

    public void CompleteTask(string key)
    {
        if (_log.Find(key) is not { Source: ReminderSource.Task } entry)
        {
            return;
        }
        _log.Upsert(entry with { State = ReminderState.Completed, SnoozedUntil = null, UpdatedAt = _time.GetUtcNow() });
        _agenda.CompleteTask(entry.SourceId); // publica AgendaChanged → reavalia
    }

    public IReadOnlyList<ReminderLogEntry> Recent(int count) => _log.Recent(count);

    public IReadOnlyList<ReminderLogEntry> Snoozed() => _log.Snoozed();

    /// <summary>Próximos avisos (ainda não entregues) em até <paramref name="horizon"/>, incluindo adiados.</summary>
    public IReadOnlyList<ReminderTrigger> Upcoming(TimeSpan horizon)
    {
        var now = _time.GetUtcNow();
        var rule = TriggersBetween(now, now + horizon);
        var done = _log.ExistingKeys(rule.Select(t => t.Key));
        var snoozed = _log.Snoozed()
            .Where(s => s.SnoozedUntil is { } u && u <= now + horizon)
            .Select(s => new ReminderTrigger(s.Key, s.Source, s.SourceId, s.Title, s.Anchor, s.SnoozedUntil!.Value, 0, false, s.UpdatedAt, Snoozed: true));
        return [.. rule.Where(t => !done.Contains(t.Key)).Concat(snoozed).OrderBy(t => t.FireAt)];
    }

    // ---------- Núcleo ----------

    /// <summary>Avisos calculados a partir de eventos e tarefas com horário de aviso em (fromExclusive, toInclusive].</summary>
    /// <param name="updatedSince">Se informado, considera só origens criadas/editadas a partir deste instante.</param>
    public IReadOnlyList<ReminderTrigger> TriggersBetween(DateTimeOffset fromExclusive, DateTimeOffset toInclusive, DateTimeOffset? updatedSince = null)
    {
        var list = new List<ReminderTrigger>();
        if (toInclusive <= fromExclusive)
        {
            return list;
        }
        foreach (var e in _agenda.Events)
        {
            if (e.ReminderMinutes.Count == 0 || e.UpdatedAt < updatedSince)
            {
                continue;
            }
            var maxBefore = TimeSpan.FromMinutes(e.ReminderMinutes.Max());
            // Inícios cujo aviso pode cair na janela (+1 dia de folga para a âncora das 09:00 do dia inteiro).
            foreach (var o in _agenda.Expand(e, fromExclusive - TimeSpan.FromDays(1), toInclusive + maxBefore + TimeSpan.FromDays(1)))
            {
                var anchor = e.AllDay ? ZonedTime.ToUtc(o.Start.Date + AgendaService.AllDayReminderTime, _agenda.LocalZone) : o.StartUtc;
                foreach (var minutes in e.ReminderMinutes)
                {
                    var fire = anchor - TimeSpan.FromMinutes(minutes);
                    if (fire > fromExclusive && fire <= toInclusive)
                    {
                        list.Add(new ReminderTrigger(ReminderTrigger.MakeKey(ReminderSource.Event, e.Id, anchor, minutes),
                            ReminderSource.Event, e.Id, e.Title, anchor, fire, minutes, e.AllDay, e.UpdatedAt));
                    }
                }
            }
        }
        foreach (var t in _agenda.Tasks)
        {
            if (t.IsCompleted || t.ReminderMinutes is not { } minutes || t.Due is not { } due || t.UpdatedAt < updatedSince)
            {
                continue;
            }
            var anchor = t.DueHasTime ? ZonedTime.ToUtc(due, _agenda.TaskZone(t)) : ZonedTime.ToUtc(due.Date + AgendaService.AllDayReminderTime, _agenda.LocalZone);
            var fire = anchor - TimeSpan.FromMinutes(minutes);
            if (fire > fromExclusive && fire <= toInclusive)
            {
                list.Add(new ReminderTrigger(ReminderTrigger.MakeKey(ReminderSource.Task, t.Id, anchor, minutes),
                    ReminderSource.Task, t.Id, t.Title, anchor, fire, minutes, !t.DueHasTime, t.UpdatedAt));
            }
        }
        foreach (var source in _sources)
        {
            list.AddRange(source.TriggersBetween(fromExclusive, toInclusive, updatedSince));
        }
        return [.. list.OrderBy(t => t.FireAt)];
    }

    private void Evaluate()
    {
        var deliveries = new List<(ReminderTrigger Trigger, bool Late)>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var now = _time.GetUtcNow();
            var watermark = _log.Watermark;
            // Primeira execução: nada do passado. Depois: tudo desde a última avaliação, limitado a 7 dias.
            var from = watermark is { } w ? (w < now - MaxCatchUp ? now - MaxCatchUp : w) : now;
            if (from > now)
            {
                // O relógio voltou: reavalia só daqui em diante, sem repetir (as chaves já registradas protegem).
                from = now;
            }

            var due = TriggersBetween(from, now).ToList();
            if (watermark is { } previous)
            {
                // Criados ou editados desde a última avaliação com aviso que já "passou" (ex.: reunião daqui a
                // 5 min com aviso de 10 min): avisa agora, se o compromisso ainda não começou.
                due.AddRange(TriggersBetween(now - TimeSpan.FromMinutes(AgendaService.MaxReminderMinutes), from, updatedSince: previous)
                    .Where(t => t.Anchor > now));
            }
            var existing = _log.ExistingKeys(due.Select(t => t.Key));
            due.RemoveAll(t => existing.Contains(t.Key));
            due.AddRange(_log.SnoozedDueBy(now).Select(s =>
                new ReminderTrigger(s.Key, s.Source, s.SourceId, CurrentTitle(s), s.Anchor, s.SnoozedUntil ?? now, 0, false, s.UpdatedAt, Snoozed: true)));

            foreach (var t in due)
            {
                var createdLate = t.SourceUpdatedAt > t.FireAt && !t.Snoozed;
                if (createdLate && t.Anchor <= now)
                {
                    // Criado/editado depois do horário do aviso e o compromisso já começou: nada a avisar.
                    Record(t, ReminderState.Skipped, null, late: false, now);
                    continue;
                }
                var late = !createdLate && now - t.FireAt > LateThreshold;
                // Registrado já aqui, dentro do lock: uma avaliação concorrente não entrega de novo.
                Record(t, ReminderState.Fired, null, late, now);
                deliveries.Add((t, late));
            }
            _log.Watermark = now;
            ScheduleNext(now);
        }

        if (deliveries.Count > 0)
        {
            Deliver(deliveries);
            _bus.Publish(new RemindersChanged());
        }
    }

    private string CurrentTitle(ReminderLogEntry s) => s.Source switch
    {
        ReminderSource.Event => _agenda.FindEvent(s.SourceId)?.Title ?? s.Title,
        ReminderSource.Task => _agenda.FindTask(s.SourceId)?.Title ?? s.Title,
        _ => SourceFor(s.Source)?.CurrentTitle(s.SourceId) ?? s.Title,
    };

    private IReminderSource? SourceFor(ReminderSource source) => _sources.FirstOrDefault(x => x.Source == source);

    private void ScheduleNext(DateTimeOffset now)
    {
        _timer?.Dispose();
        _timer = null;
        var next = TriggersBetween(now, now + MaxSleep).Select(t => (DateTimeOffset?)t.FireAt).FirstOrDefault();
        var snooze = _log.Snoozed().Select(s => s.SnoozedUntil).Where(u => u > now).Min();
        var wake = new[] { next, snooze, now + MaxSleep }.Where(d => d is not null).Min()!.Value;
        NextWakeUp = wake;
        // +50 ms: acorda logo depois do horário, não um instante antes.
        _timer = _time.CreateTimer(_ => Evaluate(), null, wake - now + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    private void Deliver(List<(ReminderTrigger Trigger, bool Late)> deliveries)
    {
        var now = _time.GetUtcNow();
        var late = deliveries.Where(d => d.Late).ToList();
        var onTime = deliveries.Where(d => !d.Late).ToList();
        if (late.Count > MaxIndividualLate)
        {
            // Resumo único em vez de uma rajada de toasts.
            var titles = late.Select(d => d.Trigger.Title).Distinct().ToList();
            var body = string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? $" e mais {titles.Count - 3}" : "") + ".";
            var outcome = _notifications.Notify(new NotificationRequest(NotificationCategory.Reminder, NotificationPriority.High,
                $"{late.Count} lembretes enquanto o app estava fechado ou o PC suspenso", body, MissedTag,
                DedupKey: $"missed:{now.ToUnixTimeSeconds() / 60}"));
            late.ForEach(d => Record(d.Trigger, ReminderState.Fired, outcome, late: true, now));
            late.Clear();
        }
        foreach (var (trigger, isLate) in onTime.Concat(late))
        {
            var outcome = _notifications.Notify(BuildRequest(trigger, isLate, now));
            Record(trigger, ReminderState.Fired, outcome, isLate, now);
            LogDelivered(trigger.Source, isLate, outcome);
        }
    }

    internal NotificationRequest BuildRequest(ReminderTrigger t, bool late, DateTimeOffset now)
    {
        var zone = _agenda.LocalZone;
        var today = ZonedTime.ToWall(now, zone).Date;
        var anchorWall = ZonedTime.ToWall(t.Anchor, zone);
        string body;
        if (SourceFor(t.Source) is { } external)
        {
            body = external.Body(t, now);
        }
        else if (t.Source == ReminderSource.Event)
        {
            var e = _agenda.FindEvent(t.SourceId);
            body = t.AllDay
                ? $"{AgendaFormat.Capitalize(AgendaFormat.Day(anchorWall, today))}, o dia todo"
                : t.Anchor > now
                    ? $"Começa em {AgendaFormat.Span(t.Anchor - now)} · {AgendaFormat.Time(anchorWall)}"
                    : $"Começou às {AgendaFormat.Time(anchorWall)}" + (AgendaFormat.Day(anchorWall, today) is var d && d != "hoje" ? $" ({d})" : "");
            if (e?.Location is { } location)
            {
                body += $" · {location}";
            }
        }
        else
        {
            var task = _agenda.FindTask(t.SourceId);
            var day = AgendaFormat.Day(anchorWall, today);
            body = t.AllDay
                ? $"Prazo: {day}"
                : t.Anchor > now ? $"Prazo: {day} às {AgendaFormat.Time(anchorWall)} (em {AgendaFormat.Span(t.Anchor - now)})" : $"Venceu {day} às {AgendaFormat.Time(anchorWall)}";
            if (task is { Priority: TaskPriority.High or TaskPriority.Urgent })
            {
                body += $" · prioridade {AgendaFormat.Priority(task.Priority).ToLower(AgendaFormat.PtBr)}";
            }
        }
        if (t.Snoozed)
        {
            body = "Adiado · " + body;
        }
        if (late)
        {
            body += $" · atrasado: o aviso era às {AgendaFormat.Time(ZonedTime.ToWall(t.FireAt, zone))}, com o app fechado ou o PC suspenso";
        }

        IReadOnlyList<NotificationAction> actions = t.Source == ReminderSource.Task
            ? [new("snooze", "Adiar"), new("done", "Concluir")]
            : [new("snooze", "Adiar")];
        return new NotificationRequest(
            t.Source == ReminderSource.Event ? NotificationCategory.CalendarEvent : NotificationCategory.Reminder,
            NotificationPriority.High,
            t.Title,
            body,
            TagPrefix + t.Key,
            DedupKey: $"{t.Key}@{t.FireAt.ToUnixTimeSeconds()}",
            Actions: actions,
            Selection: new NotificationSelection("minutes", "10", [.. SnoozeMinutes.Select(m => new NotificationAction(m.ToString(AgendaFormat.PtBr), m < 60 ? $"{m} minutos" : "1 hora"))]));
    }

    private void Record(ReminderTrigger t, ReminderState state, DeliveryOutcome? outcome, bool late, DateTimeOffset now) =>
        _log.Upsert(new ReminderLogEntry(t.Key, t.Source, t.SourceId, t.Title, t.Anchor, t.FireAt, state, null, outcome, late, now));

    private void OnActivated(NotificationActivated e)
    {
        if (e.Tag == MissedTag)
        {
            _bus.Publish(new OpenMainRequested("reminders"));
            return;
        }
        if (!e.Tag.StartsWith(TagPrefix, StringComparison.Ordinal))
        {
            return;
        }
        var key = e.Tag[TagPrefix.Length..];
        switch (e.ActionId?.Split(':'))
        {
            case ["snooze", var value] when int.TryParse(value, out var minutes) && SnoozeMinutes.Contains(minutes):
                Snooze(key, TimeSpan.FromMinutes(minutes));
                break;
            case ["snooze"]:
                Snooze(key, TimeSpan.FromMinutes(10));
                break;
            case ["done", ..]:
                CompleteTask(key);
                break;
            case null:
                var page = _sources.FirstOrDefault(s => key.StartsWith(s.Source + ":", StringComparison.Ordinal))?.Page
                    ?? (key.StartsWith(nameof(ReminderSource.Task), StringComparison.Ordinal) ? "tasks" : "calendar");
                _bus.Publish(new OpenMainRequested(page));
                break;
        }
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Lembrete entregue: {Source} (atrasado: {Late}) → {Outcome}")]
    private partial void LogDelivered(ReminderSource source, bool late, DeliveryOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lembrete adiado por {Duration}")]
    private partial void LogSnoozed(TimeSpan duration);
}
