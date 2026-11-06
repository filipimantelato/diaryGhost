using DesktopHub.Core.Agenda;
using DesktopHub.Core.Notifications;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DesktopHub.Infrastructure.Agenda;

public sealed class SqliteReminderLog(SqliteDatabase database) : IReminderLog
{
    private const string WatermarkKey = "reminders_evaluated_until";

    public ReminderLogEntry? Find(string key) =>
        Query("SELECT * FROM reminder_log WHERE key = $key;", c => c.Parameters.AddWithValue("$key", key)).FirstOrDefault();

    public IReadOnlySet<string> ExistingKeys(IEnumerable<string> keys)
    {
        var wanted = keys.Distinct().ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return found;
        }
        using var connection = database.OpenConnection();
        // Lotes para respeitar o limite de parâmetros do SQLite.
        foreach (var chunk in wanted.Chunk(200))
        {
            using var cmd = connection.CreateCommand();
            var names = chunk.Select((_, i) => "$k" + i).ToList();
            cmd.CommandText = $"SELECT key FROM reminder_log WHERE key IN ({string.Join(',', names)});";
            for (var i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                found.Add(reader.GetString(0));
            }
        }
        return found;
    }

    public void Upsert(ReminderLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reminder_log (key, source, source_id, title, anchor_ms, fire_at_ms, state, snoozed_until_ms, outcome, late, updated_at_ms)
            VALUES ($key, $source, $sourceId, $title, $anchor, $fire, $state, $snoozed, $outcome, $late, $updated)
            ON CONFLICT(key) DO UPDATE SET title = excluded.title, state = excluded.state, snoozed_until_ms = excluded.snoozed_until_ms,
                outcome = COALESCE(excluded.outcome, reminder_log.outcome), late = excluded.late, updated_at_ms = excluded.updated_at_ms;
            """;
        cmd.Parameters.AddWithValue("$key", entry.Key);
        cmd.Parameters.AddWithValue("$source", entry.Source.ToString());
        cmd.Parameters.AddWithValue("$sourceId", entry.SourceId.ToString());
        cmd.Parameters.AddWithValue("$title", entry.Title);
        cmd.Parameters.AddWithValue("$anchor", entry.Anchor.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$fire", entry.FireAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$state", entry.State.ToString());
        cmd.Parameters.AddWithValue("$snoozed", entry.SnoozedUntil is { } s ? s.ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", entry.Outcome is { } o ? o.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$late", entry.Late ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ReminderLogEntry> SnoozedDueBy(DateTimeOffset at) =>
        Query("SELECT * FROM reminder_log WHERE state = 'Snoozed' AND snoozed_until_ms <= $at ORDER BY snoozed_until_ms;",
            c => c.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds()));

    public IReadOnlyList<ReminderLogEntry> Snoozed() =>
        Query("SELECT * FROM reminder_log WHERE state = 'Snoozed' ORDER BY snoozed_until_ms;", _ => { });

    public IReadOnlyList<ReminderLogEntry> Recent(int count) =>
        Query("SELECT * FROM reminder_log WHERE state <> 'Skipped' ORDER BY updated_at_ms DESC LIMIT $n;", c => c.Parameters.AddWithValue("$n", count));

    public int PruneBefore(DateTimeOffset cutoff)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        // Adiados pendentes nunca são apagados.
        cmd.CommandText = "DELETE FROM reminder_log WHERE updated_at_ms < $cutoff AND state <> 'Snoozed';";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }

    public DateTimeOffset? Watermark
    {
        get
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value_ms FROM agenda_meta WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", WatermarkKey);
            return cmd.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
        set
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM agenda_meta WHERE key = $key;";
            }
            else
            {
                cmd.CommandText = "INSERT INTO agenda_meta (key, value_ms) VALUES ($key, $v) ON CONFLICT(key) DO UPDATE SET value_ms = excluded.value_ms;";
                cmd.Parameters.AddWithValue("$v", value.Value.ToUnixTimeMilliseconds());
            }
            cmd.Parameters.AddWithValue("$key", WatermarkKey);
            cmd.ExecuteNonQuery();
        }
    }

    private List<ReminderLogEntry> Query(string sql, Action<SqliteCommand> bind)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<ReminderLogEntry>();
        while (r.Read())
        {
            var snoozed = r.GetOrdinal("snoozed_until_ms");
            var outcome = r.GetOrdinal("outcome");
            list.Add(new ReminderLogEntry(
                r.GetString(r.GetOrdinal("key")),
                Enum.Parse<ReminderSource>(r.GetString(r.GetOrdinal("source"))),
                Guid.Parse(r.GetString(r.GetOrdinal("source_id"))),
                r.GetString(r.GetOrdinal("title")),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("anchor_ms"))),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("fire_at_ms"))),
                Enum.Parse<ReminderState>(r.GetString(r.GetOrdinal("state"))),
                r.IsDBNull(snoozed) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(snoozed)),
                r.IsDBNull(outcome) ? null : Enum.TryParse<DeliveryOutcome>(r.GetString(outcome), out var o) ? o : null,
                r.GetInt64(r.GetOrdinal("late")) == 1,
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("updated_at_ms")))));
        }
        return list;
    }
}
