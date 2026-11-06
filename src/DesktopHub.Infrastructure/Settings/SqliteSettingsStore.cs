using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopHub.Core.Settings;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Settings;

/// <summary>
/// Cada seção de configuração é um documento JSON numa linha de <c>settings</c>.
/// Propriedades novas recebem o valor padrão do código; propriedades desconhecidas são ignoradas —
/// isso permite evoluir as preferências sem migração de esquema.
/// </summary>
public sealed partial class SqliteSettingsStore(SqliteDatabase database, TimeProvider time, ILogger<SqliteSettingsStore> logger)
    : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public T? Read<T>(string key) where T : class
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Não derruba o app: volta aos padrões e registra o motivo.
            LogInvalid(ex, key);
            return null;
        }
    }

    public void Write<T>(string key, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value_json, updated_at_ms) VALUES ($key, $json, $now)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at_ms = excluded.updated_at_ms;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuração '{Key}' inválida; usando valores padrão")]
    private partial void LogInvalid(Exception exception, string key);
}
