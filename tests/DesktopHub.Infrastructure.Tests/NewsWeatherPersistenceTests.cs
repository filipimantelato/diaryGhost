using DesktopHub.Core.News;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Weather;
using DesktopHub.Infrastructure.News;
using DesktopHub.Infrastructure.Weather;

namespace DesktopHub.Infrastructure.Tests;

public sealed class NewsWeatherPersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sources_and_articles_roundtrip_and_duplicates_are_ignored()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteNewsStore(dir.Database);
        var source = TrustedSources.All[0] with { ETag = "\"abc\"", LastSuccessAt = T0, ConsecutiveFailures = 2, LastError = "HTTP 503" };
        store.UpsertSource(source);
        var article = new NewsArticle
        {
            Id = Guid.CreateVersion7(T0),
            SourceId = source.Id,
            ItemKey = "item-1",
            Title = "Título",
            Url = "https://a.gov.br/1",
            Summary = "Resumo",
            PublishedAt = T0.AddHours(-1),
            FetchedAt = T0,
            CanonicalUrl = "a.gov.br/1",
            TitleKey = "titulo",
            ClusterId = Guid.CreateVersion7(T0),
        };

        Assert.Equal(1, store.InsertArticles([article]));
        Assert.Equal(0, store.InsertArticles([article with { Id = Guid.CreateVersion7(T0) }])); // mesma fonte+item
        store.UpdateArticleState(article.Id, isRead: true, isSaved: false, isFavorite: true);

        var reopened = new SqliteNewsStore(dir.Reopen());
        var loadedSource = Assert.Single(reopened.LoadSources());
        Assert.Equal(("\"abc\"", 2, "HTTP 503", T0), (loadedSource.ETag, loadedSource.ConsecutiveFailures, loadedSource.LastError, loadedSource.LastSuccessAt));
        var loaded = Assert.Single(reopened.LoadArticles(T0.AddDays(-1)));
        Assert.Equal((true, true, T0.AddHours(-1)), (loaded.IsRead, loaded.IsFavorite, loaded.PublishedAt));
        Assert.Equal(0, reopened.PruneArticles(T0.AddDays(10))); // favorito não sai
    }

    [Fact]
    public void Built_in_sources_cannot_be_deleted()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteNewsStore(dir.Database);
        store.UpsertSource(TrustedSources.All[0]);
        store.DeleteSource(TrustedSources.All[0].Id);
        Assert.Single(store.LoadSources());
    }

    [Fact]
    public void Weather_cache_roundtrips()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var snapshot = new WeatherSnapshot(new WeatherLocation("Campinas", "São Paulo", "Brasil", -22.9, -47.06, "America/Sao_Paulo"),
            new CurrentWeather(26.8, 30.8, 72, 3, 3.6, 0, true),
            [new HourlyWeather(new DateTime(2026, 11, 6, 17, 0, 0), 26, 10, 3)],
            [new DailyWeather(new DateOnly(2026, 11, 6), 3, 29, 18, 40, new DateTime(2026, 11, 6, 5, 20, 0), new DateTime(2026, 11, 6, 18, 30, 0))],
            T0, "Open-Meteo");
        new SqliteWeatherCache(dir.Database).Save(snapshot);

        var loaded = new SqliteWeatherCache(dir.Reopen()).Load()!;
        Assert.Equal(26.8, loaded.Current.Temperature);
        Assert.Equal(T0, loaded.FetchedAt);
        Assert.Equal(new DateOnly(2026, 11, 6), Assert.Single(loaded.Daily).Date);
    }

    [Fact]
    public void Open_meteo_responses_are_parsed()
    {
        var location = new WeatherLocation("Campinas", "São Paulo", "Brasil", -22.9, -47.06, null);
        var forecast = OpenMeteoProvider.ParseForecast("""
            {"timezone":"America/Sao_Paulo",
             "current":{"time":"2026-11-06T16:45","temperature_2m":26.8,"apparent_temperature":30.8,"relative_humidity_2m":72,"weather_code":3,"wind_speed_10m":3.6,"precipitation":0.0,"is_day":1},
             "hourly":{"time":["2026-11-06T17:00","2026-11-06T18:00"],"temperature_2m":[26.5,25.1],"precipitation_probability":[10,null],"weather_code":[3,61]},
             "daily":{"time":["2026-11-06"],"weather_code":[61],"temperature_2m_max":[29.4],"temperature_2m_min":[18.2],"precipitation_probability_max":[40],"sunrise":["2026-11-06T05:20"],"sunset":["2026-11-06T18:31"]}}
            """, location, T0);

        Assert.Equal((26.8, 72, 3, true), (forecast.Current.Temperature, forecast.Current.Humidity, forecast.Current.Code, forecast.Current.IsDay));
        Assert.Equal(2, forecast.Hourly.Count);
        Assert.Null(forecast.Hourly[1].PrecipitationProbability);
        Assert.Equal(("America/Sao_Paulo", 29.4, 40), (forecast.Location.TimeZone, forecast.Daily[0].Max, forecast.Daily[0].PrecipitationProbability));

        var places = OpenMeteoProvider.ParseSearch("""
            {"results":[{"name":"Campinas","latitude":-22.90556,"longitude":-47.06083,"timezone":"America/Sao_Paulo","country":"Brasil","admin1":"São Paulo"}]}
            """);
        Assert.Equal("Campinas, São Paulo, Brasil", Assert.Single(places).DisplayName);
        Assert.Empty(OpenMeteoProvider.ParseSearch("""{"generationtime_ms":0.1}"""));
    }
}
