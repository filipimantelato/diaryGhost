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
        foreach (var (txId, posting) in Read(connection, "SELECT * FROM fin_postings ORDER BY transaction_id, seq;", r =>
                     (Guid.Parse(r.GetString(r.GetOrdinal("transaction_id"))),
                      Text(r, "card_id") is { } card
                          ? Posting.ForCard(Guid.Parse(card), r.GetInt64(r.GetOrdinal("amount_minor")), ParseDate(r.GetString(r.GetOrdinal("statement_closing"))))
                          : new Posting(Guid.Parse(r.GetString(r.GetOrdinal("account_id"))), r.GetInt64(r.GetOrdinal("amount_minor"))))))
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
                PurchaseId = Text(r, "purchase_id") is { } pid ? Guid.Parse(pid) : null,
                InstallmentNumber = Long(r, "installment_number") is { } k ? (int)k : null,
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
        var rules = new Dictionary<Guid, List<CardCycleRule>>();
        foreach (var (cardId, rule) in Read(connection, "SELECT * FROM fin_card_cycle_rules ORDER BY card_id, effective_from;", r =>
                     (Guid.Parse(r.GetString(r.GetOrdinal("card_id"))),
                      new CardCycleRule(ParseDate(r.GetString(r.GetOrdinal("effective_from"))), (int)r.GetInt64(r.GetOrdinal("closing_day")), (int)r.GetInt64(r.GetOrdinal("due_day"))))))
        {
            if (!rules.TryGetValue(cardId, out var list))
            {
                rules[cardId] = list = [];
            }
            list.Add(rule);
        }
        var cards = Read(connection, "SELECT * FROM fin_cards;", r =>
        {
            var id = Guid.Parse(r.GetString(r.GetOrdinal("id")));
            return new CreditCard
            {
                Id = id,
                Name = r.GetString(r.GetOrdinal("name")),
                Institution = Text(r, "institution"),
                Network = Text(r, "network"),
                LastDigits = Text(r, "last_digits"),
                Limit = r.GetInt64(r.GetOrdinal("limit_minor")),
                Currency = r.GetString(r.GetOrdinal("currency")),
                Color = r.GetString(r.GetOrdinal("color")),
                PaymentAccountId = Text(r, "payment_account_id") is { } pa ? Guid.Parse(pa) : null,
                ClosingDayGoesNext = r.GetInt64(r.GetOrdinal("closing_day_goes_next")) == 1,
                Rules = rules.TryGetValue(id, out var list) ? list : [],
                ArchivedAt = Long(r, "archived_at_ms") is { } a ? Ms(a) : null,
                SortOrder = (int)r.GetInt64(r.GetOrdinal("sort_order")),
                CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
                UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
            };
        }).Where(c => c.Rules.Count > 0).ToList();
        var purchases = Read(connection, "SELECT * FROM fin_card_purchases;", r => new CardPurchase
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            CardId = Guid.Parse(r.GetString(r.GetOrdinal("card_id"))),
            Date = ParseDate(r.GetString(r.GetOrdinal("date"))),
            Description = r.GetString(r.GetOrdinal("description")),
            CategoryId = Text(r, "category_id") is { } c ? Guid.Parse(c) : null,
            Total = r.GetInt64(r.GetOrdinal("total_minor")),
            Installments = (int)r.GetInt64(r.GetOrdinal("installments")),
            Notes = Text(r, "notes"),
            Tags = Text(r, "tags") is { Length: > 0 } tags ? tags.Split(',') : [],
            CreatedAt = Ms(r.GetInt64(r.GetOrdinal("created_at_ms"))),
            UpdatedAt = Ms(r.GetInt64(r.GetOrdinal("updated_at_ms"))),
        });
        var overrides = Read(connection, "SELECT * FROM fin_statement_overrides;", r => new StatementOverride(
            Guid.Parse(r.GetString(r.GetOrdinal("card_id"))), ParseDate(r.GetString(r.GetOrdinal("closing_date"))), ParseDate(r.GetString(r.GetOrdinal("due_date")))));
        return new FinanceSnapshot(accounts, categories, valid) { Cards = cards, Purchases = purchases, StatementOverrides = overrides };
    }

    // ---------- Cartões ----------

    public void SaveCard(CreditCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Write((connection, tx) =>
        {
            Execute(connection, tx, """
                INSERT INTO fin_cards (id, name, institution, network, last_digits, limit_minor, currency, color, payment_account_id, closing_day_goes_next, archived_at_ms, sort_order, created_at_ms, updated_at_ms)
                VALUES ($id, $name, $institution, $network, $digits, $limit, $currency, $color, $account, $goesNext, $archived, $sort, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, institution = excluded.institution, network = excluded.network,
                    last_digits = excluded.last_digits, limit_minor = excluded.limit_minor, currency = excluded.currency, color = excluded.color,
                    payment_account_id = excluded.payment_account_id, closing_day_goes_next = excluded.closing_day_goes_next,
                    archived_at_ms = excluded.archived_at_ms, sort_order = excluded.sort_order, updated_at_ms = excluded.updated_at_ms;
                """, p =>
            {
                p.AddWithValue("$id", card.Id.ToString());
                p.AddWithValue("$name", card.Name);
                p.AddWithValue("$institution", Db(card.Institution));
                p.AddWithValue("$network", Db(card.Network));
                p.AddWithValue("$digits", Db(card.LastDigits));
                p.AddWithValue("$limit", card.Limit);
                p.AddWithValue("$currency", card.Currency);
                p.AddWithValue("$color", card.Color);
                p.AddWithValue("$account", Db(card.PaymentAccountId?.ToString()));
                p.AddWithValue("$goesNext", card.ClosingDayGoesNext ? 1 : 0);
                p.AddWithValue("$archived", Db(card.ArchivedAt?.ToUnixTimeMilliseconds()));
                p.AddWithValue("$sort", card.SortOrder);
                p.AddWithValue("$created", card.CreatedAt.ToUnixTimeMilliseconds());
                p.AddWithValue("$updated", card.UpdatedAt.ToUnixTimeMilliseconds());
            });
            Execute(connection, tx, "DELETE FROM fin_card_cycle_rules WHERE card_id = $id;", p => p.AddWithValue("$id", card.Id.ToString()));
            foreach (var rule in card.Rules)
            {
                Execute(connection, tx, "INSERT INTO fin_card_cycle_rules (card_id, effective_from, closing_day, due_day) VALUES ($card, $from, $closing, $due);", p =>
                {
                    p.AddWithValue("$card", card.Id.ToString());
                    p.AddWithValue("$from", Date(rule.EffectiveFrom));
                    p.AddWithValue("$closing", rule.ClosingDay);
                    p.AddWithValue("$due", rule.DueDay);
                });
            }
        });
    }

    public void DeleteCard(Guid id) => Write((connection, tx) =>
    {
        Execute(connection, tx, "DELETE FROM fin_cards WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));
        Audit(connection, tx, "delete", "card", id, null);
    });

    public void SavePurchase(CardPurchase purchase, IReadOnlyList<FinanceTransaction> installments)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        ArgumentNullException.ThrowIfNull(installments);
        if (installments.Count != purchase.Installments || installments.Sum(i => -i.Postings.Single().Amount) != purchase.Total)
        {
            throw new InvalidOperationException("As parcelas precisam somar o total da compra.");
        }
        Write((connection, tx) =>
        {
            Execute(connection, tx, """
                INSERT INTO fin_card_purchases (id, card_id, date, description, category_id, total_minor, installments, notes, tags, created_at_ms, updated_at_ms)
                VALUES ($id, $card, $date, $description, $category, $total, $installments, $notes, $tags, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET card_id = excluded.card_id, date = excluded.date, description = excluded.description,
                    category_id = excluded.category_id, total_minor = excluded.total_minor, installments = excluded.installments,
                    notes = excluded.notes, tags = excluded.tags, updated_at_ms = excluded.updated_at_ms;
                """, p =>
            {
                p.AddWithValue("$id", purchase.Id.ToString());
                p.AddWithValue("$card", purchase.CardId.ToString());
                p.AddWithValue("$date", Date(purchase.Date));
                p.AddWithValue("$description", purchase.Description);
                p.AddWithValue("$category", Db(purchase.CategoryId?.ToString()));
                p.AddWithValue("$total", purchase.Total);
                p.AddWithValue("$installments", purchase.Installments);
                p.AddWithValue("$notes", Db(purchase.Notes));
                p.AddWithValue("$tags", Db(purchase.Tags.Count == 0 ? null : string.Join(',', purchase.Tags)));
                p.AddWithValue("$created", purchase.CreatedAt.ToUnixTimeMilliseconds());
                p.AddWithValue("$updated", purchase.UpdatedAt.ToUnixTimeMilliseconds());
            });
            // Parcelas que deixaram de existir (menos parcelas) saem; as demais são regravadas.
            var keep = installments.Select(i => i.Id.ToString()).ToList();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                var names = keep.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)).ToList();
                cmd.CommandText = $"DELETE FROM fin_transactions WHERE purchase_id = $purchase AND id NOT IN ({string.Join(',', names)});";
                cmd.Parameters.AddWithValue("$purchase", purchase.Id.ToString());
                for (var i = 0; i < keep.Count; i++)
                {
                    cmd.Parameters.AddWithValue(names[i], keep[i]);
                }
                cmd.ExecuteNonQuery();
            }
            foreach (var installment in installments)
            {
                WriteTransaction(connection, tx, installment);
            }
        });
    }

    public void DeletePurchase(Guid id, DateTimeOffset at) => Write((connection, tx) =>
    {
        // As parcelas (e suas partidas) saem em cascata.
        Execute(connection, tx, "DELETE FROM fin_card_purchases WHERE id = $id;", p => p.AddWithValue("$id", id.ToString()));
        Audit(connection, tx, "delete", "purchase", id, null, at);
    });

    public void SaveStatementOverride(StatementOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write((connection, tx) => Execute(connection, tx, """
            INSERT INTO fin_statement_overrides (card_id, closing_date, due_date) VALUES ($card, $closing, $due)
            ON CONFLICT(card_id, closing_date) DO UPDATE SET due_date = excluded.due_date;
            """, p =>
        {
            p.AddWithValue("$card", value.CardId.ToString());
            p.AddWithValue("$closing", Date(value.Closing));
            p.AddWithValue("$due", Date(value.Due));
        }));
    }

    public void SaveTransactions(IReadOnlyList<FinanceTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        transactions.ToList().ForEach(CheckInvariants);
        Write((connection, tx) =>
        {
            foreach (var t in transactions)
            {
                WriteTransaction(connection, tx, t);
            }
        });
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
        CheckInvariants(transaction);
        Write((connection, tx) => WriteTransaction(connection, tx, transaction));
    }

    /// <summary>Última barreira: as invariantes que o serviço já garante, conferidas de novo antes de gravar.</summary>
    private static void CheckInvariants(FinanceTransaction transaction)
    {
        if (transaction.Postings.Count is < 1 or > 2 || (transaction.Kind == TransactionKind.Transfer) != (transaction.Postings.Count == 2))
        {
            throw new InvalidOperationException("Número de partidas incompatível com o tipo do lançamento.");
        }
        if (transaction.Kind == TransactionKind.Transfer && transaction.Postings.Sum(p => p.Amount) != 0)
        {
            throw new InvalidOperationException("As partidas de uma transferência precisam somar zero.");
        }
        if (transaction.Postings.Any(p => p.IsCard && p.StatementClosing is null))
        {
            throw new InvalidOperationException("Partida de cartão sem fatura.");
        }
    }

    private static void WriteTransaction(SqliteConnection connection, SqliteTransaction tx, FinanceTransaction transaction)
    {
        CheckInvariants(transaction);
        Execute(connection, tx, """
            INSERT INTO fin_transactions (id, kind, date, description, category_id, status, payment_method, notes, tags, refund_of_id, purchase_id, installment_number, created_at_ms, updated_at_ms)
            VALUES ($id, $kind, $date, $description, $category, $status, $method, $notes, $tags, $refundOf, $purchase, $installment, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, date = excluded.date, description = excluded.description,
                category_id = excluded.category_id, status = excluded.status, payment_method = excluded.payment_method,
                notes = excluded.notes, tags = excluded.tags, refund_of_id = excluded.refund_of_id, purchase_id = excluded.purchase_id,
                installment_number = excluded.installment_number, updated_at_ms = excluded.updated_at_ms;
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
            p.AddWithValue("$purchase", Db(transaction.PurchaseId?.ToString()));
            p.AddWithValue("$installment", Db(transaction.InstallmentNumber));
            p.AddWithValue("$created", transaction.CreatedAt.ToUnixTimeMilliseconds());
            p.AddWithValue("$updated", transaction.UpdatedAt.ToUnixTimeMilliseconds());
        });
        Execute(connection, tx, "DELETE FROM fin_postings WHERE transaction_id = $id;", p => p.AddWithValue("$id", transaction.Id.ToString()));
        for (var i = 0; i < transaction.Postings.Count; i++)
        {
            var posting = transaction.Postings[i];
            var seq = i;
            Execute(connection, tx, "INSERT INTO fin_postings (transaction_id, seq, account_id, card_id, statement_closing, amount_minor) VALUES ($tx, $seq, $account, $card, $closing, $amount);", p =>
            {
                p.AddWithValue("$tx", transaction.Id.ToString());
                p.AddWithValue("$seq", seq);
                p.AddWithValue("$account", Db(posting.IsCard ? null : posting.AccountId.ToString()));
                p.AddWithValue("$card", Db(posting.CardId?.ToString()));
                p.AddWithValue("$closing", Db(posting.StatementClosing is { } c ? Date(c) : null));
                p.AddWithValue("$amount", posting.Amount);
            });
        }
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
