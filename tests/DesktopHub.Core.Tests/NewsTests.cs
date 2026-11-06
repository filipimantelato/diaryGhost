using System.Text;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Weather;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

public sealed class FeedParserTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel><title>Agência Teste</title>
            <item>
              <title>Senado aprova &lt;b&gt;projeto&lt;/b&gt; de lei</title>
              <link>https://exemplo.gov.br/noticia/1?utm_source=rss</link>
              <guid isPermaLink="false">abc-1</guid>
              <description><![CDATA[<p>Texto <strong>com</strong> HTML &amp; entidades.</p><script>alert(1)</script>]]></description>
              <dc:creator>Maria Silva</dc:creator>
              <pubDate>Fri, 06 Nov 2026 16:45:00 -0300</pubDate>
            </item>
            <item><title>Sem link nem guid</title></item>
            <item><title>Data com fuso nomeado</title><link>/relativo/2</link><pubDate>Fri, 6 Nov 2026 19:00:00 GMT</pubDate></item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Rss_items_are_read_and_html_becomes_text()
    {
        var feed = FeedParser.Parse(Rss);

        Assert.Equal("Agência Teste", feed.Title);
        Assert.Equal(3, feed.Items.Count);
        var first = feed.Items[0];
        Assert.Equal("Senado aprova projeto de lei", first.Title);
        Assert.Equal("Texto com HTML & entidades.", first.Summary);
        Assert.Equal("Maria Silva", first.Author);
        Assert.Equal("abc-1", first.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 19, 45, 0, TimeSpan.Zero), first.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero), feed.Items[2].PublishedAt);
    }

    [Fact]
    public void Atom_entries_are_read()
    {
        var feed = FeedParser.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Releases</title>
              <entry><id>tag:github.com,2008:1</id><title>v10.0.1</title>
                <link rel="alternate" href="https://github.com/dotnet/runtime/releases/tag/v10.0.1"/>
                <updated>2026-11-05T10:00:00Z</updated><author><name>dotnet-bot</name></author>
                <content type="html">&lt;p&gt;Correções&lt;/p&gt;</content></entry>
            </feed>
            """);

        var entry = Assert.Single(feed.Items);
        Assert.Equal(("v10.0.1", "https://github.com/dotnet/runtime/releases/tag/v10.0.1", "dotnet-bot", "Correções"),
            (entry.Title, entry.Link, entry.Author, entry.Summary));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), entry.PublishedAt);
    }

    [Fact]
    public void Rdf_rss1_items_are_read()
    {
        var feed = FeedParser.Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>RDF</title></channel>
              <item><title>Item RDF</title><link>https://a.org/x</link><dc:date>2026-11-06T12:00:00-03:00</dc:date></item>
            </rdf:RDF>
            """);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), Assert.Single(feed.Items).PublishedAt);
    }

    [Fact]
    public void External_entities_are_never_resolved()
    {
        var secret = Path.GetTempFileName();
        File.WriteAllText(secret, "SEGREDO");
        try
        {
            var xml = $"""
                <?xml version="1.0"?>
                <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///{secret.Replace('\\', '/')}">]>
                <rss><channel><item><title>a &xxe; b</title><link>https://a.org</link></item></channel></rss>
                """;
            try
            {
                var feed = FeedParser.Parse(xml);
                Assert.DoesNotContain("SEGREDO", feed.Items.FirstOrDefault()?.Title ?? "", StringComparison.Ordinal);
            }
            catch (System.Xml.XmlException)
            {
                // Recusar o documento também é aceitável: o que não pode é ler o arquivo.
            }
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Non_feed_documents_are_rejected()
    {
        Assert.Throws<FormatException>(() => FeedParser.Parse("<html><body>oi</body></html>"));
    }

    [Theory]
    [InlineData("Fri, 06 Nov 2026 16:45:00 -0300", "2026-11-06T19:45:00Z")]
    [InlineData("06 Nov 2026 16:45:00 EST", "2026-11-06T21:45:00Z")]
    [InlineData("Fri, 6 Nov 2026 16:45 +0000", "2026-11-06T16:45:00Z")]
    [InlineData("2026-11-06T16:45:00Z", "2026-11-06T16:45:00Z")]
    [InlineData("2026-11-06T16:45:00", "2026-11-06T16:45:00Z")]
    public void Dates_in_common_formats(string text, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), FeedParser.ParseDate(text));
    }

    [Fact]
    public void Invalid_date_is_null()
    {
        Assert.Null(FeedParser.ParseDate("ontem à tarde"));
    }
}

public sealed class NewsTextTests
{
    [Fact]
    public void Html_is_stripped_and_truncated()
    {
        Assert.Equal("Olá mundo", HtmlText.ToPlain("<div>Olá<br/>mundo</div><style>.x{}</style>", 100));
        Assert.Equal("abcd…", HtmlText.ToPlain("abcdefghij", 5));
        Assert.Equal("p tag", HtmlText.ToPlain("&lt;p&gt;p tag&lt;/p&gt;", 100));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://a.org/x")]
    [InlineData("")]
    public void Only_http_urls_are_accepted(string url)
    {
        Assert.False(NewsUrl.TryParseHttp(url, null, out _));
    }

    [Fact]
    public void Relative_links_resolve_against_the_feed()
    {
        Assert.True(NewsUrl.TryParseHttp("/noticia/2", new Uri("https://exemplo.gov.br/rss"), out var url));
        Assert.Equal("https://exemplo.gov.br/noticia/2", url.AbsoluteUri);
    }

    [Fact]
    public void Canonical_url_ignores_scheme_www_tracking_and_trailing_slash()
    {
        var a = NewsUrl.Canonical(new Uri("https://www.Exemplo.com/a/b/?utm_source=x&id=2&fbclid=9#topo"));
        var b = NewsUrl.Canonical(new Uri("http://exemplo.com/a/b?id=2"));
        Assert.Equal("exemplo.com/a/b?id=2", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Title_key_and_story_similarity()
    {
        Assert.Equal("camara aprova reforma tributaria", NewsTitles.Key("Câmara aprova Reforma Tributária!"));
        var a = NewsTitles.Tokens(NewsTitles.Key("Câmara aprova texto-base da reforma tributária em primeiro turno"));
        var b = NewsTitles.Tokens(NewsTitles.Key("Reforma tributária: Câmara aprova texto-base em 1º turno"));
        var c = NewsTitles.Tokens(NewsTitles.Key("Nova versão do Windows chega com mudanças no menu Iniciar"));
        Assert.True(NewsTitles.SameStory(a, b));
        Assert.False(NewsTitles.SameStory(a, c));
        Assert.True(NewsTitles.Matches(NewsTitles.Key("Novidades da NASA sobre Marte"), NewsTitles.Key("nasa")));
        Assert.False(NewsTitles.Matches(NewsTitles.Key("Nasal spray"), NewsTitles.Key("nasa")));
    }

    [Fact]
    public void Trusted_sources_are_https_and_unique()
    {
        Assert.All(TrustedSources.All, s => Assert.StartsWith("https://", s.FeedUrl, StringComparison.Ordinal));
        Assert.Equal(TrustedSources.All.Count, TrustedSources.All.Select(s => s.Id).Distinct().Count());
        Assert.All(Enum.GetValues<NewsCategory>(), c => Assert.Contains(TrustedSources.All, s => s.Category == c));
    }
}

public sealed class NewsServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);

    internal sealed class MemoryNewsStore : INewsStore
    {
        public Dictionary<Guid, NewsSource> Sources { get; } = [];
        public List<NewsArticle> Articles { get; } = [];
        public IReadOnlyList<NewsSource> LoadSources() => [.. Sources.Values];
        public void UpsertSource(NewsSource source) => Sources[source.Id] = source;
        public void DeleteSource(Guid id) => Sources.Remove(id);
        public IReadOnlyList<NewsArticle> LoadArticles(DateTimeOffset since) => [.. Articles.Where(a => a.FetchedAt >= since || a.IsSaved || a.IsFavorite)];
        public int InsertArticles(IEnumerable<NewsArticle> articles)
        {
            var list = articles.Where(a => !Articles.Any(x => x.SourceId == a.SourceId && x.ItemKey == a.ItemKey)).ToList();
            Articles.AddRange(list);
            return list.Count;
        }
        public void UpdateArticleState(Guid id, bool isRead, bool isSaved, bool isFavorite)
        {
            var i = Articles.FindIndex(a => a.Id == id);
            Articles[i] = Articles[i] with { IsRead = isRead, IsSaved = isSaved, IsFavorite = isFavorite };
        }
        public int PruneArticles(DateTimeOffset before) => Articles.RemoveAll(a => a.FetchedAt < before && !a.IsSaved && !a.IsFavorite);
    }

    /// <summary>Servidor falso: responde por URL e registra as requisições (com os cabeçalhos condicionais).</summary>
    internal sealed class FakeFetcher : IFeedFetcher
    {
        public Dictionary<string, Func<string?, FetchResult>> Responses { get; } = [];
        public List<(string Url, string? ETag)> Requests { get; } = [];

        public Task<FetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            Requests.Add((url.AbsoluteUri, etag));
            return Task.FromResult(Responses.TryGetValue(url.AbsoluteUri, out var respond) ? respond(etag) : FetchResult.Fail("HTTP 404 Not Found"));
        }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    private sealed class NullSink : INotificationSink
    {
        public List<NotificationRequest> Shown { get; } = [];
        public NotificationChannelStatus Status => NotificationChannelStatus.Enabled;
        public event Action<Guid, string?>? Activated { add { } remove { } }
        public DeliveryOutcome Show(Guid id, NotificationRequest request)
        {
            Shown.Add(request);
            return DeliveryOutcome.Shown;
        }
    }

    private sealed class NullHistory : INotificationHistory
    {
        public void Add(NotificationRecord record) { }
        public bool ExistsSince(string dedupKey, DateTimeOffset since) => false;
        public int CountShownSince(NotificationCategory category, DateTimeOffset since) => 0;
        public IReadOnlyList<NotificationRecord> Recent(int count) => [];
        public int PruneBefore(DateTimeOffset cutoff) => 0;
    }

    internal static byte[] Feed(params (string Title, string Link)[] items) => Encoding.UTF8.GetBytes(
        "<rss><channel><title>F</title>" + string.Concat(items.Select(i =>
            $"<item><title>{System.Security.SecurityElement.Escape(i.Title)}</title><link>{i.Link}</link><pubDate>Fri, 06 Nov 2026 19:00:00 GMT</pubDate></item>")) + "</channel></rss>");

    private sealed record Rig(NewsService Service, FakeFetcher Fetcher, MemoryNewsStore Store, FakeTimeProvider Time, NullSink Sink, SettingsService Settings);

    private static readonly NewsSource A = TrustedSources.All[0];
    private static readonly NewsSource B = TrustedSources.All[1];

    private static Rig Create(bool enabled = true)
    {
        var time = new FakeTimeProvider(T0);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        settings.UpdateNews(n => n with { Enabled = enabled });
        var store = new MemoryNewsStore();
        // Só as duas primeiras fontes ficam ativas no teste.
        foreach (var s in TrustedSources.All.Skip(2))
        {
            store.UpsertSource(s with { Enabled = false });
        }
        var fetcher = new FakeFetcher();
        var sink = new NullSink();
        var center = new NotificationCenter(sink, new NullHistory(), settings, bus, time, NullLogger<NotificationCenter>.Instance);
        var service = new NewsService(store, fetcher, settings, center, bus, time, NullLogger<NewsService>.Instance);
        service.Start();
        return new Rig(service, fetcher, store, time, sink, settings);
    }

    [Fact]
    public async Task Disabled_module_makes_no_requests()
    {
        var rig = Create(enabled: false);
        rig.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await rig.Service.RefreshNowAsync());
        Assert.Empty(rig.Fetcher.Requests);
    }

    [Fact]
    public async Task Duplicates_are_dropped_and_similar_stories_are_grouped()
    {
        var rig = Create();
        rig.Fetcher.Responses[A.FeedUrl] = _ => new FetchResult(FetchStatus.Ok, Feed(
            ("Câmara aprova texto-base da reforma tributária em primeiro turno", "https://a.gov.br/1?utm_source=rss"),
            ("Chuva forte atinge o litoral paulista", "https://a.gov.br/2")), "\"v1\"", null, null);
        rig.Fetcher.Responses[B.FeedUrl] = _ => new FetchResult(FetchStatus.Ok, Feed(
            ("Reforma tributária: Câmara aprova texto-base em primeiro turno", "https://b.com/x"),
            ("Mesma notícia, outro feed", "http://www.a.gov.br/1/")), null, null, null);

        var added = await rig.Service.RefreshNowAsync();

        Assert.Equal(3, added); // a.gov.br/1 repetida pela URL canônica
        var reform = rig.Service.Articles.Where(a => a.Title.Contains("Reforma", StringComparison.OrdinalIgnoreCase) || a.Title.Contains("reforma", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reform.Count);
        Assert.Equal(reform[0].ClusterId, reform[1].ClusterId);
        Assert.All(rig.Service.Articles, a => Assert.Equal(T0, a.FetchedAt));
    }

    [Fact]
    public async Task Second_fetch_is_conditional_and_304_downloads_nothing()
    {
        var rig = Create();
        rig.Fetcher.Responses[A.FeedUrl] = etag => etag == "\"v1\""
            ? new FetchResult(FetchStatus.NotModified, null, etag, null, null)
            : new FetchResult(FetchStatus.Ok, Feed(("Uma notícia qualquer de teste", "https://a.gov.br/1")), "\"v1\"", null, null);
        rig.Fetcher.Responses[B.FeedUrl] = _ => new FetchResult(FetchStatus.NotModified, null, null, null, null);

        await rig.Service.RefreshNowAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(31));
        await rig.Service.RefreshNowAsync();

        Assert.Contains(rig.Fetcher.Requests, r => r.Url == A.FeedUrl && r.ETag == "\"v1\"");
        Assert.Single(rig.Service.Articles);
        Assert.Null(rig.Service.Source(A.Id)!.LastError);
    }

    [Fact]
    public async Task Manual_refresh_respects_the_per_source_minimum_interval()
    {
        var rig = Create();
        rig.Fetcher.Responses[A.FeedUrl] = _ => new FetchResult(FetchStatus.NotModified, null, null, null, null);
        rig.Fetcher.Responses[B.FeedUrl] = _ => new FetchResult(FetchStatus.NotModified, null, null, null, null);
        await rig.Service.RefreshNowAsync();
        await rig.Service.RefreshNowAsync();
        Assert.Equal(2, rig.Fetcher.Requests.Count);
    }

    [Fact]
    public async Task Failures_back_off_and_keep_the_error_visible()
    {
        var rig = Create();
        rig.Fetcher.Responses[B.FeedUrl] = _ => new FetchResult(FetchStatus.NotModified, null, null, null, null);
        // A não tem resposta → 404.
        await rig.Service.RefreshNowAsync();

        var a = rig.Service.Source(A.Id)!;
        Assert.Equal(1, a.ConsecutiveFailures);
        Assert.Contains("404", a.LastError, StringComparison.Ordinal);
        Assert.True(a.NextFetchAt >= T0 + TimeSpan.FromMinutes(27));

        Assert.Equal(TimeSpan.FromHours(24), NewsService.Backoff(A.Id, 1, TimeSpan.FromMinutes(30), TimeSpan.FromDays(3)));
        Assert.True(NewsService.Backoff(A.Id, 20, TimeSpan.FromMinutes(30), null) <= NewsService.MaxBackoff * 1.1);
    }

    [Fact]
    public async Task Invalid_feed_content_is_a_failure_not_a_crash()
    {
        var rig = Create();
        rig.Fetcher.Responses[A.FeedUrl] = _ => new FetchResult(FetchStatus.Ok, Encoding.UTF8.GetBytes("<html>erro</html>"), null, null, null);
        await rig.Service.RefreshNowAsync();
        Assert.Contains("RSS/Atom", rig.Service.Source(A.Id)!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_saved_state_persists_and_old_unsaved_articles_are_pruned()
    {
        var rig = Create();
        rig.Fetcher.Responses[A.FeedUrl] = _ => new FetchResult(FetchStatus.Ok, Feed(
            ("Primeira notícia de teste aqui", "https://a.gov.br/1"), ("Segunda notícia de teste aqui", "https://a.gov.br/2")), null, null, null);
        await rig.Service.RefreshNowAsync();
        var first = rig.Service.Articles[0];
        rig.Service.SetState(first.Id, read: true, saved: true);

        Assert.True(rig.Store.Articles.Single(a => a.Id == first.Id).IsSaved);
        Assert.Equal(1, rig.Store.PruneArticles(T0.AddDays(1)));
    }

    [Fact]
    public async Task Followed_topic_notifies_only_when_enabled()
    {
        var rig = Create();
        rig.Settings.UpdateNews(n => n with { FollowedTopics = ["NASA"], NotifyFollowedTopics = true });
        rig.Fetcher.Responses[A.FeedUrl] = _ => new FetchResult(FetchStatus.Ok, Feed(("Missão da NASA chega a Marte", "https://a.gov.br/1")), null, null, null);
        await rig.Service.RefreshNowAsync();
        var shown = Assert.Single(rig.Sink.Shown);
        Assert.Equal("Novidade sobre NASA", shown.Title);
    }

    [Fact]
    public async Task User_source_is_validated_before_being_added()
    {
        var rig = Create();
        Assert.NotNull(await rig.Service.AddSourceAsync("javascript:alert(1)", NewsCategory.Geral));
        rig.Fetcher.Responses["https://exemplo.org/feed"] = _ => new FetchResult(FetchStatus.Ok, Encoding.UTF8.GetBytes("<html/>"), null, null, null);
        Assert.Contains("não é um feed", await rig.Service.AddSourceAsync("https://exemplo.org/feed", NewsCategory.Geral), StringComparison.Ordinal);
        rig.Fetcher.Responses["https://exemplo.org/rss"] = _ => new FetchResult(FetchStatus.Ok, Feed(("x", "https://exemplo.org/1")), null, null, null);
        Assert.Null(await rig.Service.AddSourceAsync("https://exemplo.org/rss", NewsCategory.Tecnologia));
        Assert.Contains(rig.Service.Sources, s => !s.BuiltIn && s.Name == "F" && s.Category == NewsCategory.Tecnologia);
    }
}

public sealed class WeatherServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
    private static readonly WeatherLocation Campinas = new("Campinas", "São Paulo", "Brasil", -22.9, -47.06, "America/Sao_Paulo");

    private sealed class FakeProvider : IWeatherProvider
    {
        public bool Offline { get; set; }
        public int Calls { get; private set; }
        public Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WeatherLocation>>([Campinas]);
        public Task<WeatherSnapshot> ForecastAsync(WeatherLocation location, CancellationToken cancellationToken)
        {
            Calls++;
            return Offline
                ? Task.FromException<WeatherSnapshot>(new HttpRequestException("sem rede"))
                : Task.FromResult(new WeatherSnapshot(location, new CurrentWeather(26.8, 30.8, 72, 3, 3.6, 0, true), [], [], T0, "teste"));
        }
    }

    private sealed class MemoryCache : IWeatherCache
    {
        public WeatherSnapshot? Value { get; set; }
        public WeatherSnapshot? Load() => Value;
        public void Save(WeatherSnapshot snapshot) => Value = snapshot;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    private static (WeatherService Service, FakeProvider Provider, MemoryCache Cache, FakeTimeProvider Time, SettingsService Settings) Create(WeatherLocation? location)
    {
        var time = new FakeTimeProvider(T0);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        settings.UpdateWeather(w => w with { Location = location });
        var provider = new FakeProvider();
        var cache = new MemoryCache();
        var service = new WeatherService(provider, cache, settings, bus, time, NullLogger<WeatherService>.Instance);
        return (service, provider, cache, time, settings);
    }

    [Fact]
    public async Task Without_location_nothing_is_requested()
    {
        var (service, provider, _, time, _) = Create(null);
        service.Start();
        time.Advance(TimeSpan.FromHours(3));
        await service.RefreshAsync();
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Offline_keeps_the_last_forecast_with_its_time_and_an_error()
    {
        var (service, provider, cache, time, _) = Create(Campinas);
        service.Start();
        await service.RefreshAsync();
        Assert.NotNull(cache.Value);

        provider.Offline = true;
        time.Advance(TimeSpan.FromHours(3));
        await service.RefreshAsync();

        Assert.Equal(T0, service.Snapshot!.FetchedAt);
        Assert.True(service.IsStale);
        Assert.NotNull(service.LastError);
    }

    [Fact]
    public void Cached_forecast_from_another_place_is_not_shown()
    {
        var (service, _, cache, _, _) = Create(Campinas);
        cache.Value = new WeatherSnapshot(new WeatherLocation("Lisboa", null, "Portugal", 38.7, -9.1, null), new CurrentWeather(15, 15, 80, 0, 5, 0, true), [], [], T0, "teste");
        service.Start();
        Assert.Null(service.Snapshot);
    }

    [Theory]
    [InlineData(0, "Céu limpo")]
    [InlineData(63, "Chuva moderada")]
    [InlineData(95, "Trovoadas")]
    [InlineData(1234, "Condição desconhecida")]
    public void Wmo_codes_are_described(int code, string text) => Assert.Equal(text, WeatherCodes.Describe(code));
}
