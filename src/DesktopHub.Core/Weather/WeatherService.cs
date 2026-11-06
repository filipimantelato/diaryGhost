using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Weather;

/// <summary>
/// Clima para o local escolhido pelo usuário. Atualiza a cada <see cref="WeatherSettings.RefreshMinutes"/>
/// (um timer), guarda a última previsão e a exibe com o horário da consulta quando não há internet.
/// Sem local configurado, não faz nenhuma requisição.
/// </summary>
public sealed partial class WeatherService : IDisposable
{
    /// <summary>Acima disto a previsão é marcada como desatualizada na tela.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private readonly IWeatherProvider _provider;
    private readonly IWeatherCache _cache;
    private readonly SettingsService _settings;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<WeatherService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _timer;
    private bool _disposed;

    public WeatherService(IWeatherProvider provider, IWeatherCache cache, SettingsService settings, IEventBus bus, TimeProvider time,
        ILogger<WeatherService> logger)
    {
        _provider = provider;
        _cache = cache;
        _settings = settings;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public WeatherSnapshot? Snapshot { get; private set; }

    public string? LastError { get; private set; }

    public bool IsStale => Snapshot is { } s && _time.GetUtcNow() - s.FetchedAt > StaleAfter;

    public WeatherLocation? Location => _settings.Weather.Location;

    public void Start()
    {
        var cached = _cache.Load();
        // Previsão guardada de outro local (o usuário trocou) não é exibida.
        Snapshot = cached is not null && SameLocation(cached.Location, Location) ? cached : null;
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Weather)
            {
                if (Snapshot is not null && !SameLocation(Snapshot.Location, Location))
                {
                    Snapshot = null;
                    _bus.Publish(new WeatherUpdated(null, null));
                }
                Schedule(TimeSpan.Zero);
            }
        }));
        var age = Snapshot is null ? TimeSpan.MaxValue : _time.GetUtcNow() - Snapshot.FetchedAt;
        var interval = TimeSpan.FromMinutes(_settings.Weather.RefreshMinutes);
        Schedule(age >= interval ? TimeSpan.FromSeconds(5) : interval - age);
    }

    public void Reschedule() => Schedule(TimeSpan.FromSeconds(5));

    public Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name, CancellationToken cancellationToken = default) =>
        _provider.SearchAsync(name, cancellationToken);

    public void SetLocation(WeatherLocation? location) => _settings.UpdateWeather(w => w with { Location = location });

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Location is not { } location || !await _refreshing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var ok = false;
        try
        {
            var snapshot = await _provider.ForecastAsync(location, cancellationToken).ConfigureAwait(false);
            Snapshot = snapshot;
            LastError = null;
            _cache.Save(snapshot);
            ok = true;
            LogUpdated(snapshot.Current.Temperature, snapshot.Current.Code);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            LastError = ex is TaskCanceledException ? "Tempo esgotado ao consultar o clima." : "Sem conexão com o serviço de clima.";
            LogFailed(ex);
        }
        finally
        {
            _refreshing.Release();
        }
        Schedule(ok ? TimeSpan.FromMinutes(_settings.Weather.RefreshMinutes) : RetryAfterFailure);
        _bus.Publish(new WeatherUpdated(Snapshot, LastError));
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _timer?.Dispose();
            _timer = null;
            if (Location is null)
            {
                return;
            }
            _timer = _time.CreateTimer(_ => _ = RefreshAsync(), null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static bool SameLocation(WeatherLocation a, WeatherLocation? b) =>
        b is not null && Math.Abs(a.Latitude - b.Latitude) < 0.01 && Math.Abs(a.Longitude - b.Longitude) < 0.01;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _subscriptions.ForEach(s => s.Dispose());
        _refreshing.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Clima atualizado: {Temperature} °C, código {Code}")]
    private partial void LogUpdated(double temperature, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao atualizar o clima; mantida a última previsão")]
    private partial void LogFailed(Exception ex);
}
