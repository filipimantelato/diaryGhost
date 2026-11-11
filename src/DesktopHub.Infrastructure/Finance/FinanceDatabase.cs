using DesktopHub.Core;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Finance;

/// <summary>
/// <c>finance.db</c>: arquivo próprio do módulo (ADR-028), com o mesmo tratamento do banco principal —
/// verificação de integridade, quarentena, backup antes de migrar e migrações versionadas.
/// </summary>
public sealed class FinanceDatabase(AppPaths paths, ILogger<SqliteDatabase> logger)
{
    private readonly SqliteDatabase _inner = new(paths, logger, paths.FinanceDatabaseFile, FinanceMigrations.All);

    public string FilePath => _inner.FilePath;

    public DatabaseStartupReport Initialize() => _inner.Initialize();

    public SqliteConnection OpenConnection() => _inner.OpenConnection();
}

/// <summary>
/// Migrações do <c>finance.db</c>. Regras de sempre: nunca editar uma migração publicada.
/// Dinheiro em INTEGER (unidades mínimas da moeda); datas financeiras em TEXT <c>yyyy-MM-dd</c>;
/// carimbos em milissegundos Unix UTC; IDs em TEXT (GUID v7).
/// </summary>
public static class FinanceMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "fundacao_financeira", """
            CREATE TABLE fin_accounts (
                id                    TEXT    NOT NULL PRIMARY KEY,
                name                  TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
                kind                  TEXT    NOT NULL CHECK (kind IN ('Checking', 'Savings', 'DigitalWallet', 'Cash', 'Investment', 'Other')),
                institution           TEXT,
                currency              TEXT    NOT NULL CHECK (length(currency) = 3),
                opening_balance_minor INTEGER NOT NULL,
                opening_date          TEXT    NOT NULL CHECK (opening_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                color                 TEXT    NOT NULL,
                include_in_available  INTEGER NOT NULL CHECK (include_in_available IN (0, 1)),
                archived_at_ms        INTEGER,
                sort_order            INTEGER NOT NULL,
                created_at_ms         INTEGER NOT NULL,
                updated_at_ms         INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE fin_categories (
                id              TEXT    NOT NULL PRIMARY KEY,
                name            TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
                kind            TEXT    NOT NULL CHECK (kind IN ('Expense', 'Income', 'System')),
                parent_id       TEXT    REFERENCES fin_categories (id) ON DELETE RESTRICT,
                color           TEXT    NOT NULL,
                archived_at_ms  INTEGER,
                sort_order      INTEGER NOT NULL,
                CHECK (parent_id IS NULL OR parent_id <> id)
            ) STRICT;

            CREATE TABLE fin_transactions (
                id              TEXT    NOT NULL PRIMARY KEY,
                kind            TEXT    NOT NULL CHECK (kind IN ('Income', 'Expense', 'Transfer', 'Refund', 'Adjustment')),
                date            TEXT    NOT NULL CHECK (date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                description     TEXT    NOT NULL CHECK (length(description) BETWEEN 1 AND 200),
                category_id     TEXT    REFERENCES fin_categories (id) ON DELETE RESTRICT,
                status          TEXT    NOT NULL CHECK (status IN ('Pending', 'Cleared')),
                payment_method  TEXT    NOT NULL,
                notes           TEXT,
                tags            TEXT,
                refund_of_id    TEXT    REFERENCES fin_transactions (id) ON DELETE SET NULL,
                created_at_ms   INTEGER NOT NULL,
                updated_at_ms   INTEGER NOT NULL,
                CHECK (kind <> 'Transfer' OR category_id IS NULL)
            ) STRICT;
            CREATE INDEX ix_fin_transactions_date ON fin_transactions (date);
            CREATE INDEX ix_fin_transactions_category ON fin_transactions (category_id);

            -- Partidas: o efeito de cada transação em uma conta (+ entra, − sai). card_id fica reservado para os
            -- cartões (Fase 10): cada partida aponta para exatamente um dos dois.
            CREATE TABLE fin_postings (
                transaction_id  TEXT    NOT NULL REFERENCES fin_transactions (id) ON DELETE CASCADE,
                seq             INTEGER NOT NULL CHECK (seq IN (0, 1)),
                account_id      TEXT    REFERENCES fin_accounts (id) ON DELETE RESTRICT,
                card_id         TEXT,
                amount_minor    INTEGER NOT NULL CHECK (amount_minor <> 0),
                PRIMARY KEY (transaction_id, seq),
                CHECK ((account_id IS NULL) <> (card_id IS NULL))
            ) STRICT;
            CREATE INDEX ix_fin_postings_account ON fin_postings (account_id);

            -- Registro de exclusões, sem valores nem descrições (só o que, quando e o tipo).
            CREATE TABLE fin_audit (
                at_ms        INTEGER NOT NULL,
                action       TEXT    NOT NULL,
                entity       TEXT    NOT NULL,
                entity_id    TEXT    NOT NULL,
                detail       TEXT
            ) STRICT;
            """),
    ];
}
