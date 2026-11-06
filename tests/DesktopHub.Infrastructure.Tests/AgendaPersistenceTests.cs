using DesktopHub.Core.Agenda;
using DesktopHub.Core.Notifications;
using DesktopHub.Infrastructure.Agenda;
using DesktopHub.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Infrastructure.Tests;

public sealed class AgendaPersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_categories_are_seeded()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteAgendaStore(dir.Database, NullLogger<SqliteAgendaStore>.Instance);
        Assert.Equal(["Pessoal", "Trabalho", "Estudos", "Saúde"], store.LoadCategories().Select(c => c.Name));
    }

    [Fact]
    public void Event_roundtrips_with_rule_exceptions_and_reminders()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteAgendaStore(dir.Database, NullLogger<SqliteAgendaStore>.Instance);
        var category = store.LoadCategories()[1];
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(T0),
            Title = "Daily; DROP TABLE tasks;--",
            Start = new DateTime(2026, 11, 2, 9, 30, 0),
            Duration = TimeSpan.FromMinutes(15),
            TimeZoneId = "E. South America Standard Time",
            Recurrence = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=MO,WE,FR;UNTIL=20261231T235959"),
            Exceptions = [new DateTime(2026, 11, 4, 9, 30, 0)],
            CategoryId = category.Id,
            Location = "Sala 2",
            ReminderMinutes = [0, 10],
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        store.UpsertEvent(e);
        var loaded = Assert.Single(new SqliteAgendaStore(dir.Reopen(), NullLogger<SqliteAgendaStore>.Instance).LoadEvents());

        Assert.Equal(e.Title, loaded.Title);
        Assert.Equal(e.Start, loaded.Start);
        Assert.Equal(e.Recurrence!.ToString(), loaded.Recurrence!.ToString());
        Assert.Equal(e.Exceptions, loaded.Exceptions);
        Assert.Equal([0, 10], loaded.ReminderMinutes);
        Assert.Equal(category.Id, loaded.CategoryId);
        Assert.Equal(T0, loaded.UpdatedAt);
    }

    [Fact]
    public void Deleting_a_category_unlinks_tasks()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteAgendaStore(dir.Database, NullLogger<SqliteAgendaStore>.Instance);
        var category = store.LoadCategories()[0];
        var task = new TaskItem
        {
            Id = Guid.CreateVersion7(T0),
            Title = "Ligar",
            Priority = TaskPriority.High,
            CategoryId = category.Id,
            Due = new DateTime(2026, 11, 6, 8, 0, 0),
            DueHasTime = true,
            TimeZoneId = "UTC",
            ReminderMinutes = 0,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        store.UpsertTask(task);

        store.DeleteCategory(category.Id);

        var loaded = Assert.Single(store.LoadTasks());
        Assert.Null(loaded.CategoryId);
        Assert.Equal((TaskPriority.High, 0, true), (loaded.Priority, loaded.ReminderMinutes, loaded.DueHasTime));
    }

    [Fact]
    public void Reminder_log_tracks_state_snoozes_and_watermark()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var log = new SqliteReminderLog(dir.Database);
        Assert.Null(log.Watermark);
        var entry = new ReminderLogEntry("Event:x:1:10", ReminderSource.Event, Guid.CreateVersion7(T0), "Reunião", T0, T0.AddMinutes(-10),
            ReminderState.Fired, null, DeliveryOutcome.Shown, false, T0);
        log.Upsert(entry);
        log.Upsert(entry with { State = ReminderState.Snoozed, SnoozedUntil = T0.AddMinutes(5), Outcome = null });
        log.Watermark = T0;

        var reopened = new SqliteReminderLog(dir.Reopen());
        Assert.Equal(T0, reopened.Watermark);
        Assert.Equal(["Event:x:1:10"], reopened.ExistingKeys(["Event:x:1:10", "outra"]));
        Assert.Empty(reopened.SnoozedDueBy(T0));
        var due = Assert.Single(reopened.SnoozedDueBy(T0.AddMinutes(5)));
        Assert.Equal(DeliveryOutcome.Shown, due.Outcome); // resultado anterior preservado
        Assert.Equal(0, reopened.PruneBefore(T0.AddDays(1))); // adiados pendentes não são apagados
    }

    [Fact]
    public void Toast_selection_is_escaped_and_placed_before_actions()
    {
        var request = new NotificationRequest(NotificationCategory.Reminder, NotificationPriority.High, "Tarefa", null, "reminder:x",
            Actions: [new("snooze", "Adiar")],
            Selection: new NotificationSelection("minutes", "10", [new("5", "5 \"min\""), new("10", "<10>")]));

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(WindowsToastSink.BuildXml(Guid.Empty, request));

        var actions = doc.GetElementsByTagName("actions")[0]!;
        Assert.Equal("input", actions.FirstChild!.Name);
        Assert.Equal(2, doc.GetElementsByTagName("selection").Count);
        Assert.Equal("<10>", doc.GetElementsByTagName("selection")[1]!.Attributes!["content"]!.Value);
    }
}
