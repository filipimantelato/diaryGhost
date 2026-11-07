using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Activity;

/// <summary>
/// Recebe eventos de ferramentas autorizadas pelo canal local, guarda um histórico curto, mantém o estado
/// por sessão (trabalhando / aguardando você / terminou) e avisa conforme as preferências. O canal só
/// escuta com a integração ativada.
/// </summary>
public sealed partial class ActivityService : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    /// <summary>Limite de eventos por minuto (protege contra um hook em laço).</summary>
    public const int MaxPerMinute = 60;

    private readonly IActivityChannel _channel;
    private readonly IActivityStore _store;
    private readonly IClaudeHooksInstaller _hooks;
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notifications;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<ActivityService> _logger;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _recentArrivals = new();
    private readonly List<IDisposable> _subscriptions = [];
    private List<ActivityEvent> _events = [];

    public ActivityService(IActivityChannel channel, IActivityStore store, IClaudeHooksInstaller hooks, SettingsService settings,
        NotificationCenter notifications, IEventBus bus, TimeProvider time, ILogger<ActivityService> logger)
    {
        _channel = channel;
        _store = store;
        _hooks = hooks;
        _settings = settings;
        _notifications = notifications;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public bool IsListening => _channel.IsListening;

    public string? ChannelError => _channel.LastError;

    public IClaudeHooksInstaller Hooks => _hooks;

    public IReadOnlyList<ActivityEvent> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>Último evento de cada sessão, mais recentes primeiro (sessões encerradas há mais de 1 h somem).</summary>
    public IReadOnlyList<ActivityEvent> Sessions
    {
        get
        {
            var now = _time.GetUtcNow();
            return [.. Recent.Where(e => e.Kind != ActivityKind.Test)
                .GroupBy(e => e.Session ?? e.Project ?? "")
                .Select(g => g.First())
                .Where(e => e.Kind != ActivityKind.SessionEnded || now - e.ReceivedAt < TimeSpan.FromHours(1))
                .Take(6)];
        }
    }

    public void Start()
    {
        _store.PruneBefore(_time.GetUtcNow() - Retention);
        var recent = _store.Recent(100);
        lock (_gate)
        {
            _events = [.. recent];
        }
        _channel.MessageReceived += OnMessage;
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Integrations)
            {
                ApplyListening();
            }
        }));
        ApplyListening();
    }

    private void ApplyListening()
    {
        if (_settings.Integrations.ClaudeCodeEnabled)
        {
            _channel.Start();
        }
        else
        {
            _channel.Close();
        }
        LogListening(_channel.IsListening);
    }

    /// <summary>Ponto de entrada das linhas recebidas (público para testes e para o botão "Enviar teste").</summary>
    public ActivityEvent? Receive(string line)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            while (_recentArrivals.Count > 0 && now - _recentArrivals.Peek() > TimeSpan.FromMinutes(1))
            {
                _recentArrivals.Dequeue();
            }
            if (_recentArrivals.Count >= MaxPerMinute)
            {
                LogRateLimited();
                return null;
            }
            _recentArrivals.Enqueue(now);
        }
        var evt = ActivityContract.Parse(line, now);
        if (evt is null)
        {
            LogRejected();
            return null;
        }
        _store.Add(evt);
        lock (_gate)
        {
            _events.Insert(0, evt);
            if (_events.Count > 100)
            {
                _events.RemoveRange(100, _events.Count - 100);
            }
        }
        LogReceived(evt.Source, evt.Kind);
        _bus.Publish(new ActivityReceived(evt));
        Notify(evt);
        return evt;
    }

    private void OnMessage(string line) => Receive(line);

    private void Notify(ActivityEvent e)
    {
        var s = _settings.Integrations;
        var where = e.Project is null ? "" : $" em {e.Project}";
        NotificationRequest? request = e.Kind switch
        {
            ActivityKind.Finished when s.NotifyClaudeFinished => new(NotificationCategory.Integration, NotificationPriority.Normal,
                $"Claude Code terminou{where}", "A resposta está pronta.", "activity", DedupKey: $"claude:{e.Session}:{e.ReceivedAt.ToUnixTimeSeconds() / 30}"),
            ActivityKind.Waiting when s.NotifyClaudeWaiting => new(NotificationCategory.Integration, NotificationPriority.High,
                $"Claude Code aguardando você{where}", e.Message ?? "Precisa da sua confirmação.", "activity", DedupKey: $"claude-wait:{e.Session}:{e.Message}"),
            ActivityKind.Test => new(NotificationCategory.Integration, NotificationPriority.Normal, "Integração funcionando", e.Message, "activity"),
            _ => null,
        };
        if (request is not null)
        {
            _notifications.Notify(request);
        }
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessage;
        _channel.Close();
        _subscriptions.ForEach(s => s.Dispose());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Canal de atividade escutando: {Listening}")]
    private partial void LogListening(bool listening);

    [LoggerMessage(Level = LogLevel.Information, Message = "Atividade recebida: {Source}/{Kind}")]
    private partial void LogReceived(string source, ActivityKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mensagem de atividade fora do contrato descartada")]
    private partial void LogRejected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Excesso de mensagens de atividade; descartando por 1 minuto")]
    private partial void LogRateLimited();
}
