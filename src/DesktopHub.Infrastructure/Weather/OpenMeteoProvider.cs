using System.Globalization;
using System.Text.Json;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Weather;
using DesktopHub.Infrastructure.Net;

namespace DesktopHub.Infrastructure.Weather;

/// <summary>
/// Open-Meteo (https://open-meteo.com): previsão e busca de cidades, gratuitas e sem chave de API para uso
/// não comercial. Só são enviados o nome buscado ou as coordenadas do local escolhido — nada do usuário.
/// </summary>
public sealed class OpenMeteoProvider(HttpClient http, TimeProvider time) : IWeatherProvider
{
    private const long MaxBytes = 1024 * 1024;

    public async Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name, CancellationToken cancellationToken)
    {
        var query = (name ?? "").Trim();
        if (query.Length < 2)
        {
            return [];
        }
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language=pt&format=json";
        return ParseSearch(await GetAsync(url, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WeatherSnapshot> ForecastAsync(WeatherLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var lat = location.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = location.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,precipitation,is_day" +
            "&hourly=temperature_2m,precipitation_probability,weather_code" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
            "&timezone=auto&forecast_days=7&forecast_hours=24";
        return ParseForecast(await GetAsync(url, cancellationToken).ConfigureAwait(false), location, time.GetUtcNow());
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await HttpClients.ReadLimitedAsync(response.Content, MaxBytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    internal static IReadOnlyList<WeatherLocation> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. results.EnumerateArray().Select(r => new WeatherLocation(
            r.GetProperty("name").GetString() ?? "",
            Str(r, "admin1"),
            Str(r, "country"),
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble(),
            Str(r, "timezone"))).Where(l => l.Name.Length > 0)];
    }

    internal static WeatherSnapshot ParseForecast(string json, WeatherLocation location, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var c = root.GetProperty("current");
        var current = new CurrentWeather(
            c.GetProperty("temperature_2m").GetDouble(),
            c.GetProperty("apparent_temperature").GetDouble(),
            c.GetProperty("relative_humidity_2m").GetInt32(),
            c.GetProperty("weather_code").GetInt32(),
            c.GetProperty("wind_speed_10m").GetDouble(),
            c.GetProperty("precipitation").GetDouble(),
            c.GetProperty("is_day").GetInt32() == 1);

        var h = root.GetProperty("hourly");
        var hourTimes = h.GetProperty("time").EnumerateArray().Select(t => Local(t.GetString())).ToList();
        var hourTemps = h.GetProperty("temperature_2m").EnumerateArray().ToList();
        var hourProb = h.GetProperty("precipitation_probability").EnumerateArray().ToList();
        var hourCodes = h.GetProperty("weather_code").EnumerateArray().ToList();
        var hourly = hourTimes.Select((t, i) => new HourlyWeather(t!.Value, hourTemps[i].GetDouble(), NullableInt(hourProb[i]), hourCodes[i].GetInt32())).ToList();

        var d = root.GetProperty("daily");
        var days = d.GetProperty("time").EnumerateArray().Select(t => DateOnly.ParseExact(t.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        var codes = d.GetProperty("weather_code").EnumerateArray().ToList();
        var max = d.GetProperty("temperature_2m_max").EnumerateArray().ToList();
        var min = d.GetProperty("temperature_2m_min").EnumerateArray().ToList();
        var prob = d.GetProperty("precipitation_probability_max").EnumerateArray().ToList();
        var sunrise = d.GetProperty("sunrise").EnumerateArray().ToList();
        var sunset = d.GetProperty("sunset").EnumerateArray().ToList();
        var daily = days.Select((day, i) => new DailyWeather(day, codes[i].GetInt32(), max[i].GetDouble(), min[i].GetDouble(),
            NullableInt(prob[i]), Local(sunrise[i].GetString()), Local(sunset[i].GetString()))).ToList();

        var zone = Str(root, "timezone") ?? location.TimeZone;
        return new WeatherSnapshot(location with { TimeZone = zone }, current, hourly, daily, fetchedAt, "Open-Meteo");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : null;

    /// <summary>Horários vêm no fuso do local ("2026-11-06T16:45"), sem deslocamento.</summary>
    private static DateTime? Local(string? text) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
