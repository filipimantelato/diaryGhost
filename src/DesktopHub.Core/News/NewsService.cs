using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.News;

/// <summary>
/// Agregador de notícias. Só busca com o módulo ativado. Um único timer dorme até a próxima fonte vencida;
/// cada busca é condicional (ETag/If-Modified-Since → 304 não baixa nada), falhas recuam exponencialmente
/// (respeitando Retry-After) e cada fonte tem limites de frequência e de itens. Artigos repetidos (mesma URL
/// canônica ou mesmo título) são descartados; parecidos de fontes diferentes formam um grupo.
/// </summary>
public sealed partial class NewsService : IDisposable
{
    public const int MaxItemsPerFetch = 100;
    public const int MaxConcurrentFetches = 4;
    /// <summary>"Atualizar" manual não busca de novo uma fonte consultada há menos que isto.</summary>
    public static readonly TimeSpan MinManualInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ClusterWindow = TimeSpan.FromHours(48);

    private readonly INewsStore _store;
    private readonly IFeedFetcher _fetcher;
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notifications;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<NewsService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private Dictionary<Guid, NewsSource> _sources = [];
    private List<NewsArticle> _articles = [];
    private ITimer? _timer;
    private bool _disposed;

    public NewsService(INewsStore store, IFeedFetcher fetcher, SettingsService settings, NotificationCenter notifications,
        IEventBus bus, TimeProvider time, ILogger<NewsService> logger)
    {
        _store = store;
        _fetcher = fetcher;
        _settings = settings;
        _notifications = notifications;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public bool IsEnabled => _settings.News.Enabled;

    public bool IsRefreshing => _refreshing.CurrentCount == 0;

    public IReadOnlyList<NewsSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources.Values.OrderBy(s => s.Category).ThenBy(s => s.Name, StringComparer.CurrentCulture)];
            }
        }
    }

    /// <summary>Artigos retidos, mais recentes primeiro.</summary>
    public IReadOnlyList<NewsArticle> Articles
    {
        get
        {
            lock (_gate)
            {
                return [.. _articles];
            }
        }
    }

    public NewsSource? Source(Guid id)
    {
        lock (_gate)
        {
            return _sources.GetValueOrDefault(id);
        }
    }

    /// <summary>Última consulta bem-sucedida de qualquer fonte ativa (para "atualizado às …" e modo offline).</summary>
    public DateTimeOffset? LastSuccessAt => Sources.Where(s => s.Enabled).Max(s => s.LastSuccessAt);

    public void Start()
    {
        var stored = _store.LoadSources().ToDictionary(s => s.Id);
        foreach (var builtIn in TrustedSources.All)
        {
            // Fonte curada nova ou com nome/URL atualizados nesta versão; estado e "oculta" do usuário são mantidos.
            var merged = stored.TryGetValue(builtIn.Id, out var existing)
                ? existing with { Name = builtIn.Name, FeedUrl = builtIn.FeedUrl, Category = builtIn.Category, BuiltIn = true }
                : builtIn;
            if (!Equals(merged, existing))
            {
                _store.UpsertSource(merged);
                stored[merged.Id] = merged;
            }
        }
        var now = _time.GetUtcNow();
        var pruned = _store.PruneArticles(now - TimeSpan.FromDays(_settings.News.RetentionDays));
        var articles = _store.LoadArticles(now - TimeSpan.FromDays(_settings.News.RetentionDays));
        lock (_gate)
        {
            _sources = stored;
            _articles = [.. articles.OrderByDescending(a => a.SortDate)];
        }
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.News)
            {
                Schedule(StartupDelay);
            }
        }));
        _subscriptions.Add(_bus.Subscribe<NotificationActivated>(e =>
        {
            if (e.Tag == "news")
            {
                _bus.Publish(new OpenMainRequested("news"));
            }
        }));
        LogStarted(stored.Count, articles.Count, pruned);
        Schedule(StartupDelay); // não disputa recursos com a inicialização do app
    }

    // ---------- Busca ----------

    /// <summary>Atualiza agora as fontes ativas (respeitando o intervalo mínimo por fonte). Devolve artigos novos.</summary>
    public Task<int> RefreshNowAsync(CancellationToken cancellationToken = default) => RefreshAsync(manual: true, cancellationToken);

    private async Task<int> RefreshAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!IsEnabled || !await _refreshing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        var added = 0;
        try
        {
            var now = _time.GetUtcNow();
            var due = Sources.Where(s => s.Enabled && (manual
                ? s.LastAttemptAt is null || now - s.LastAttemptAt >= MinManualInterval
                : s.NextFetchAt is null || s.NextFetchAt <= now)).ToList();
            if (due.Count == 0)
            {
                return 0;
            }
            _bus.Publish(new NewsUpdated(0)); // telas mostram "atualizando…"
            using var throttle = new SemaphoreSlim(MaxConcurrentFetches);
            var tasks = due.Select(async source =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await FetchSourceAsync(source, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            });
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var fresh = results.SelectMany(r => r).ToList();
            added = fresh.Count;
            LogRefreshed(due.Count, added);
            NotifyFollowed(fresh);
        }
        finally
        {
            _refreshing.Release();
            Schedule(null);
            _bus.Publish(new NewsUpdated(added));
        }
        return added;
    }

    private async Task<List<NewsArticle>> FetchSourceAsync(NewsSource source, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        FetchResult result;
        if (!NewsUrl.TryParseHttp(source.FeedUrl, null, out var feedUrl))
        {
            result = FetchResult.Fail("Endereço de feed inválido.");
        }
        else
        {
            try
            {
                result = await _fetcher.FetchAsync(feedUrl, source.ETag, source.LastModified, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = FetchResult.Fail(ex.Message);
            }
        }

        var now = _time.GetUtcNow();
        var interval = TimeSpan.FromMinutes(_settings.News.RefreshMinutes);
        List<NewsArticle> fresh = [];
        NewsSource updated;
        switch (result.Status)
        {
            case FetchStatus.NotModified:
                updated = source with { LastAttemptAt = started, LastSuccessAt = now, LastError = null, ConsecutiveFailures = 0, NextFetchAt = now + interval };
                break;
            case FetchStatus.Ok when TryParse(result.Content, out var feed, out var parseError):
                fresh = Ingest(source, feedUrl, feed!, now);
                updated = source with
                {
                    ETag = result.ETag,
                    LastModified = result.LastModified,
                    LastAttemptAt = started,
                    LastSuccessAt = now,
                    LastError = null,
                    ConsecutiveFailures = 0,
                    NextFetchAt = now + interval,
                };
                _ = parseError;
                break;
            default:
                var failures = source.ConsecutiveFailures + 1;
                var error = result.Status == FetchStatus.Ok ? "O conteúdo não é um feed RSS/Atom válido." : result.Error ?? "Falha desconhecida.";
                updated = source with
                {
                    LastAttemptAt = started,
                    LastError = error,
                    ConsecutiveFailures = failures,
                    NextFetchAt = now + Backoff(source.Id, failures, interval, result.RetryAfter),
                };
                LogFetchFailed(source.Name, failures, error);
                break;
        }
        _store.UpsertSource(updated);
        lock (_gate)
        {
            _sources[updated.Id] = updated;
        }
        return fresh;
    }

    private static bool TryParse(byte[]? content, out ParsedFeed? feed, out string? error)
    {
        feed = null;
        error = null;
        if (content is null)
        {
            error = "Resposta vazia.";
            return false;
        }
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            feed = FeedParser.Parse(stream);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Recuo exponencial: 15 min, 30, 1 h, 2 h… até 6 h (nunca menos que o intervalo normal), com variação fixa
    /// por fonte para não repetir em sincronia. Retry-After do servidor é respeitado (até 24 h).
    /// </summary>
    public static TimeSpan Backoff(Guid sourceId, int failures, TimeSpan interval, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromMinutes(15 * Math.Pow(2, Math.Min(failures - 1, 10)));
        var delay = exponential > MaxBackoff ? MaxBackoff : exponential;
        delay = delay < interval ? interval : delay;
        var jitter = 1 + (sourceId.GetHashCode() % 100) / 1000.0; // ±10%
        delay = TimeSpan.FromTicks((long)(delay.Ticks * jitter));
        if (retryAfter is { } ra && ra > delay)
        {
            delay = ra > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : ra;
        }
        return delay;
    }

    /// <summary>Converte itens em artigos novos: descarta inválidos e repetidos, agrupa parecidos, grava.</summary>
    private List<NewsArticle> Ingest(NewsSource source, Uri feedUrl, ParsedFeed feed, DateTimeOffset now)
    {
        List<NewsArticle> fresh = [];
        lock (_gate)
        {
            var existingKeys = _articles.Where(a => a.SourceId == source.Id).Select(a => a.ItemKey).ToHashSet(StringComparer.Ordinal);
            var urls = _articles.Select(a => a.CanonicalUrl).ToHashSet(StringComparer.Ordinal);
            var recent = _articles.Where(a => now - a.SortDate <= ClusterWindow)
                .Select(a => (Article: a, Tokens: NewsTitles.Tokens(a.TitleKey))).ToList();
            var recentTitles = recent.Select(r => r.Article.TitleKey).ToHashSet(StringComparer.Ordinal);

            foreach (var item in feed.Items.Take(MaxItemsPerFetch))
            {
                if (!NewsUrl.TryParseHttp(item.Link, feedUrl, out var url))
                {
                    continue; // sem link http(s) válido não há como abrir a notícia
                }
                var key = string.IsNullOrWhiteSpace(item.Id) ? url.AbsoluteUri : item.Id!;
                var canonical = NewsUrl.Canonical(url);
                var titleKey = NewsTitles.Key(item.Title);
                if (titleKey.Length == 0 || existingKeys.Contains(key) || urls.Contains(canonical) || recentTitles.Contains(titleKey))
                {
                    continue;
                }
                var published = item.PublishedAt is { } p && p > now.AddDays(-365) ? p : (DateTimeOffset?)null;
                var tokens = NewsTitles.Tokens(titleKey);
                var cluster = recent.FirstOrDefault(r => r.Article.SourceId != source.Id && NewsTitles.SameStory(tokens, r.Tokens)).Article?.ClusterId;
                var article = new NewsArticle
                {
                    Id = Guid.CreateVersion7(now),
                    SourceId = source.Id,
                    ItemKey = key.Length > 500 ? key[..500] : key,
                    Title = item.Title,
                    Url = url.AbsoluteUri,
                    Summary = item.Summary is { Length: > 0 } s && !string.Equals(s, item.Title, StringComparison.Ordinal) ? s : null,
                    Author = item.Author,
                    PublishedAt = published,
                    FetchedAt = now,
                    CanonicalUrl = canonical,
                    TitleKey = titleKey,
                    ClusterId = cluster ?? Guid.CreateVersion7(now),
                };
                fresh.Add(article);
                existingKeys.Add(key);
                urls.Add(canonical);
                recentTitles.Add(titleKey);
                recent.Add((article, tokens));
            }
            if (fresh.Count > 0)
            {
                _store.InsertArticles(fresh);
                _articles = [.. _articles.Concat(fresh).OrderByDescending(a => a.SortDate)];
            }
        }
        return fresh;
    }

    private void NotifyFollowed(List<NewsArticle> fresh)
    {
        var settings = _settings.News;
        if (!settings.NotifyFollowedTopics || fresh.Count == 0)
        {
            return;
        }
        foreach (var topic in settings.FollowedTopics)
        {
            var topicKey = NewsTitles.Key(topic);
            var matches = fresh.Where(a => NewsTitles.Matches(a.TitleKey, topicKey)).ToList();
            if (matches.Count == 0)
            {
                continue;
            }
            var first = matches[0];
            var sourceName = Source(first.SourceId)?.Name ?? "";
            _notifications.Notify(new NotificationRequest(NotificationCategory.News, NotificationPriority.Normal,
                matches.Count == 1 ? $"Novidade sobre {topic}" : $"{matches.Count} novidades sobre {topic}",
                $"{first.Title} — {sourceName}", "news", DedupKey: $"news:{first.Id}"));
        }
    }

    // ---------- Agendamento ----------

    private void Schedule(TimeSpan? minimumDelay)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _timer?.Dispose();
            _timer = null;
            if (!IsEnabled)
            {
                return;
            }
            var now = _time.GetUtcNow();
            var next = _sources.Values.Where(s => s.Enabled).Select(s => s.NextFetchAt ?? now).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            if (next == DateTimeOffset.MaxValue)
            {
                return;
            }
            var delay = next - now;
            if (minimumDelay is { } min && delay < min)
            {
                delay = min;
            }
            if (delay < TimeSpan.FromSeconds(1))
            {
                delay = TimeSpan.FromSeconds(1);
            }
            _timer = _time.CreateTimer(_ => _ = RefreshAsync(manual: false, CancellationToken.None), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reagenda (ex.: ao voltar da suspensão).</summary>
    public void Reschedule() => Schedule(TimeSpan.FromSeconds(10));

    // ---------- Estado de leitura ----------

    public void SetState(Guid articleId, bool? read = null, bool? saved = null, bool? favorite = null)
    {
        NewsArticle? updated = null;
        lock (_gate)
        {
            var index = _articles.FindIndex(a => a.Id == articleId);
            if (index < 0)
            {
                return;
            }
            var a = _articles[index];
            updated = a with { IsRead = read ?? a.IsRead, IsSaved = saved ?? a.IsSaved, IsFavorite = favorite ?? a.IsFavorite };
            _articles[index] = updated;
        }
        _store.UpdateArticleState(updated.Id, updated.IsRead, updated.IsSaved, updated.IsFavorite);
        _bus.Publish(new NewsUpdated(0));
    }

    public void MarkAllRead(IEnumerable<Guid> ids)
    {
        foreach (var id in ids.ToList())
        {
            lock (_gate)
            {
                if (_articles.Find(a => a.Id == id) is not { IsRead: false })
                {
                    continue;
                }
            }
            SetState(id, read: true);
        }
    }

    // ---------- Fontes ----------

    /// <summary>Ocultar/mostrar uma fonte (curadas não podem ser apagadas, só ocultadas).</summary>
    public void SetSourceEnabled(Guid id, bool enabled)
    {
        NewsSource? updated;
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source))
            {
                return;
            }
            updated = source with { Enabled = enabled, NextFetchAt = enabled ? null : source.NextFetchAt };
            _sources[id] = updated;
        }
        _store.UpsertSource(updated);
        _bus.Publish(new NewsUpdated(0));
        Schedule(TimeSpan.FromSeconds(2));
    }

    /// <summary>Adiciona um feed do usuário depois de baixá-lo e validá-lo. Devolve uma mensagem de erro ou null.</summary>
    public async Task<string?> AddSourceAsync(string url, NewsCategory category, CancellationToken cancellationToken = default)
    {
        if (!NewsUrl.TryParseHttp(url, null, out var feedUrl))
        {
            return "Use um endereço http(s) completo do feed RSS ou Atom.";
        }
        if (Sources.Any(s => string.Equals(NewsUrl.Canonical(new Uri(s.FeedUrl)), NewsUrl.Canonical(feedUrl), StringComparison.Ordinal)))
        {
            return "Essa fonte já está na lista.";
        }
        FetchResult result;
        try
        {
            result = await _fetcher.FetchAsync(feedUrl, null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return $"Não foi possível acessar: {ex.Message}";
        }
        if (result.Status != FetchStatus.Ok)
        {
            return $"Não foi possível acessar: {result.Error}";
        }
        if (!TryParse(result.Content, out var feed, out var parseError))
        {
            return $"O endereço não é um feed RSS/Atom válido ({parseError}).";
        }
        var name = feed!.Title is { Length: > 0 } title ? title : feedUrl.Host;
        var source = new NewsSource
        {
            Id = Guid.CreateVersion7(_time.GetUtcNow()),
            Name = name.Length > 80 ? name[..80] : name,
            FeedUrl = feedUrl.AbsoluteUri,
            Category = category,
            BuiltIn = false,
        };
        _store.UpsertSource(source);
        lock (_gate)
        {
            _sources[source.Id] = source;
        }
        LogSourceAdded(source.Name);
        _ = RefreshAsync(manual: true, CancellationToken.None);
        return null;
    }

    public void RemoveSource(Guid id)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source) || source.BuiltIn)
            {
                return;
            }
            _sources.Remove(id);
            _articles.RemoveAll(a => a.SourceId == id);
        }
        _store.DeleteSource(id);
        _bus.Publish(new NewsUpdated(0));
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Notícias: {Sources} fontes, {Articles} artigos carregados, {Pruned} antigos removidos")]
    private partial void LogStarted(int sources, int articles, int pruned);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notícias atualizadas: {Sources} fontes consultadas, {Added} artigos novos")]
    private partial void LogRefreshed(int sources, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fonte de notícias falhou ({Failures}ª vez): {Source} — {Error}")]
    private partial void LogFetchFailed(string source, int failures, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fonte de notícias adicionada: {Source}")]
    private partial void LogSourceAdded(string source);
}
