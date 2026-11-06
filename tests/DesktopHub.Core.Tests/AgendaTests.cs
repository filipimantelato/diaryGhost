using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

internal static class TestZones
{
    /// <summary>Fuso fixo de teste com as regras dos EUA (-5/-4): horário de verão de 08/03/2026 a 01/11/2026.</summary>
    public static TimeZoneInfo Eastern { get; } = CreateEastern();

    private static TimeZoneInfo CreateEastern()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Teste/Leste", TimeSpan.FromHours(-5), "Teste Leste", "EST", "EDT", [rule]);
    }
}

public sealed class RecurrenceTests
{
    private static List<DateTime> Take(DateTime start, string rule, int max = 20, params DateTime[] exceptions) =>
        [.. RecurrenceExpander.Occurrences(start, RecurrenceRule.Parse(rule), exceptions).Take(max)];

    private static DateTime D(int y, int m, int d, int h = 9, int min = 0) => new(y, m, d, h, min, 0);

    [Fact]
    public void Count_includes_the_start_and_excluded_dates()
    {
        var result = Take(D(2026, 1, 30), "FREQ=DAILY;COUNT=3", 20, D(2026, 1, 31));
        Assert.Equal([D(2026, 1, 30), D(2026, 2, 1)], result);
    }

    [Fact]
    public void Monthly_on_day_31_skips_short_months()
    {
        Assert.Equal([D(2026, 1, 31), D(2026, 3, 31), D(2026, 5, 31), D(2026, 7, 31)], Take(D(2026, 1, 31), "FREQ=MONTHLY;COUNT=4"));
    }

    [Fact]
    public void Last_day_of_month_uses_negative_month_day()
    {
        Assert.Equal([D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30)], Take(D(2026, 1, 31), "FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=4"));
        Assert.Equal(D(2028, 2, 29), Take(D(2028, 1, 31), "FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=2")[1]); // bissexto
    }

    [Fact]
    public void Yearly_on_february_29_only_in_leap_years()
    {
        Assert.Equal([D(2024, 2, 29), D(2028, 2, 29), D(2032, 2, 29)], Take(D(2024, 2, 29), "FREQ=YEARLY;COUNT=3"));
    }

    [Fact]
    public void Weekly_with_days_and_interval()
    {
        // 02/11/2026 é segunda.
        Assert.Equal([D(2026, 11, 2), D(2026, 11, 4), D(2026, 11, 16), D(2026, 11, 18)], Take(D(2026, 11, 2), "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4"));
    }

    [Fact]
    public void Monthly_on_last_friday_and_second_tuesday()
    {
        Assert.Equal([D(2026, 11, 27), D(2026, 12, 25), D(2027, 1, 29)], Take(D(2026, 11, 27), "FREQ=MONTHLY;BYDAY=-1FR;COUNT=3"));
        Assert.Equal([D(2026, 11, 10), D(2026, 12, 8), D(2027, 1, 12)], Take(D(2026, 11, 10), "FREQ=MONTHLY;BYDAY=2TU;COUNT=3"));
    }

    [Fact]
    public void Last_weekday_of_month_with_setpos()
    {
        Assert.Equal([D(2026, 10, 30), D(2026, 11, 30), D(2026, 12, 31)], Take(D(2026, 10, 30), "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3"));
    }

    [Fact]
    public void Until_is_inclusive_and_date_only_until_covers_the_whole_day()
    {
        Assert.Equal(3, Take(D(2026, 1, 1), "FREQ=DAILY;UNTIL=20260103T090000").Count);
        Assert.Equal(3, Take(D(2026, 1, 1, 22), "FREQ=DAILY;UNTIL=20260103").Count);
    }

    [Fact]
    public void Impossible_rule_terminates_with_only_the_start()
    {
        Assert.Equal([D(2026, 1, 10)], Take(D(2026, 1, 10), "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"));
    }

    [Fact]
    public void Infinite_rule_is_lazy()
    {
        Assert.Equal(500, RecurrenceExpander.Occurrences(D(2026, 1, 1), RecurrenceRule.Daily()).Take(500).Count());
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1")]
    [InlineData("FREQ=YEARLY;BYMONTHDAY=-1;BYMONTH=2;UNTIL=20300101T000000Z")]
    public void Rules_roundtrip(string text)
    {
        Assert.Equal(text, RecurrenceRule.Parse(text).ToString());
        Assert.Equal(text, RecurrenceRule.Parse("RRULE:" + text).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=1")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;COUNT=2;UNTIL=20260101")]
    [InlineData("FREQ=WEEKLY;BYDAY=2MO")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]
    [InlineData("FREQ=MONTHLY;BYDAY=XX")]
    [InlineData("FREQ=DAILY;BYHOUR=9")]
    public void Invalid_or_unsupported_rules_are_rejected(string text)
    {
        Assert.False(RecurrenceRule.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Description_is_readable()
    {
        Assert.Equal("Toda semana na segunda-feira e quarta-feira, 4 vezes", AgendaFormat.Describe(RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=WE,MO;COUNT=4"), D(2026, 11, 2)));
        Assert.Equal("Todo mês na última sexta-feira", AgendaFormat.Describe(RecurrenceRule.Parse("FREQ=MONTHLY;BYDAY=-1FR"), D(2026, 11, 27)));
        Assert.Equal("Todo dia útil", AgendaFormat.Describe(RecurrenceRule.Parse("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR"), D(2026, 11, 2)));
    }
}

public sealed class ZonedTimeTests
{
    [Fact]
    public void Nonexistent_time_moves_forward_by_the_gap()
    {
        // 08/03/2026 02:30 não existe (02:00 → 03:00): usa o deslocamento de antes (-5) → 07:30Z = 03:30 EDT.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), ZonedTime.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), TestZones.Eastern));
    }

    [Fact]
    public void Ambiguous_time_uses_the_first_occurrence()
    {
        // 01/11/2026 01:30 acontece duas vezes; a primeira ainda é EDT (-4) → 05:30Z.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), ZonedTime.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0), TestZones.Eastern));
    }
}

internal sealed class MemoryAgendaStore : IAgendaStore
{
    public Dictionary<Guid, CalendarEvent> EventRows { get; } = [];
    public Dictionary<Guid, TaskItem> TaskRows { get; } = [];
    public Dictionary<Guid, AgendaCategory> CategoryRows { get; } = [];
    public IReadOnlyList<CalendarEvent> LoadEvents() => [.. EventRows.Values];
    public void UpsertEvent(CalendarEvent value) => EventRows[value.Id] = value;
    public void DeleteEvent(Guid id) => EventRows.Remove(id);
    public IReadOnlyList<TaskItem> LoadTasks() => [.. TaskRows.Values];
    public void UpsertTask(TaskItem value) => TaskRows[value.Id] = value;
    public void DeleteTask(Guid id) => TaskRows.Remove(id);
    public IReadOnlyList<AgendaCategory> LoadCategories() => [.. CategoryRows.Values];
    public void UpsertCategory(AgendaCategory value) => CategoryRows[value.Id] = value;
    public void DeleteCategory(Guid id) => CategoryRows.Remove(id);
}

internal sealed class MemoryReminderLog : IReminderLog
{
    public Dictionary<string, ReminderLogEntry> Rows { get; } = [];
    public DateTimeOffset? Watermark { get; set; }
    public ReminderLogEntry? Find(string key) => Rows.GetValueOrDefault(key);
    public IReadOnlySet<string> ExistingKeys(IEnumerable<string> keys) => keys.Where(Rows.ContainsKey).ToHashSet();
    public void Upsert(ReminderLogEntry entry) => Rows[entry.Key] = entry;
    public IReadOnlyList<ReminderLogEntry> SnoozedDueBy(DateTimeOffset at) => [.. Rows.Values.Where(r => r.State == ReminderState.Snoozed && r.SnoozedUntil <= at)];
    public IReadOnlyList<ReminderLogEntry> Snoozed() => [.. Rows.Values.Where(r => r.State == ReminderState.Snoozed)];
    public IReadOnlyList<ReminderLogEntry> Recent(int count) => [.. Rows.Values.OrderByDescending(r => r.UpdatedAt).Take(count)];
    public int PruneBefore(DateTimeOffset cutoff) => 0;
}

public sealed class AgendaServiceTests
{
    // 06/11/2026 12:00 UTC = 07:00 no fuso de teste (EST, -5).
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    internal static (AgendaService Agenda, FakeTimeProvider Time, MemoryAgendaStore Store, EventBus Bus) Create(MemoryAgendaStore? store = null, FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TestZones.Eastern);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        store ??= new MemoryAgendaStore();
        var agenda = new AgendaService(store, bus, time, NullLogger<AgendaService>.Instance);
        agenda.Start();
        return (agenda, time, store, bus);
    }

    [Fact]
    public void Weekly_event_keeps_wall_clock_across_dst()
    {
        var (agenda, _, _, _) = Create();
        // Segunda 02/03/2026 09:00 EST (14:00Z); depois de 08/03 vira 13:00Z.
        agenda.SaveEvent(agenda.NewEvent(new DateTime(2026, 3, 2, 9, 0, 0)) with { Title = "Reunião", Recurrence = RecurrenceRule.Weekly(DayOfWeek.Monday) });

        var list = agenda.OccurrencesBetween(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([14, 13, 13], list.Select(o => o.StartUtc.Hour));
        Assert.All(list, o => Assert.Equal(9, o.Start.Hour));
        Assert.All(list, o => Assert.Equal(TimeSpan.FromHours(1), o.EndUtc - o.StartUtc));
    }

    [Fact]
    public void Daily_event_in_the_dst_gap_is_not_dropped()
    {
        var (agenda, _, _, _) = Create();
        agenda.SaveEvent(agenda.NewEvent(new DateTime(2026, 3, 7, 2, 30, 0)) with { Title = "Madrugada", Recurrence = RecurrenceRule.Daily() });

        var days = agenda.OccurrencesForDays(new DateTime(2026, 3, 7), new DateTime(2026, 3, 9));

        Assert.Equal(3, days.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), days[1].StartUtc); // 03:30 EDT
    }

    [Fact]
    public void Occurrence_in_progress_is_included()
    {
        var (agenda, _, _, _) = Create();
        agenda.SaveEvent(agenda.NewEvent(new DateTime(2026, 11, 6, 6, 0, 0)) with { Title = "Plantão", Duration = TimeSpan.FromHours(3) });
        Assert.Single(agenda.OccurrencesBetween(T0, T0.AddHours(1))); // começou 06:00, agora 07:00
    }

    [Fact]
    public void Deleting_one_occurrence_or_the_rest_of_the_series()
    {
        var (agenda, _, _, _) = Create();
        var e = agenda.SaveEvent(agenda.NewEvent(new DateTime(2026, 11, 2, 9, 0, 0)) with { Title = "Diário", Recurrence = RecurrenceRule.Daily() });

        agenda.DeleteOccurrence(e.Id, new DateTime(2026, 11, 3, 9, 0, 0));
        agenda.EndSeriesBefore(e.Id, new DateTime(2026, 11, 5, 9, 0, 0));

        var days = agenda.OccurrencesForDays(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30)).Select(o => o.Start.Day);
        Assert.Equal([2, 4], days);
    }

    [Fact]
    public void Completing_an_overdue_recurring_task_moves_it_to_today_not_the_past()
    {
        var (agenda, _, _, _) = Create();
        var task = agenda.SaveTask(agenda.NewTask() with
        {
            Title = "Remédio",
            Due = new DateTime(2026, 11, 3, 8, 0, 0),
            DueHasTime = true,
            Recurrence = RecurrenceRule.Daily(),
        });

        var next = agenda.CompleteTask(task.Id);

        Assert.Equal(new DateTime(2026, 11, 6, 8, 0, 0), next!.Due);
        Assert.False(next.IsCompleted);
        Assert.Equal(1, next.CompletedCount);
    }

    [Fact]
    public void Completing_a_plain_task_marks_it_done_and_reopen_undoes()
    {
        var (agenda, _, _, _) = Create();
        var task = agenda.SaveTask(agenda.NewTask() with { Title = "Comprar pão" });
        Assert.True(agenda.CompleteTask(task.Id)!.IsCompleted);
        agenda.ReopenTask(task.Id);
        Assert.False(agenda.FindTask(task.Id)!.IsCompleted);
    }

    [Fact]
    public void Date_only_task_is_overdue_only_after_its_day()
    {
        var (agenda, time, _, _) = Create();
        var task = agenda.SaveTask(agenda.NewTask() with { Title = "Hoje", Due = new DateTime(2026, 11, 6) });
        Assert.False(agenda.IsOverdue(task));
        time.Advance(TimeSpan.FromHours(17)); // 00:00 de 07/11 no fuso de teste
        Assert.True(agenda.IsOverdue(task));
    }

    [Fact]
    public void Validation_rejects_bad_input_with_a_message()
    {
        var (agenda, _, _, _) = Create();
        Assert.NotNull(AgendaService.Validate(agenda.NewEvent(new DateTime(2026, 11, 6, 9, 0, 0))));
        Assert.NotNull(AgendaService.Validate(agenda.NewEvent(new DateTime(2026, 11, 6, 9, 0, 0)) with { Title = "x", Duration = TimeSpan.FromHours(-1) }));
        Assert.NotNull(AgendaService.Validate(agenda.NewTask() with { Title = "x", ReminderMinutes = 10 }));
        Assert.Throws<ArgumentException>(() => agenda.SaveTask(agenda.NewTask()));
    }
}

public sealed class ReminderSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero); // 07:00 local

    private sealed class FakeSink : INotificationSink
    {
        public List<NotificationRequest> Shown { get; } = [];
        public NotificationChannelStatus Status => NotificationChannelStatus.Enabled;
        public event Action<Guid, string?>? Activated;
        public Guid LastId { get; private set; }

        public DeliveryOutcome Show(Guid id, NotificationRequest request)
        {
            LastId = id;
            Shown.Add(request);
            return DeliveryOutcome.Shown;
        }

        public void Click(string? action) => Activated?.Invoke(LastId, action);
    }

    private sealed class MemoryHistory : INotificationHistory
    {
        private readonly List<NotificationRecord> _records = [];
        public void Add(NotificationRecord record) => _records.Add(record);
        public bool ExistsSince(string dedupKey, DateTimeOffset since) => _records.Any(r => r.DedupKey == dedupKey && r.CreatedAt >= since);
        public int CountShownSince(NotificationCategory category, DateTimeOffset since) => _records.Count(r => r.Category == category && r.CreatedAt >= since);
        public IReadOnlyList<NotificationRecord> Recent(int count) => [.. _records.TakeLast(count)];
        public int PruneBefore(DateTimeOffset cutoff) => 0;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    /// <summary>Um "processo" do app: serviços novos sobre armazenamento persistente compartilhado.</summary>
    private sealed class Rig : IDisposable
    {
        public required AgendaService Agenda { get; init; }
        public required ReminderScheduler Scheduler { get; init; }
        public required FakeSink Sink { get; init; }
        public required FakeTimeProvider Time { get; init; }

        public void Dispose() => Scheduler.Dispose();
    }

    private static Rig Boot(FakeTimeProvider time, MemoryAgendaStore store, MemoryReminderLog log)
    {
        var (agenda, _, _, bus) = AgendaServiceTests.Create(store, time);
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        var sink = new FakeSink();
        var center = new NotificationCenter(sink, new MemoryHistory(), settings, bus, time, NullLogger<NotificationCenter>.Instance);
        var scheduler = new ReminderScheduler(agenda, log, center, bus, time, NullLogger<ReminderScheduler>.Instance);
        scheduler.Start();
        return new Rig { Agenda = agenda, Scheduler = scheduler, Sink = sink, Time = time };
    }

    private static Rig Boot() => Boot(new FakeTimeProvider(T0), new MemoryAgendaStore(), new MemoryReminderLog());

    private static CalendarEvent Meeting(AgendaService agenda, DateTime start, params int[] reminders) =>
        agenda.SaveEvent(agenda.NewEvent(start) with { Title = "Reunião", ReminderMinutes = reminders });

    [Fact]
    public void Event_reminder_fires_once_at_its_time()
    {
        using var rig = Boot();
        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 7, 30, 0), 10);

        rig.Time.Advance(TimeSpan.FromMinutes(19));
        Assert.Empty(rig.Sink.Shown);
        rig.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var shown = Assert.Single(rig.Sink.Shown);
        Assert.Equal("Reunião", shown.Title);
        Assert.StartsWith("Começa em 10 min", shown.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("atrasado", shown.Body, StringComparison.Ordinal);

        rig.Time.Advance(TimeSpan.FromHours(3));
        Assert.Single(rig.Sink.Shown);
    }

    [Fact]
    public void First_run_does_not_replay_the_past()
    {
        var store = new MemoryAgendaStore();
        var time = new FakeTimeProvider(T0);
        var (agenda, _, _, _) = AgendaServiceTests.Create(store, time);
        agenda.SaveEvent(agenda.NewEvent(new DateTime(2026, 11, 6, 6, 0, 0)) with { Title = "Antes", ReminderMinutes = [10], UpdatedAt = T0.AddDays(-1) });

        using var rig = Boot(time, store, new MemoryReminderLog());
        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void Reminder_survives_restart_and_fires()
    {
        var time = new FakeTimeProvider(T0);
        var store = new MemoryAgendaStore();
        var log = new MemoryReminderLog();
        using (var first = Boot(time, store, log))
        {
            first.Agenda.SaveTask(first.Agenda.NewTask() with { Title = "Ligar", Due = new DateTime(2026, 11, 6, 8, 0, 0), DueHasTime = true, ReminderMinutes = 0 });
        } // app encerrado

        using var second = Boot(time, store, log);
        time.Advance(TimeSpan.FromMinutes(61));
        var shown = Assert.Single(second.Sink.Shown);
        Assert.Equal("Ligar", shown.Title);
        Assert.Contains(shown.Actions!, a => a.Id == "done");
    }

    [Fact]
    public void Reminder_missed_while_closed_is_delivered_late_and_only_once()
    {
        var time = new FakeTimeProvider(T0);
        var store = new MemoryAgendaStore();
        var log = new MemoryReminderLog();
        using (var first = Boot(time, store, log))
        {
            Meeting(first.Agenda, new DateTime(2026, 11, 6, 7, 30, 0), 10);
        }

        time.Advance(TimeSpan.FromHours(2)); // PC suspenso / app fechado
        using (var second = Boot(time, store, log))
        {
            var shown = Assert.Single(second.Sink.Shown);
            Assert.Contains("atrasado", shown.Body, StringComparison.Ordinal);
            Assert.Contains("07:20", shown.Body, StringComparison.Ordinal);
            Assert.StartsWith("Começou às 07:30", shown.Body, StringComparison.Ordinal);
        }

        using var third = Boot(time, store, log);
        Assert.Empty(third.Sink.Shown);
    }

    [Fact]
    public void Reschedule_after_resume_never_duplicates()
    {
        using var rig = Boot();
        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 7, 30, 0), 10);
        rig.Time.SetUtcNow(T0.AddHours(1));
        rig.Scheduler.Reschedule(); // como no evento de retomada do Windows
        rig.Scheduler.Reschedule();
        Assert.Single(rig.Sink.Shown);
    }

    [Fact]
    public void Reminder_whose_time_already_passed_at_creation_fires_now_but_is_not_late()
    {
        using var rig = Boot();
        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 7, 5, 0), 10); // aviso seria 06:55, criado às 07:00

        var shown = Assert.Single(rig.Sink.Shown);
        Assert.DoesNotContain("atrasado", shown.Body, StringComparison.Ordinal);

        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 6, 30, 0), 10); // já começou: nada a avisar
        Assert.Single(rig.Sink.Shown);
    }

    [Fact]
    public void Snooze_from_the_notification_fires_again()
    {
        using var rig = Boot();
        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 7, 10, 0), 5);
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(rig.Sink.Shown);

        rig.Sink.Click("snooze:5");
        rig.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(rig.Sink.Shown);
        rig.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(2, rig.Sink.Shown.Count);
        Assert.StartsWith("Adiado", rig.Sink.Shown[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_from_the_notification_completes_the_task()
    {
        using var rig = Boot();
        var task = rig.Agenda.SaveTask(rig.Agenda.NewTask() with { Title = "Pagar conta", Due = new DateTime(2026, 11, 6, 7, 30, 0), DueHasTime = true, ReminderMinutes = 15 });
        rig.Time.Advance(TimeSpan.FromMinutes(16));

        rig.Sink.Click("done");

        Assert.True(rig.Agenda.FindTask(task.Id)!.IsCompleted);
    }

    [Fact]
    public void Many_missed_reminders_become_one_summary()
    {
        var time = new FakeTimeProvider(T0);
        var store = new MemoryAgendaStore();
        var log = new MemoryReminderLog();
        using (var first = Boot(time, store, log))
        {
            for (var i = 1; i <= 5; i++)
            {
                Meeting(first.Agenda, new DateTime(2026, 11, 6, 8, i * 5, 0), 0);
            }
        }

        time.Advance(TimeSpan.FromHours(4));
        using var second = Boot(time, store, log);

        var summary = Assert.Single(second.Sink.Shown);
        Assert.StartsWith("5 lembretes", summary.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_sleeps_until_the_next_reminder_without_polling()
    {
        using var rig = Boot();
        Assert.Equal(T0 + ReminderScheduler.MaxSleep, rig.Scheduler.NextWakeUp);
        Meeting(rig.Agenda, new DateTime(2026, 11, 6, 9, 0, 0), 30);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 13, 30, 0, TimeSpan.Zero), rig.Scheduler.NextWakeUp);
    }

    [Fact]
    public void All_day_event_reminds_at_nine_on_the_day()
    {
        using var rig = Boot();
        rig.Agenda.SaveEvent(rig.Agenda.NewEvent(new DateTime(2026, 11, 6), allDay: true) with { Title = "Aniversário", ReminderMinutes = [0] });
        rig.Time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1)); // 09:00 local
        var shown = Assert.Single(rig.Sink.Shown);
        Assert.Equal("Hoje, o dia todo", shown.Body);
    }
}
