using System.Globalization;
using DesktopHub.Core.Agenda;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Agenda;

/// <summary>Eventos, tarefas e categorias no SQLite. Linhas ilegíveis são ignoradas e registradas, nunca derrubam o app.</summary>
public sealed partial class SqliteAgendaStore(SqliteDatabase database, ILogger<SqliteAgendaStore> logger) : IAgendaStore
{
    private const string WallFormat = "yyyy-MM-dd'T'HH:mm:ss";

    internal static string Wall(DateTime value) => value.ToString(WallFormat, CultureInfo.InvariantCulture);

    internal static DateTime ParseWall(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, WallFormat, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static object Db(long? value) => value is null ? DBNull.Value : value.Value;

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? Long(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static DateTimeOffset Ms(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    // ---------- Eventos ----------

    public IReadOnlyList<CalendarEvent> LoadEvents() => Read("SELECT * FROM calendar_events;", r => new CalendarEvent
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Title = r.GetString(r.GetOrdinal("title")),
        Start = ParseWall(r.GetString(r.GetOrdinal("start_local"))),
        Duration = TimeSpan.FromMilliseconds(r.GetInt64(r.GetOrdinal("duration_ms"))),
        AllDay = r.GetInt64(r.GetOrdinal("all_day")) == 1,
        TimeZoneId = r.GetString(r.GetOrdinal("time_zone")),
        Recurrence = Text(r, "rrule") is { } rule ? RecurrenceRule.Parse(rule) : null,
        Exceptions = Text(r, "exdates") is { Length: > 0 } ex ? [.. ex.Split(',').Select(ParseWall)] : [],
        CategoryId = Text(r, "category_id") is { } c ? Guid.Parse(c) : null,
        Location = Text(r, "location"),
        Notes = Text(r, "notes"),
        ReminderMinutes = Text(r, "reminders") is { Length: > 0 } rem ? [.. rem.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))] : [],
        CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
        UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
        SubscriptionId = Text(r, "subscription_id") is { } sub ? Guid.Parse(sub) : null,
        ExternalUid = Text(r, "external_uid"),
    });

    public void UpsertEvent(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Execute("""
            INSERT INTO calendar_events (id, title, start_local, duration_ms, all_day, time_zone, rrule, exdates, category_id, location, notes, reminders, created_at_ms, updated_at_ms, subscription_id, external_uid)
            VALUES ($id, $title, $start, $duration, $allDay, $tz, $rrule, $exdates, $category, $location, $notes, $reminders, $created, $updated, $subscription, $uid)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, start_local = excluded.start_local, duration_ms = excluded.duration_ms,
                all_day = excluded.all_day, time_zone = excluded.time_zone, rrule = excluded.rrule, exdates = excluded.exdates,
                category_id = excluded.category_id, location = excluded.location, notes = excluded.notes, reminders = excluded.reminders,
                updated_at_ms = excluded.updated_at_ms;
            """, p =>
        {
            p.AddWithValue("$id", value.Id.ToString());
            p.AddWithValue("$title", value.Title);
            p.AddWithValue("$start", Wall(value.Start));
            p.AddWithValue("$duration", (long)value.Duration.TotalMilliseconds);
            p.AddWithValue("$allDay", value.AllDay ? 1 : 0);
            p.AddWithValue("$tz", value.TimeZoneId);
            p.AddWithValue("$rrule", Db(value.Recurrence?.ToString()));
            p.AddWithValue("$exdates", Db(value.Exceptions.Count == 0 ? null : string.Join(',', value.Exceptions.Select(Wall))));
            p.AddWithValue("$category", Db(value.CategoryId?.ToString()));
            p.AddWithValue("$location", Db(value.Location));
            p.AddWithValue("$notes", Db(value.Notes));
            p.AddWithValue("$reminders", Db(value.ReminderMinutes.Count == 0 ? null : string.Join(',', value.ReminderMinutes.Select(m => m.ToString(CultureInfo.InvariantCulture)))));
            p.AddWithValue("$created", value.CreatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$updated", value.UpdatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$subscription", Db(value.SubscriptionId?.ToString()));
            p.AddWithValue("$uid", Db(value.ExternalUid));
        });
    }

    public void DeleteEvent(Guid id) => Execute("DELETE FROM calendar_events WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));

    // ---------- Tarefas ----------

    public IReadOnlyList<TaskItem> LoadTasks() => Read("SELECT * FROM tasks;", r => new TaskItem
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Title = r.GetString(r.GetOrdinal("title")),
        Notes = Text(r, "notes"),
        Priority = Enum.Parse<TaskPriority>(r.GetString(r.GetOrdinal("priority"))),
        CategoryId = Text(r, "category_id") is { } c ? Guid.Parse(c) : null,
        Due = Text(r, "due_local") is { } due ? ParseWall(due) : null,
        DueHasTime = r.GetInt64(r.GetOrdinal("due_has_time")) == 1,
        TimeZoneId = r.GetString(r.GetOrdinal("time_zone")),
        Recurrence = Text(r, "rrule") is { } rule ? RecurrenceRule.Parse(rule) : null,
        RecurrenceStart = Text(r, "recurrence_start") is { } rs ? ParseWall(rs) : null,
        ReminderMinutes = Long(r, "reminder_minutes") is { } m ? (int)m : null,
        CompletedAt = Long(r, "completed_at_ms") is { } done ? Ms(done) : null,
        CompletedCount = (int)r.GetInt64(r.GetOrdinal("completed_count")),
        CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
        UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
    });

    public void UpsertTask(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Execute("""
            INSERT INTO tasks (id, title, notes, priority, category_id, due_local, due_has_time, time_zone, rrule, recurrence_start, reminder_minutes, completed_at_ms, completed_count, created_at_ms, updated_at_ms)
            VALUES ($id, $title, $notes, $priority, $category, $due, $hasTime, $tz, $rrule, $rstart, $reminder, $completed, $count, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, notes = excluded.notes, priority = excluded.priority,
                category_id = excluded.category_id, due_local = excluded.due_local, due_has_time = excluded.due_has_time,
                time_zone = excluded.time_zone, rrule = excluded.rrule, recurrence_start = excluded.recurrence_start,
                reminder_minutes = excluded.reminder_minutes, completed_at_ms = excluded.completed_at_ms,
                completed_count = excluded.completed_count, updated_at_ms = excluded.updated_at_ms;
            """, p =>
        {
            p.AddWithValue("$id", value.Id.ToString());
            p.AddWithValue("$title", value.Title);
            p.AddWithValue("$notes", Db(value.Notes));
            p.AddWithValue("$priority", value.Priority.ToString());
            p.AddWithValue("$category", Db(value.CategoryId?.ToString()));
            p.AddWithValue("$due", Db(value.Due is { } d ? Wall(d) : null));
            p.AddWithValue("$hasTime", value.DueHasTime ? 1 : 0);
            p.AddWithValue("$tz", value.TimeZoneId);
            p.AddWithValue("$rrule", Db(value.Recurrence?.ToString()));
            p.AddWithValue("$rstart", Db(value.RecurrenceStart is { } rs ? Wall(rs) : null));
            p.AddWithValue("$reminder", Db(value.ReminderMinutes));
            p.AddWithValue("$completed", Db(value.CompletedAt?.ToUnixTimeMilliseconds()));
            p.AddWithValue("$count", value.CompletedCount);
            p.AddWithValue("$created", value.CreatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$updated", value.UpdatedAt.ToUnixTimeMilliseconds());
        });
    }

    public void DeleteTask(Guid id) => Execute("DELETE FROM tasks WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));

    // ---------- Categorias ----------

    public IReadOnlyList<AgendaCategory> LoadCategories() => Read("SELECT * FROM agenda_categories ORDER BY rowid;", r =>
        new AgendaCategory(Guid.Parse(r.GetString(r.GetOrdinal("id"))), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("color"))));

    public void UpsertCategory(AgendaCategory value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Execute("""
            INSERT INTO agenda_categories (id, name, color) VALUES ($id, $name, $color)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, color = excluded.color;
            """, p =>
        {
            p.AddWithValue("$id", value.Id.ToString());
            p.AddWithValue("$name", value.Name);
            p.AddWithValue("$color", value.Color);
        });
    }

    /// <summary>A chave estrangeira (ON DELETE SET NULL) desvincula eventos e tarefas.</summary>
    public void DeleteCategory(Guid id) => Execute("DELETE FROM agenda_categories WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));

    // ---------- Assinaturas (iCal) ----------

    public IReadOnlyList<CalendarSubscription> LoadSubscriptions() => Read("SELECT * FROM calendar_subscriptions ORDER BY rowid;", r => new CalendarSubscription
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Name = r.GetString(r.GetOrdinal("name")),
        CategoryId = Text(r, "category_id") is { } c ? Guid.Parse(c) : null,
        ReminderMinutes = Long(r, "reminder_minutes") is { } m ? (int)m : null,
        Enabled = r.GetInt64(r.GetOrdinal("enabled")) == 1,
        ETag = Text(r, "etag"),
        LastModified = Text(r, "last_modified"),
        LastAttemptAt = Long(r, "last_attempt_at_ms") is { } a ? Ms(a) : null,
        LastSuccessAt = Long(r, "last_success_at_ms") is { } s ? Ms(s) : null,
        LastError = Text(r, "last_error"),
        EventCount = (int)r.GetInt64(r.GetOrdinal("event_count")),
    });

    public void UpsertSubscription(CalendarSubscription value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Execute("""
            INSERT INTO calendar_subscriptions (id, name, category_id, reminder_minutes, enabled, etag, last_modified, last_attempt_at_ms, last_success_at_ms, last_error, event_count)
            VALUES ($id, $name, $category, $reminder, $enabled, $etag, $lastModified, $attempt, $success, $error, $count)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, category_id = excluded.category_id, reminder_minutes = excluded.reminder_minutes,
                enabled = excluded.enabled, etag = excluded.etag, last_modified = excluded.last_modified, last_attempt_at_ms = excluded.last_attempt_at_ms,
                last_success_at_ms = excluded.last_success_at_ms, last_error = excluded.last_error, event_count = excluded.event_count;
            """, p =>
        {
            p.AddWithValue("$id", value.Id.ToString());
            p.AddWithValue("$name", value.Name);
            p.AddWithValue("$category", Db(value.CategoryId?.ToString()));
            p.AddWithValue("$reminder", Db(value.ReminderMinutes));
            p.AddWithValue("$enabled", value.Enabled ? 1 : 0);
            p.AddWithValue("$etag", Db(value.ETag));
            p.AddWithValue("$lastModified", Db(value.LastModified));
            p.AddWithValue("$attempt", Db(value.LastAttemptAt?.ToUnixTimeMilliseconds()));
            p.AddWithValue("$success", Db(value.LastSuccessAt?.ToUnixTimeMilliseconds()));
            p.AddWithValue("$error", Db(value.LastError is { Length: > 500 } e ? e[..500] : value.LastError));
            p.AddWithValue("$count", value.EventCount);
        });
    }

    public void DeleteSubscription(Guid id) => Execute("DELETE FROM calendar_subscriptions WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));

    public void ReplaceSubscriptionEvents(Guid subscriptionId, IReadOnlyList<CalendarEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM calendar_events WHERE subscription_id = $sub;";
            delete.Parameters.AddWithValue("$sub", subscriptionId.ToString());
            delete.ExecuteNonQuery();
        }
        foreach (var e in events)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO calendar_events (id, title, start_local, duration_ms, all_day, time_zone, rrule, exdates, category_id, location, notes, reminders, created_at_ms, updated_at_ms, subscription_id, external_uid)
                VALUES ($id, $title, $start, $duration, $allDay, $tz, $rrule, $exdates, $category, $location, $notes, $reminders, $created, $updated, $subscription, $uid);
                """;
            var p = cmd.Parameters;
            p.AddWithValue("$id", e.Id.ToString());
            p.AddWithValue("$title", e.Title);
            p.AddWithValue("$start", Wall(e.Start));
            p.AddWithValue("$duration", (long)e.Duration.TotalMilliseconds);
            p.AddWithValue("$allDay", e.AllDay ? 1 : 0);
            p.AddWithValue("$tz", e.TimeZoneId);
            p.AddWithValue("$rrule", Db(e.Recurrence?.ToString()));
            p.AddWithValue("$exdates", Db(e.Exceptions.Count == 0 ? null : string.Join(',', e.Exceptions.Select(Wall))));
            p.AddWithValue("$category", Db(e.CategoryId?.ToString()));
            p.AddWithValue("$location", Db(e.Location));
            p.AddWithValue("$notes", Db(e.Notes));
            p.AddWithValue("$reminders", Db(e.ReminderMinutes.Count == 0 ? null : string.Join(',', e.ReminderMinutes.Select(m => m.ToString(CultureInfo.InvariantCulture)))));
            p.AddWithValue("$created", e.CreatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$updated", e.UpdatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$subscription", subscriptionId.ToString());
            p.AddWithValue("$uid", Db(e.ExternalUid));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------- Suporte ----------

    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> map)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            try
            {
                list.Add(map(reader));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                LogInvalidRow(ex);
            }
        }
        return list;
    }

    private void Execute(string sql, Action<SqliteParameterCollection> bind)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd.Parameters);
        cmd.ExecuteNonQuery();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registro da agenda ilegível ignorado")]
    private partial void LogInvalidRow(Exception ex);
}
