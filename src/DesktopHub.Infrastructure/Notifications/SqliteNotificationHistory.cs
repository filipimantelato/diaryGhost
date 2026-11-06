using DesktopHub.Core.Notifications;
using DesktopHub.Infrastructure.Persistence;

namespace DesktopHub.Infrastructure.Notifications;

public sealed class SqliteNotificationHistory(SqliteDatabase database) : INotificationHistory
{
    public void Add(NotificationRecord record)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notifications (id, category, priority, title, body, dedup_key, created_at_ms, outcome)
            VALUES ($id, $category, $priority, $title, $body, $dedup, $created, $outcome);
            """;
        cmd.Parameters.AddWithValue("$id", record.Id.ToString());
        cmd.Parameters.AddWithValue("$category", record.Category.ToString());
        cmd.Parameters.AddWithValue("$priority", (int)record.Priority);
        cmd.Parameters.AddWithValue("$title", record.Title);
        cmd.Parameters.AddWithValue("$body", (object?)record.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dedup", (object?)record.DedupKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", record.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$outcome", record.Outcome.ToString());
        cmd.ExecuteNonQuery();
    }

    public bool ExistsSince(string dedupKey, DateTimeOffset since)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM notifications WHERE dedup_key = $key AND created_at_ms >= $since);";
        cmd.Parameters.AddWithValue("$key", dedupKey);
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public int CountShownSince(NotificationCategory category, DateTimeOffset since)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM notifications WHERE category = $category AND created_at_ms >= $since AND outcome = 'Shown';";
        cmd.Parameters.AddWithValue("$category", category.ToString());
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<NotificationRecord> Recent(int count)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category, priority, title, body, dedup_key, created_at_ms, outcome FROM notifications ORDER BY created_at_ms DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);
        using var r = cmd.ExecuteReader();
        var list = new List<NotificationRecord>();
        while (r.Read())
        {
            list.Add(new NotificationRecord(
                Guid.Parse(r.GetString(0)),
                Enum.TryParse<NotificationCategory>(r.GetString(1), out var c) ? c : NotificationCategory.System,
                (NotificationPriority)r.GetInt64(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                Enum.TryParse<DeliveryOutcome>(r.GetString(7), out var o) ? o : DeliveryOutcome.Failed));
        }
        return list;
    }

    public int PruneBefore(DateTimeOffset cutoff)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM notifications WHERE created_at_ms < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }
}
