using System.Globalization;
using DesktopHub.Core.Finance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Finance;

/// <summary>
/// Persistência do módulo de finanças. Cada operação é uma transação SQLite: um lançamento e suas partidas
/// entram ou saem juntos. Linhas ilegíveis são ignoradas e registradas (sem conteúdo), nunca derrubam o app.
/// </summary>
public sealed partial class SqliteFinanceStore(FinanceDatabase database, ILogger<SqliteFinanceStore> logger) : IFinanceStore
{
    private const string DateFormat = "yyyy-MM-dd";

    internal static string Date(DateOnly d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

    internal static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, DateFormat, CultureInfo.InvariantCulture);

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static object Db(long? value) => value is null ? DBNull.Value : value.Value;

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? Long(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static DateTimeOffset Ms(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public FinanceSnapshot Load()
    {
        using var connection = database.OpenConnection();
        var accounts = Read(connection, "SELECT * FROM fin_accounts;", r => new FinancialAccount
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            Name = r.GetString(r.GetOrdinal("name")),
            Kind = Enum.Parse<AccountKind>(r.GetString(r.GetOrdinal("kind"))),
            Institution = Text(r, "institution"),
            Currency = r.GetString(r.GetOrdinal("currency")),
            OpeningBalance = r.GetInt64(r.GetOrdinal("opening_balance_minor")),
            OpeningDate = ParseDate(r.GetString(r.GetOrdinal("opening_date"))),
            Color = r.GetString(r.GetOrdinal("color")),
            IncludeInAvailable = r.GetInt64(r.GetOrdinal("include_in_available")) == 1,
            ArchivedAt = Long(r, "archived_at_ms") is { } a ? Ms(a) : null,
            SortOrder = (int)r.GetInt64(r.GetOrdinal("sort_order")),
            CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
            UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
        });
        var categories = Read(connection, "SELECT * FROM fin_categories;", r => new TransactionCategory
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            Name = r.GetString(r.GetOrdinal("name")),
            Kind = Enum.Parse<CategoryKind>(r.GetString(r.GetOrdinal("kind"))),
            ParentId = Text(r, "parent_id") is { } p ? Guid.Parse(p) : null,
            Color = r.GetString(r.GetOrdinal("color")),
            ArchivedAt = Long(r, "archived_at_ms") is { } a ? Ms(a) : null,
            SortOrder = (int)r.GetInt64(r.GetOrdinal("sort_order")),
        });

        var postings = new Dictionary<Guid, List<Posting>>();
        foreach (var (txId, posting) in Read(connection, "SELECT * FROM fin_postings WHERE account_id IS NOT NULL ORDER BY transaction_id, seq;", r =>
                     (Guid.Parse(r.GetString(r.GetOrdinal("transaction_id"))),
                      new Posting(Guid.Parse(r.GetString(r.GetOrdinal("account_id"))), r.GetInt64(r.GetOrdinal("amount_minor"))))))
        {
            if (!postings.TryGetValue(txId, out var list))
            {
                postings[txId] = list = [];
            }
            list.Add(posting);
        }

        var transactions = Read(connection, "SELECT * FROM fin_transactions;", r =>
        {
            var id = Guid.Parse(r.GetString(r.GetOrdinal("id")));
            return new FinanceTransaction
            {
                Id = id,
                Kind = Enum.Parse<TransactionKind>(r.GetString(r.GetOrdinal("kind"))),
                Date = ParseDate(r.GetString(r.GetOrdinal("date"))),
                Description = r.GetString(r.GetOrdinal("description")),
                CategoryId = Text(r, "category_id") is { } c ? Guid.Parse(c) : null,
                Status = Enum.Parse<TransactionStatus>(r.GetString(r.GetOrdinal("status"))),
                PaymentMethod = Enum.TryParse<PaymentMethod>(r.GetString(r.GetOrdinal("payment_method")), out var pm) ? pm : PaymentMethod.Other,
                Notes = Text(r, "notes"),
                Tags = Text(r, "tags") is { Length: > 0 } tags ? tags.Split(',') : [],
                RefundOfId = Text(r, "refund_of_id") is { } of ? Guid.Parse(of) : null,
                Postings = postings.TryGetValue(id, out var list) ? list : [],
                CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
                UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
            };
        });
        // Transação sem partidas (não deveria existir: são gravadas juntas) não é exibida nem somada.
        var valid = transactions.Where(t => t.Postings.Count > 0).ToList();
        if (valid.Count != transactions.Count)
        {
            LogInvalidRows(transactions.Count - valid.Count);
        }
        return new FinanceSnapshot(accounts, categories, valid);
    }

    public void SaveAccount(FinancialAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Write((connection, tx) => Execute(connection, tx, """
            INSERT INTO fin_accounts (id, name, kind, institution, currency, opening_balance_minor, opening_date, color, include_in_available, archived_at_ms, sort_order, created_at_ms, updated_at_ms)
            VALUES ($id, $name, $kind, $institution, $currency, $opening, $openingDate, $color, $available, $archived, $sort, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, kind = excluded.kind, institution = excluded.institution, currency = excluded.currency,
                opening_balance_minor = excluded.opening_balance_minor, opening_date = excluded.opening_date, color = excluded.color,
                include_in_available = excluded.include_in_available, archived_at_ms = excluded.archived_at_ms, sort_order = excluded.sort_order,
                updated_at_ms = excluded.updated_at_ms;
            """, p =>
        {
            p.AddWithValue("$id", account.Id.ToString());
            p.AddWithValue("$name", account.Name);
            p.AddWithValue("$kind", account.Kind.ToString());
            p.AddWithValue("$institution", Db(account.Institution));
            p.AddWithValue("$currency", account.Currency);
            p.AddWithValue("$opening", account.OpeningBalance);
            p.AddWithValue("$openingDate", Date(account.OpeningDate));
            p.AddWithValue("$color", account.Color);
            p.AddWithValue("$available", account.IncludeInAvailable ? 1 : 0);
            p.AddWithValue("$archived", Db(account.ArchivedAt?.ToUnixTimeMilliseconds()));
            p.AddWithValue("$sort", account.SortOrder);
            p.AddWithValue("$created", account.CreatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$updated", account.UpdatedAt.ToUnixTimeMilliseconds());
        }));
    }

    public void DeleteAccount(Guid id) => Write((connection, tx) =>
    {
        Execute(connection, tx, "DELETE FROM fin_accounts WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));
        Audit(connection, tx, "delete", "account", id, null);
    });

    public void SaveCategory(TransactionCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        Write((connection, tx) => Execute(connection, tx, """
            INSERT INTO fin_categories (id, name, kind, parent_id, color, archived_at_ms, sort_order)
            VALUES ($id, $name, $kind, $parent, $color, $archived, $sort)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, kind = excluded.kind, parent_id = excluded.parent_id, color = excluded.color,
                archived_at_ms = excluded.archived_at_ms, sort_order = excluded.sort_order;
            """, p =>
        {
            p.AddWithValue("$id", category.Id.ToString());
            p.AddWithValue("$name", category.Name);
            p.AddWithValue("$kind", category.Kind.ToString());
            p.AddWithValue("$parent", Db(category.ParentId?.ToString()));
            p.AddWithValue("$color", category.Color);
            p.AddWithValue("$archived", Db(category.ArchivedAt?.ToUnixTimeMilliseconds()));
            p.AddWithValue("$sort", category.SortOrder);
        }));
    }

    public void DeleteCategory(Guid id) => Write((connection, tx) =>
    {
        Execute(connection, tx, "DELETE FROM fin_categories WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));
        Audit(connection, tx, "delete", "category", id, null);
    });

    public void SaveTransaction(FinanceTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Última barreira: as invariantes que o serviço já garante, conferidas de novo antes de gravar.
        if (transaction.Postings.Count is < 1 or > 2 || (transaction.Kind == TransactionKind.Transfer) != (transaction.Postings.Count == 2))
        {
            throw new InvalidOperationException("Número de partidas incompatível com o tipo do lançamento.");
        }
        if (transaction.Kind == TransactionKind.Transfer && transaction.Postings.Sum(p => p.Amount) != 0)
        {
            throw new InvalidOperationException("As partidas de uma transferência precisam somar zero.");
        }
        Write((connection, tx) =>
        {
            Execute(connection, tx, """
                INSERT INTO fin_transactions (id, kind, date, description, category_id, status, payment_method, notes, tags, refund_of_id, created_at_ms, updated_at_ms)
                VALUES ($id, $kind, $date, $description, $category, $status, $method, $notes, $tags, $refundOf, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, date = excluded.date, description = excluded.description,
                    category_id = excluded.category_id, status = excluded.status, payment_method = excluded.payment_method,
                    notes = excluded.notes, tags = excluded.tags, refund_of_id = excluded.refund_of_id, updated_at_ms = excluded.updated_at_ms;
                """, p =>
            {
                p.AddWithValue("$id", transaction.Id.ToString());
                p.AddWithValue("$kind", transaction.Kind.ToString());
                p.AddWithValue("$date", Date(transaction.Date));
                p.AddWithValue("$description", transaction.Description);
                p.AddWithValue("$category", Db(transaction.CategoryId?.ToString()));
                p.AddWithValue("$status", transaction.Status.ToString());
                p.AddWithValue("$method", transaction.PaymentMethod.ToString());
                p.AddWithValue("$notes", Db(transaction.Notes));
                p.AddWithValue("$tags", Db(transaction.Tags.Count == 0 ? null : string.Join(',', transaction.Tags)));
                p.AddWithValue("$refundOf", Db(transaction.RefundOfId?.ToString()));
                p.AddWithValue("$created", transaction.CreatedAt.ToUnixTimeMilliseconds());
                p.AddWithValue("$updated", transaction.UpdatedAt.ToUnixTimeMilliseconds());
            });
            Execute(connection, tx, "DELETE FROM fin_postings WHERE transaction_id = $id;", p => p.AddWithValue("$id", transaction.Id.ToString()));
            for (var i = 0; i < transaction.Postings.Count; i++)
            {
                var posting = transaction.Postings[i];
                var seq = i;
                Execute(connection, tx, "INSERT INTO fin_postings (transaction_id, seq, account_id, amount_minor) VALUES ($tx, $seq, $account, $amount);", p =>
                {
                    p.AddWithValue("$tx", transaction.Id.ToString());
                    p.AddWithValue("$seq", seq);
                    p.AddWithValue("$account", posting.AccountId.ToString());
                    p.AddWithValue("$amount", posting.Amount);
                });
            }
        });
    }

    public void DeleteTransaction(Guid id, DateTimeOffset at) => Write((connection, tx) =>
    {
        string? kind = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT kind FROM fin_transactions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            kind = cmd.ExecuteScalar() as string;
        }
        Execute(connection, tx, "DELETE FROM fin_transactions WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));
        Audit(connection, tx, "delete", "transaction", id, kind, at);
    });

    /// <summary>Exclusões registradas (para conferência; sem valores nem descrições).</summary>
    public IReadOnlyList<(DateTimeOffset At, string Action, string Entity, Guid EntityId, string? Detail)> AuditLog()
    {
        using var connection = database.OpenConnection();
        return Read(connection, "SELECT * FROM fin_audit ORDER BY at_ms;", r =>
            (Ms(r.GetInt64(r.GetOrdinal("at_ms"))), r.GetString(r.GetOrdinal("action")), r.GetString(r.GetOrdinal("entity")),
             Guid.Parse(r.GetString(r.GetOrdinal("entity_id"))), Text(r, "detail")));
    }

    private static void Audit(SqliteConnection connection, SqliteTransaction tx, string action, string entity, Guid id, string? detail, DateTimeOffset? at = null) =>
        Execute(connection, tx, "INSERT INTO fin_audit (at_ms, action, entity, entity_id, detail) VALUES ($at, $action, $entity, $id, $detail);", p =>
        {
            p.AddWithValue("$at", (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
            p.AddWithValue("$action", action);
            p.AddWithValue("$entity", entity);
            p.AddWithValue("$id", id.ToString());
            p.AddWithValue("$detail", Db(detail));
        });

    // ---------- Suporte ----------

    private void Write(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        work(connection, tx);
        tx.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, Action<SqliteParameterCollection> bind)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind(cmd.Parameters);
        cmd.ExecuteNonQuery();
    }

    private List<T> Read<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        var invalid = 0;
        while (reader.Read())
        {
            try
            {
                list.Add(map(reader));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidCastException)
            {
                invalid++;
            }
        }
        if (invalid > 0)
        {
            LogInvalidRows(invalid);
        }
        return list;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} registro(s) de finanças ilegível(is) ignorado(s)")]
    private partial void LogInvalidRows(int count);
}
