using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopHub.Core.Productivity;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Productivity;

public sealed partial class SqliteProductivityStore(SqliteDatabase database, ILogger<SqliteProductivityStore> logger) : IProductivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public ProductivitySnapshot? LoadActive()
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM productivity_state WHERE id = 1;";
        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ProductivitySnapshot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Estado ilegível não pode impedir o app de abrir: começa do zero e registra.
            LogInvalidState(ex);
            return null;
        }
    }

    public void SaveActive(ProductivitySnapshot snapshot)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO productivity_state (id, value_json, updated_at_ms) VALUES (1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET value_json = excluded.value_json, updated_at_ms = excluded.updated_at_ms;
            """;
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, JsonOptions));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void AddSession(FocusSession session)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO focus_sessions (id, kind, label, started_at_ms, ended_at_ms, planned_ms, actual_ms, completed)
            VALUES ($id, $kind, $label, $start, $end, $planned, $actual, $completed);
            """;
        cmd.Parameters.AddWithValue("$id", session.Id.ToString());
        cmd.Parameters.AddWithValue("$kind", session.Kind.ToString());
        cmd.Parameters.AddWithValue("$label", (object?)session.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", session.StartedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$end", Math.Max(session.EndedAt.ToUnixTimeMilliseconds(), session.StartedAt.ToUnixTimeMilliseconds()));
        cmd.Parameters.AddWithValue("$planned", session.Planned is { } p ? (long)p.TotalMilliseconds : DBNull.Value);
        cmd.Parameters.AddWithValue("$actual", (long)session.Actual.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$completed", session.Completed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FocusSession> RecentSessions(int count) =>
        Query("SELECT * FROM focus_sessions ORDER BY started_at_ms DESC LIMIT $n;", c => c.Parameters.AddWithValue("$n", count));

    public IReadOnlyList<FocusSession> SessionsSince(DateTimeOffset since) =>
        Query("SELECT * FROM focus_sessions WHERE started_at_ms >= $since ORDER BY started_at_ms;", c => c.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds()));

    private List<FocusSession> Query(string sql, Action<SqliteCommand> bind)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var list = new List<FocusSession>();
        while (reader.Read())
        {
            list.Add(new FocusSession(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Enum.Parse<SessionKind>(reader.GetString(reader.GetOrdinal("kind"))),
                reader.IsDBNull(reader.GetOrdinal("label")) ? null : reader.GetString(reader.GetOrdinal("label")),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("started_at_ms"))),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("ended_at_ms"))),
                reader.IsDBNull(reader.GetOrdinal("planned_ms")) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("planned_ms"))),
                TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("actual_ms"))),
                reader.GetInt64(reader.GetOrdinal("completed")) == 1));
        }
        return list;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Estado de produtividade salvo é inválido; iniciando do zero")]
    private partial void LogInvalidState(Exception ex);
}
