using System.Security.Cryptography;
using System.Text;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Security;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Agenda;

/// <summary>Assinaturas mudaram (lista, estado da sincronização).</summary>
public sealed record CalendarSubscriptionsChanged : IAppEvent;

/// <summary>
/// Agendas assinadas pelo endereço iCal — o caminho do Google Agenda ("Endereço secreto no formato iCal")
/// e do Outlook ("Publicar calendário" → ICS) sem login nem senha no app. Somente leitura: a fonte manda.
/// O endereço dá acesso à agenda, então é tratado como segredo (cifrado com DPAPI, nunca em log).
/// Sincroniza a cada 30 min com GET condicional; eventos recebem IDs estáveis (assinatura + UID + início),
/// para avisos já entregues não se repetirem a cada sincronização.
/// </summary>
public sealed partial class CalendarSubscriptionService : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(180);

    private readonly IAgendaStore _store;
    private readonly AgendaService _agenda;
    private readonly IFeedFetcher _fetcher;
    private readonly ISecretStore _secrets;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<CalendarSubscriptionService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private Dictionary<Guid, CalendarSubscription> _subscriptions = [];
    private ITimer? _timer;
    private bool _disposed;

    public CalendarSubscriptionService(IAgendaStore store, AgendaService agenda, IFeedFetcher fetcher, ISecretStore secrets, IEventBus bus,
        TimeProvider time, ILogger<CalendarSubscriptionService> logger)
    {
        _store = store;
        _agenda = agenda;
        _fetcher = fetcher;
        _secrets = secrets;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<CalendarSubscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions.Values];
            }
        }
    }

    public void Start()
    {
        var loaded = _store.LoadSubscriptions();
        lock (_gate)
        {
            _subscriptions = loaded.ToDictionary(s => s.Id);
        }
        Schedule(TimeSpan.FromSeconds(30));
    }

    private static string SecretName(Guid id) => $"ics-url:{id:N}";

    /// <summary>Aceita https://, http:// e webcal:// (convertido para https). Outros esquemas são recusados.</summary>
    public static bool TryNormalizeUrl(string? text, out Uri url)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            t = "https://" + t["webcal://".Length..];
        }
        return NewsUrl.TryParseHttp(t, null, out url);
    }

    /// <summary>Endereço mascarado para exibir (só o domínio).</summary>
    public string MaskedUrl(Guid id) =>
        _secrets.Read(SecretName(id)) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}/…" : "(endereço indisponível)";

    public async Task<string?> AddAsync(string name, string url, Guid? categoryId, int? reminderMinutes, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUrl(url, out var uri))
        {
            return "Use o endereço iCal completo (https:// ou webcal://).";
        }
        var subscription = new CalendarSubscription
        {
            Id = Guid.CreateVersion7(_time.GetUtcNow()),
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim()[..Math.Min(name.Trim().Length, 80)],
            CategoryId = categoryId,
            ReminderMinutes = reminderMinutes,
        };
        var (result, error) = await DownloadAsync(subscription, uri, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }
        _store.UpsertSubscription(subscription);
        _secrets.Save(SecretName(subscription.Id), uri.AbsoluteUri);
        lock (_gate)
        {
            _subscriptions[subscription.Id] = subscription;
        }
        Apply(subscription, result!, result!.ETag, result.LastModified);
        Schedule(Interval);
        LogAdded(subscription.Name);
        return null;
    }

    public void Update(Guid id, Func<CalendarSubscription, CalendarSubscription> change)
    {
        CalendarSubscription updated;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(id, out var current))
            {
                return;
            }
            // Força nova leitura completa para aplicar categoria/aviso aos eventos.
            updated = change(current) with { Id = id, ETag = null, LastModified = null };
            _subscriptions[id] = updated;
        }
        _store.UpsertSubscription(updated);
        _bus.Publish(new CalendarSubscriptionsChanged());
        _ = SyncAsync(force: true);
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            _subscriptions.Remove(id);
        }
        _store.DeleteSubscription(id);
        _secrets.Delete(SecretName(id));
        _agenda.ForgetSubscriptionEvents(id);
        _bus.Publish(new CalendarSubscriptionsChanged());
    }

    public async Task SyncAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!await _syncing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            foreach (var subscription in Subscriptions.Where(s => s.Enabled))
            {
                if (_secrets.Read(SecretName(subscription.Id)) is not { } url || !TryNormalizeUrl(url, out var uri))
                {
                    Save(subscription with { LastAttemptAt = _time.GetUtcNow(), LastError = "Endereço não encontrado no cofre; remova e adicione de novo." });
                    continue;
                }
                var (result, error) = await DownloadAsync(force ? subscription with { ETag = null, LastModified = null } : subscription, uri, cancellationToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (error is not null)
                {
                    Save(subscription with { LastAttemptAt = now, LastError = error });
                    LogSyncFailed(subscription.Name, error);
                }
                else if (result is null)
                {
                    Save(subscription with { LastAttemptAt = now, LastSuccessAt = now, LastError = null }); // 304: nada mudou
                }
                else
                {
                    Apply(subscription, result, result.ETag, result.LastModified);
                }
            }
        }
        finally
        {
            _syncing.Release();
            Schedule(Interval);
        }
    }

    private sealed record Downloaded(IcsImport Import, string? ETag, string? LastModified);

    /// <returns>(null, null) quando não mudou (304); (null, erro) em falha.</returns>
    private async Task<(Downloaded? Result, string? Error)> DownloadAsync(CalendarSubscription s, Uri uri, CancellationToken cancellationToken)
    {
        FetchResult fetched;
        try
        {
            fetched = await _fetcher.FetchAsync(uri, s.ETag, s.LastModified, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Sem acesso ao endereço: {ex.Message}");
        }
        if (fetched.Status == FetchStatus.NotModified)
        {
            return (null, null);
        }
        if (fetched.Status == FetchStatus.Failed || fetched.Content is null)
        {
            return (null, $"Sem acesso ao endereço: {fetched.Error}");
        }
        try
        {
            var text = Encoding.UTF8.GetString(fetched.Content);
            var import = ICalendar.Parse(text, _agenda.LocalZone, _time.GetUtcNow(), _time.GetUtcNow() - HistoryKept);
            return (new Downloaded(import, fetched.ETag, fetched.LastModified), null);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    private void Apply(CalendarSubscription s, Downloaded downloaded, string? etag, string? lastModified)
    {
        var now = _time.GetUtcNow();
        var events = downloaded.Import.Events.Select(e =>
        {
            var id = StableId(s.Id, e.ExternalUid, e.Start);
            var previous = _agenda.FindEvent(id);
            var mapped = e with
            {
                Id = id,
                SubscriptionId = s.Id,
                CategoryId = s.CategoryId,
                ReminderMinutes = s.ReminderMinutes is { } m ? [m] : [],
                CreatedAt = previous?.CreatedAt ?? now,
                UpdatedAt = now,
            };
            // Sem mudança real: mantém a data de edição (o agendador de avisos não reprocessa à toa).
            return previous is not null && SameContent(previous, mapped) ? mapped with { UpdatedAt = previous.UpdatedAt } : mapped;
        }).ToList();
        _agenda.ApplySubscriptionEvents(s.Id, events);
        Save(s with
        {
            ETag = etag ?? s.ETag,
            LastModified = lastModified ?? s.LastModified,
            LastAttemptAt = now,
            LastSuccessAt = now,
            LastError = downloaded.Import.Warnings.Count > 0 ? "Avisos: " + string.Join(" ", downloaded.Import.Warnings.Take(3)) : null,
            EventCount = events.Count,
        });
    }

    private static bool SameContent(CalendarEvent a, CalendarEvent b) =>
        a.Title == b.Title && a.Start == b.Start && a.Duration == b.Duration && a.AllDay == b.AllDay && a.TimeZoneId == b.TimeZoneId &&
        a.Recurrence?.ToString() == b.Recurrence?.ToString() && a.Exceptions.SequenceEqual(b.Exceptions) &&
        a.ReminderMinutes.SequenceEqual(b.ReminderMinutes) && a.CategoryId == b.CategoryId && a.Location == b.Location && a.Notes == b.Notes;

    public static Guid StableId(Guid subscriptionId, string? uid, DateTime start)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{subscriptionId:N}|{uid}|{start:O}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void Save(CalendarSubscription updated)
    {
        lock (_gate)
        {
            if (!_subscriptions.ContainsKey(updated.Id))
            {
                return; // removida durante a sincronização
            }
            _subscriptions[updated.Id] = updated;
        }
        _store.UpsertSubscription(updated);
        _bus.Publish(new CalendarSubscriptionsChanged());
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
            _timer = _subscriptions.Count == 0 ? null : _time.CreateTimer(_ => _ = SyncAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Reschedule() => Schedule(TimeSpan.FromSeconds(15));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _syncing.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agenda assinada adicionada: {Name}")]
    private partial void LogAdded(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao sincronizar a agenda {Name}: {Error}")]
    private partial void LogSyncFailed(string name, string error);
}
