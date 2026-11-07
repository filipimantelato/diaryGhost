using DesktopHub.Core.Activity;
using DesktopHub.Infrastructure.Persistence;

namespace DesktopHub.Infrastructure.Activity;

public sealed class SqliteActivityStore(SqliteDatabase database) : IActivityStore
{
    public void Add(ActivityEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity_events (id, source, kind, project, session, message, received_at_ms)
            VALUES ($id, $source, $kind, $project, $session, $message, $at);
            """;
        cmd.Parameters.AddWithValue("$id", value.Id.ToString());
        cmd.Parameters.AddWithValue("$source", value.Source);
        cmd.Parameters.AddWithValue("$kind", value.Kind.ToString());
        cmd.Parameters.AddWithValue("$project", (object?)value.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", (object?)value.Session ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message", (object?)value.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", value.ReceivedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ActivityEvent> Recent(int count)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM activity_events ORDER BY received_at_ms DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);
        using var r = cmd.ExecuteReader();
        var list = new List<ActivityEvent>();
        string? Text(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
        while (r.Read())
        {
            list.Add(new ActivityEvent(
                Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                r.GetString(r.GetOrdinal("source")),
                Enum.TryParse<ActivityKind>(r.GetString(r.GetOrdinal("kind")), out var kind) ? kind : ActivityKind.Other,
                Text("project"), Text("session"), Text("message"),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("received_at_ms")))));
        }
        return list;
    }

    public int PruneBefore(DateTimeOffset cutoff)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM activity_events WHERE received_at_ms < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }
}
