using System.Text.Json;
using DesktopHub.Core.Weather;
using DesktopHub.Infrastructure.Persistence;

namespace DesktopHub.Infrastructure.Weather;

/// <summary>Última previsão em JSON na tabela <c>cache</c>; conteúdo ilegível é ignorado.</summary>
public sealed class SqliteWeatherCache(SqliteDatabase database) : IWeatherCache
{
    private const string Key = "weather.last";

    public WeatherSnapshot? Load()
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM cache WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", Key);
        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WeatherSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public void Save(WeatherSnapshot snapshot)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO cache (key, value_json, updated_at_ms) VALUES ($key, $json, $now) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at_ms = excluded.updated_at_ms;";
        cmd.Parameters.AddWithValue("$key", Key);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot));
        cmd.Parameters.AddWithValue("$now", snapshot.FetchedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }
}
