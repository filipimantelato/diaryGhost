using DesktopHub.Core.Events;

namespace DesktopHub.Core.News;

/// <summary>Categorias (persistidas pelo nome — não renomeie).</summary>
public enum NewsCategory
{
    Geral,
    Politica,
    Tecnologia,
    Games,
    Ciencia,
    Desenvolvimento,
}

/// <summary>Fonte RSS/Atom. As curadas (<see cref="BuiltIn"/>) vêm em <see cref="TrustedSources"/>; o usuário pode ocultá-las, não apagá-las.</summary>
public sealed record NewsSource
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string FeedUrl { get; init; }
    public NewsCategory Category { get; init; }
    public bool BuiltIn { get; init; }
    public bool Enabled { get; init; } = true;

    // Estado da coleta
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? NextFetchAt { get; init; }
}

public sealed record NewsArticle
{
    public required Guid Id { get; init; }
    public required Guid SourceId { get; init; }
    /// <summary>Identifica o item dentro da fonte (guid/id do feed, ou o link).</summary>
    public required string ItemKey { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Summary { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Quando o app consultou a fonte e viu o item pela primeira vez.</summary>
    public required DateTimeOffset FetchedAt { get; init; }
    public required string CanonicalUrl { get; init; }
    public required string TitleKey { get; init; }
    /// <summary>Artigos sobre o mesmo acontecimento (de fontes diferentes) compartilham o grupo.</summary>
    public Guid ClusterId { get; init; }
    public bool IsRead { get; init; }
    public bool IsSaved { get; init; }
    public bool IsFavorite { get; init; }

    /// <summary>
    /// Data para ordenar: a publicação, se informada e plausível. Sem ela (alguns feeds oficiais não trazem
    /// data), conta como 12 h antes da consulta — para não ficar acima das notícias datadas do dia na primeira
    /// leitura do feed. A tela mostra "sem data de publicação".
    /// </summary>
    public DateTimeOffset SortDate => PublishedAt is { } p && p <= FetchedAt.AddHours(1) ? p : FetchedAt.AddHours(-12);
}

/// <summary>Item lido de um feed, antes de virar artigo.</summary>
public sealed record FeedItem(string? Id, string Title, string? Link, string? Summary, string? Author, DateTimeOffset? PublishedAt);

public sealed record ParsedFeed(string? Title, IReadOnlyList<FeedItem> Items);

public enum FetchStatus
{
    Ok,
    NotModified,
    Failed,
}

/// <param name="RetryAfter">Pedido do servidor (429/503) para esperar antes de tentar de novo.</param>
/// <param name="Content">Bytes crus: o parser XML respeita a codificação declarada no próprio feed.</param>
public sealed record FetchResult(FetchStatus Status, byte[]? Content, string? ETag, string? LastModified, string? Error, TimeSpan? RetryAfter = null)
{
    public static FetchResult Fail(string error, TimeSpan? retryAfter = null) => new(FetchStatus.Failed, null, null, null, error, retryAfter);
}

/// <summary>Porta: busca HTTP condicional (ETag / If-Modified-Since).</summary>
public interface IFeedFetcher
{
    Task<FetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken cancellationToken);
}

public interface INewsStore
{
    IReadOnlyList<NewsSource> LoadSources();
    void UpsertSource(NewsSource source);
    void DeleteSource(Guid id);

    /// <summary>Artigos dos últimos dias (mais recentes primeiro), já com estado de leitura.</summary>
    IReadOnlyList<NewsArticle> LoadArticles(DateTimeOffset since);
    /// <summary>Insere os novos (ignora os que já existem pela chave fonte+item). Devolve quantos entraram.</summary>
    int InsertArticles(IEnumerable<NewsArticle> articles);
    void UpdateArticleState(Guid id, bool isRead, bool isSaved, bool isFavorite);
    /// <summary>Apaga artigos antigos que não foram salvos nem favoritados.</summary>
    int PruneArticles(DateTimeOffset before);
}

/// <summary>Notícias, fontes ou estados de leitura mudaram.</summary>
public sealed record NewsUpdated(int NewArticles) : IAppEvent;
