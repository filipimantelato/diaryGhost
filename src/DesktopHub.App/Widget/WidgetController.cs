using DesktopHub.App.Platform;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Weather;
using Microsoft.Extensions.Logging;

namespace DesktopHub.App.Widget;

/// <summary>
/// Decide quando o widget aparece (preferência do usuário + política de tela cheia + supressão da sessão
/// via <c>--no-widget</c>) e repassa a ele os eventos de mídia e volume. A janela é criada só na primeira exibição.
/// </summary>
internal sealed partial class WidgetController(
    SettingsService settings,
    IEventBus bus,
    FullscreenMonitor fullscreen,
    MediaService media,
    IMasterVolume volume,
    ProductivityService productivity,
    AgendaService agenda,
    WeatherService weather,
    ILogger<WidgetController> logger,
    ILoggerFactory loggerFactory) : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private WidgetWindow? _window;
    private bool _suppressedForSession;
    private bool _disposed;

    public bool IsShown => _window?.IsVisible == true;

    public bool CaptureExclusionSupported => _window?.CaptureExclusionSupported ?? true;

    public void Start(bool suppressForSession)
    {
        _suppressedForSession = suppressForSession;
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Widget)
            {
                Refresh(WidgetVisibilityReason.User);
            }
            else if (e.Section == SettingsSections.Weather)
            {
                _window?.ShowWeather();
            }
            else if (e.Section == SettingsSections.Media)
            {
                _window?.RefreshMedia();
            }
        }, ui));
        _subscriptions.Add(bus.Subscribe<ForegroundFullscreenChanged>(_ => Refresh(WidgetVisibilityReason.FullscreenPolicy), ui));
        // Eventos de mídia/volume chegam de threads do Windows; o contexto de UI serializa a entrega.
        _subscriptions.Add(bus.Subscribe<ActiveMediaChanged>(e => _window?.ShowMedia(e), ui));
        _subscriptions.Add(bus.Subscribe<MasterVolumeChanged>(e => _window?.ShowVolume(e.State), ui));
        _subscriptions.Add(bus.Subscribe<ProductivityChanged>(e => _window?.ShowProductivity(e.Snapshot), ui));
        _subscriptions.Add(bus.Subscribe<AgendaChanged>(_ => _window?.ShowAgenda(), ui));
        _subscriptions.Add(bus.Subscribe<WeatherUpdated>(_ => _window?.ShowWeather(), ui));
        Refresh(WidgetVisibilityReason.User);
    }

    public void Toggle()
    {
        if (_suppressedForSession)
        {
            _suppressedForSession = false;
            if (settings.Widget.Visible)
            {
                Refresh(WidgetVisibilityReason.User);
                return;
            }
        }
        settings.UpdateWidget(w => w with { Visible = !w.Visible });
    }

    public void ResetPosition()
    {
        if (_window is null)
        {
            settings.UpdateWidget(w => w with { Position = null });
            return;
        }
        _window.ResetPosition();
    }

    private void Refresh(WidgetVisibilityReason reason)
    {
        if (_disposed)
        {
            return;
        }
        var s = settings.Widget;
        var hiddenByFullscreen = fullscreen.IsFullscreen && s.Fullscreen == FullscreenBehavior.Hide;
        var shouldShow = s.Visible && !_suppressedForSession && !hiddenByFullscreen;

        if (shouldShow)
        {
            if (_window is null)
            {
                _window = new WidgetWindow(settings, bus, media, volume, productivity, agenda, weather, loggerFactory.CreateLogger<WidgetWindow>());
                LogCreated();
            }
            _window.Apply(s);
            if (!_window.IsVisible)
            {
                _window.Show();
                bus.Publish(new WidgetVisibilityChanged(true, reason));
            }
        }
        else if (_window is not null)
        {
            _window.Apply(s);
            if (_window.IsVisible)
            {
                _window.Hide();
                bus.Publish(new WidgetVisibilityChanged(false, hiddenByFullscreen ? WidgetVisibilityReason.FullscreenPolicy : reason));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _subscriptions.ForEach(s => s.Dispose());
        _window?.Close();
        _window = null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Janela do widget criada")]
    private partial void LogCreated();
}
