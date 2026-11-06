using DesktopHub.Core.News;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DesktopHub.Infrastructure.News;

public sealed class SqliteNewsStore(SqliteDatabase database) : INewsStore
{
    private static object Db(string? v) => v is null ? DBNull.Value : v;

    private static object Db(DateTimeOffset? v) => v is { } d ? d.ToUnixTimeMilliseconds() : DBNull.Value;

    private static DateTimeOffset? Ms(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    }

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public IReadOnlyList<NewsSource> LoadSources()
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM news_sources;";
        using var r = cmd.ExecuteReader();
        var list = new List<NewsSource>();
        while (r.Read())
        {
            if (!Enum.TryParse<NewsCategory>(r.GetString(r.GetOrdinal("category")), out var category))
            {
                category = NewsCategory.Geral;
            }
            list.Add(new NewsSource
            {
                Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                Name = r.GetString(r.GetOrdinal("name")),
                FeedUrl = r.GetString(r.GetOrdinal("feed_url")),
                Category = category,
                BuiltIn = r.GetInt64(r.GetOrdinal("built_in")) == 1,
                Enabled = r.GetInt64(r.GetOrdinal("enabled")) == 1,
                ETag = Text(r, "etag"),
                LastModified = Text(r, "last_modified"),
                LastAttemptAt = Ms(r, "last_attempt_at_ms"),
                LastSuccessAt = Ms(r, "last_success_at_ms"),
                LastError = Text(r, "last_error"),
                ConsecutiveFailures = (int)r.GetInt64(r.GetOrdinal("consecutive_failures")),
                NextFetchAt = Ms(r, "next_fetch_at_ms"),
            });
        }
        return list;
    }

    public void UpsertSource(NewsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO news_sources (id, name, feed_url, category, built_in, enabled, etag, last_modified, last_attempt_at_ms,
                last_success_at_ms, last_error, consecutive_failures, next_fetch_at_ms)
            VALUES ($id, $name, $url, $category, $builtIn, $enabled, $etag, $lastModified, $attempt, $success, $error, $failures, $next)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, feed_url = excluded.feed_url, category = excluded.category,
                built_in = excluded.built_in, enabled = excluded.enabled, etag = excluded.etag, last_modified = excluded.last_modified,
                last_attempt_at_ms = excluded.last_attempt_at_ms, last_success_at_ms = excluded.last_success_at_ms,
                last_error = excluded.last_error, consecutive_failures = excluded.consecutive_failures, next_fetch_at_ms = excluded.next_fetch_at_ms;
            """;
        var p = cmd.Parameters;
        p.AddWithValue("$id", source.Id.ToString());
        p.AddWithValue("$name", source.Name);
        p.AddWithValue("$url", source.FeedUrl);
        p.AddWithValue("$category", source.Category.ToString());
        p.AddWithValue("$builtIn", source.BuiltIn ? 1 : 0);
        p.AddWithValue("$enabled", source.Enabled ? 1 : 0);
        p.AddWithValue("$etag", Db(source.ETag));
        p.AddWithValue("$lastModified", Db(source.LastModified));
        p.AddWithValue("$attempt", Db(source.LastAttemptAt));
        p.AddWithValue("$success", Db(source.LastSuccessAt));
        p.AddWithValue("$error", Db(source.LastError is { Length: > 500 } e ? e[..500] : source.LastError));
        p.AddWithValue("$failures", source.ConsecutiveFailures);
        p.AddWithValue("$next", Db(source.NextFetchAt));
        cmd.ExecuteNonQuery();
    }

    public void DeleteSource(Guid id)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        // Fontes curadas não são apagadas (só ocultadas). Os artigos da fonte saem junto (FK em cascata).
        cmd.CommandText = "DELETE FROM news_sources WHERE id = $id AND built_in = 0;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<NewsArticle> LoadArticles(DateTimeOffset since)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM news_articles WHERE fetched_at_ms >= $since OR is_saved = 1 OR is_favorite = 1 ORDER BY fetched_at_ms DESC;";
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var list = new List<NewsArticle>();
        while (r.Read())
        {
            list.Add(new NewsArticle
            {
                Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                SourceId = Guid.Parse(r.GetString(r.GetOrdinal("source_id"))),
                ItemKey = r.GetString(r.GetOrdinal("item_key")),
                Title = r.GetString(r.GetOrdinal("title")),
                Url = r.GetString(r.GetOrdinal("url")),
                Summary = Text(r, "summary"),
                Author = Text(r, "author"),
                PublishedAt = Ms(r, "published_at_ms"),
                FetchedAt = Ms(r, "fetched_at_ms")!.Value,
                CanonicalUrl = r.GetString(r.GetOrdinal("canonical_url")),
                TitleKey = r.GetString(r.GetOrdinal("title_key")),
                ClusterId = Guid.Parse(r.GetString(r.GetOrdinal("cluster_id"))),
                IsRead = r.GetInt64(r.GetOrdinal("is_read")) == 1,
                IsSaved = r.GetInt64(r.GetOrdinal("is_saved")) == 1,
                IsFavorite = r.GetInt64(r.GetOrdinal("is_favorite")) == 1,
            });
        }
        return list;
    }

    public int InsertArticles(IEnumerable<NewsArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO news_articles (id, source_id, item_key, title, url, summary, author, published_at_ms, fetched_at_ms,
                canonical_url, title_key, cluster_id, is_read, is_saved, is_favorite)
            VALUES ($id, $source, $key, $title, $url, $summary, $author, $published, $fetched, $canonical, $titleKey, $cluster, $read, $saved, $favorite);
            """;
        string[] names = ["$id", "$source", "$key", "$title", "$url", "$summary", "$author", "$published", "$fetched", "$canonical", "$titleKey", "$cluster", "$read", "$saved", "$favorite"];
        var parameters = names.Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        var inserted = 0;
        foreach (var a in articles)
        {
            object[] values =
            [
                a.Id.ToString(), a.SourceId.ToString(), a.ItemKey, a.Title, a.Url, Db(a.Summary), Db(a.Author), Db(a.PublishedAt),
                a.FetchedAt.ToUnixTimeMilliseconds(), a.CanonicalUrl, a.TitleKey, a.ClusterId.ToString(), a.IsRead ? 1 : 0, a.IsSaved ? 1 : 0, a.IsFavorite ? 1 : 0,
            ];
            for (var i = 0; i < values.Length; i++)
            {
                parameters[i].Value = values[i];
            }
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    public void UpdateArticleState(Guid id, bool isRead, bool isSaved, bool isFavorite)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE news_articles SET is_read = $read, is_saved = $saved, is_favorite = $favorite WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        cmd.Parameters.AddWithValue("$saved", isSaved ? 1 : 0);
        cmd.Parameters.AddWithValue("$favorite", isFavorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public int PruneArticles(DateTimeOffset before)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM news_articles WHERE fetched_at_ms < $before AND is_saved = 0 AND is_favorite = 0;";
        cmd.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }
}
