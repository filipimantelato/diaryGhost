using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Notifications;

/// <summary>
/// Ponto único por onde todo módulo pede uma notificação. Aplica a <see cref="NotificationPolicy"/>,
/// registra tudo no histórico (inclusive o que foi suprimido, com o motivo) e traduz cliques do sistema
/// em <see cref="NotificationActivated"/>.
/// </summary>
public sealed partial class NotificationCenter : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly INotificationSink _sink;
    private readonly INotificationHistory _history;
    private readonly SettingsService _settings;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationCenter> _logger;
    private readonly IDisposable _fullscreenSubscription;
    private readonly Dictionary<Guid, string> _tags = [];
    private readonly Lock _gate = new();
    private bool _fullscreen;

    public NotificationCenter(INotificationSink sink, INotificationHistory history, SettingsService settings, IEventBus bus,
        TimeProvider time, ILogger<NotificationCenter> logger)
    {
        _sink = sink;
        _history = history;
        _settings = settings;
        _bus = bus;
        _time = time;
        _logger = logger;
        _fullscreenSubscription = bus.Subscribe<ForegroundFullscreenChanged>(e => _fullscreen = e.IsFullscreen);
        _sink.Activated += OnActivated;
    }

    public NotificationChannelStatus ChannelStatus => _sink.Status;

    public bool IsPaused => _settings.Notifications.PausedUntil is { } until && _time.GetUtcNow() < until;

    public void PauseFor(TimeSpan? duration) =>
        _settings.UpdateNotifications(n => n with { PausedUntil = duration is { } d ? _time.GetUtcNow() + d : DateTimeOffset.MaxValue });

    public void Resume() => _settings.UpdateNotifications(n => n with { PausedUntil = null });

    public void PruneHistory()
    {
        var removed = _history.PruneBefore(_time.GetUtcNow() - Retention);
        if (removed > 0)
        {
            LogPruned(removed);
        }
    }

    public DeliveryOutcome Notify(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _time.GetUtcNow();
        var context = new NotificationContext(
            now,
            _time.GetLocalNow().TimeOfDay,
            _fullscreen,
            request.DedupKey is { } key && _history.ExistsSince(key, now - NotificationPolicy.DedupWindow),
            _history.CountShownSince(request.Category, now - NotificationPolicy.RateWindow));

        var outcome = NotificationPolicy.Decide(request, _settings.Notifications, context);
        var id = Guid.CreateVersion7(now);
        if (outcome == DeliveryOutcome.Shown)
        {
            lock (_gate)
            {
                _tags[id] = request.Tag;
            }
            outcome = _sink.Show(id, request);
        }

        var record = new NotificationRecord(id, request.Category, request.Priority, request.Title, request.Body, request.DedupKey, now, outcome);
        _history.Add(record);
        LogProcessed(request.Category, request.Priority, outcome);
        _bus.Publish(new NotificationProcessed(record));
        return outcome;
    }

    public IReadOnlyList<NotificationRecord> Recent(int count) => _history.Recent(count);

    private void OnActivated(Guid id, string? actionId)
    {
        string? tag;
        lock (_gate)
        {
            _tags.TryGetValue(id, out tag);
        }
        if (tag is not null)
        {
            _bus.Publish(new NotificationActivated(tag, actionId));
        }
    }

    public void Dispose()
    {
        _sink.Activated -= OnActivated;
        _fullscreenSubscription.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notificação {Category}/{Priority}: {Outcome}")]
    private partial void LogProcessed(NotificationCategory category, NotificationPriority priority, DeliveryOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Histórico de notificações: {Count} registros antigos removidos")]
    private partial void LogPruned(int count);
}
