using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;

namespace DesktopHub.Core.Weather;

public sealed record CurrentWeather(double Temperature, double ApparentTemperature, int Humidity, int Code, double WindKmh, double PrecipitationMm, bool IsDay);

public sealed record HourlyWeather(DateTime LocalTime, double Temperature, int? PrecipitationProbability, int Code);

public sealed record DailyWeather(DateOnly Date, int Code, double Max, double Min, int? PrecipitationProbability, DateTime? Sunrise, DateTime? Sunset);

/// <summary>Previsão como veio do provedor, com o horário em que foi consultada (exibido sempre).</summary>
public sealed record WeatherSnapshot(
    WeatherLocation Location,
    CurrentWeather Current,
    IReadOnlyList<HourlyWeather> Hourly,
    IReadOnlyList<DailyWeather> Daily,
    DateTimeOffset FetchedAt,
    string Provider);

/// <summary>Porta: provedor de previsão e de busca de cidades (Open-Meteo na Infrastructure).</summary>
public interface IWeatherProvider
{
    Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name, CancellationToken cancellationToken);
    Task<WeatherSnapshot> ForecastAsync(WeatherLocation location, CancellationToken cancellationToken);
}

/// <summary>Porta: última previsão guardada, para mostrar sem internet.</summary>
public interface IWeatherCache
{
    WeatherSnapshot? Load();
    void Save(WeatherSnapshot snapshot);
}

public sealed record WeatherUpdated(WeatherSnapshot? Snapshot, string? Error) : IAppEvent;

/// <summary>Códigos WMO usados pelo Open-Meteo → texto em pt-BR e ícone (Segoe Fluent Icons).</summary>
public static class WeatherCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "Céu limpo",
        1 => "Predominantemente limpo",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Neblina",
        51 or 53 or 55 => "Garoa",
        56 or 57 => "Garoa congelante",
        61 => "Chuva fraca",
        63 => "Chuva moderada",
        65 => "Chuva forte",
        66 or 67 => "Chuva congelante",
        71 or 73 or 75 or 77 => "Neve",
        80 => "Pancadas de chuva fracas",
        81 => "Pancadas de chuva",
        82 => "Pancadas de chuva fortes",
        85 or 86 => "Pancadas de neve",
        95 => "Trovoadas",
        96 or 99 => "Trovoadas com granizo",
        _ => "Condição desconhecida",
    };

    public static string Glyph(int code, bool isDay) => code switch
    {
        0 => isDay ? "" : "",           // Sunny / ClearNight
        1 or 2 => isDay ? "" : "",      // PartlyCloudyDay / Night
        3 => "",                               // Cloudy
        45 or 48 => "",                        // Fog
        51 or 53 or 55 or 56 or 57 => "",      // Drizzle (RainShowersDay)
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "",
        71 or 73 or 75 or 77 or 85 or 86 => "", // Snow
        95 or 96 or 99 => "",                  // Thunderstorm
        _ => "",
    };
}
