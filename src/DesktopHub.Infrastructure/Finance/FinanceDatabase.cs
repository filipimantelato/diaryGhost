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

        new(2, "cartoes_de_credito", """
            CREATE TABLE fin_cards (
                id                     TEXT    NOT NULL PRIMARY KEY,
                name                   TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
                institution            TEXT,
                network                TEXT,
                last_digits            TEXT    CHECK (last_digits IS NULL OR (length(last_digits) = 4 AND last_digits GLOB '[0-9][0-9][0-9][0-9]')),
                limit_minor            INTEGER NOT NULL CHECK (limit_minor >= 0),
                currency               TEXT    NOT NULL CHECK (length(currency) = 3),
                color                  TEXT    NOT NULL,
                payment_account_id     TEXT    REFERENCES fin_accounts (id) ON DELETE SET NULL,
                closing_day_goes_next  INTEGER NOT NULL CHECK (closing_day_goes_next IN (0, 1)),
                archived_at_ms         INTEGER,
                sort_order             INTEGER NOT NULL,
                created_at_ms          INTEGER NOT NULL,
                updated_at_ms          INTEGER NOT NULL
            ) STRICT;

            -- Regras de ciclo com vigência: mudar fechamento/vencimento não move faturas passadas.
            CREATE TABLE fin_card_cycle_rules (
                card_id         TEXT    NOT NULL REFERENCES fin_cards (id) ON DELETE CASCADE,
                effective_from  TEXT    NOT NULL,
                closing_day     INTEGER NOT NULL CHECK (closing_day BETWEEN 1 AND 31),
                due_day         INTEGER NOT NULL CHECK (due_day BETWEEN 1 AND 31),
                PRIMARY KEY (card_id, effective_from)
            ) STRICT;

            CREATE TABLE fin_card_purchases (
                id              TEXT    NOT NULL PRIMARY KEY,
                card_id         TEXT    NOT NULL REFERENCES fin_cards (id) ON DELETE RESTRICT,
                date            TEXT    NOT NULL CHECK (date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                description     TEXT    NOT NULL CHECK (length(description) BETWEEN 1 AND 200),
                category_id     TEXT    REFERENCES fin_categories (id) ON DELETE RESTRICT,
                total_minor     INTEGER NOT NULL CHECK (total_minor > 0),
                installments    INTEGER NOT NULL CHECK (installments BETWEEN 1 AND 72 AND installments <= total_minor),
                notes           TEXT,
                tags            TEXT,
                created_at_ms   INTEGER NOT NULL,
                updated_at_ms   INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE fin_statement_overrides (
                card_id       TEXT NOT NULL REFERENCES fin_cards (id) ON DELETE CASCADE,
                closing_date  TEXT NOT NULL,
                due_date      TEXT NOT NULL CHECK (due_date > closing_date),
                PRIMARY KEY (card_id, closing_date)
            ) STRICT;

            -- Parcelas: lançamentos ligados à compra (apagados junto com ela).
            ALTER TABLE fin_transactions ADD COLUMN purchase_id TEXT REFERENCES fin_card_purchases (id) ON DELETE CASCADE;
            ALTER TABLE fin_transactions ADD COLUMN installment_number INTEGER CHECK (installment_number IS NULL OR installment_number BETWEEN 1 AND 72);
            CREATE UNIQUE INDEX ux_fin_transactions_installment ON fin_transactions (purchase_id, installment_number) WHERE purchase_id IS NOT NULL;

            -- Partidas de cartão guardam a fatura (data de fechamento) a que pertencem.
            ALTER TABLE fin_postings ADD COLUMN statement_closing TEXT CHECK (statement_closing IS NULL OR statement_closing GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]');
            CREATE INDEX ix_fin_postings_card ON fin_postings (card_id, statement_closing);

            -- card_id não pode ter chave estrangeira por ALTER: gatilhos garantem que aponta para um cartão
            -- existente e que partida de cartão sempre tem fatura.
            CREATE TRIGGER trg_fin_postings_card_insert BEFORE INSERT ON fin_postings
            WHEN NEW.card_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'cartão inexistente') WHERE NOT EXISTS (SELECT 1 FROM fin_cards WHERE id = NEW.card_id);
                SELECT RAISE(ABORT, 'partida de cartão sem fatura') WHERE NEW.statement_closing IS NULL;
            END;
            CREATE TRIGGER trg_fin_cards_delete BEFORE DELETE ON fin_cards
            BEGIN
                SELECT RAISE(ABORT, 'cartão com lançamentos') WHERE EXISTS (SELECT 1 FROM fin_postings WHERE card_id = OLD.id);
            END;
            """),

        new(3, "planejamento", """
            -- Recorrências: o modelo (conta, assinatura, receita). Ocorrências previstas são calculadas; só a confirmação
            -- vira lançamento, com (recurring_id, occurrence_date) único.
            CREATE TABLE fin_recurring (
                id                  TEXT    NOT NULL PRIMARY KEY,
                kind                TEXT    NOT NULL CHECK (kind IN ('Bill', 'Subscription', 'Income')),
                description         TEXT    NOT NULL CHECK (length(description) BETWEEN 1 AND 200),
                amount_minor        INTEGER NOT NULL CHECK (amount_minor > 0),
                estimated           INTEGER NOT NULL CHECK (estimated IN (0, 1)),
                account_id          TEXT    REFERENCES fin_accounts (id) ON DELETE RESTRICT,
                card_id             TEXT    REFERENCES fin_cards (id) ON DELETE RESTRICT,
                category_id         TEXT    REFERENCES fin_categories (id) ON DELETE RESTRICT,
                frequency           TEXT    NOT NULL CHECK (frequency IN ('Weekly', 'Monthly', 'Yearly')),
                interval            INTEGER NOT NULL CHECK (interval BETWEEN 1 AND 24),
                start_date          TEXT    NOT NULL CHECK (start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                end_date            TEXT    CHECK (end_date IS NULL OR end_date >= start_date),
                remind_days_before  INTEGER NOT NULL CHECK (remind_days_before BETWEEN 0 AND 30),
                notes               TEXT,
                archived_at_ms      INTEGER,
                created_at_ms       INTEGER NOT NULL,
                updated_at_ms       INTEGER NOT NULL,
                CHECK ((account_id IS NULL) <> (card_id IS NULL)),
                CHECK (card_id IS NULL OR kind <> 'Income')
            ) STRICT;

            CREATE TABLE fin_recurring_skips (
                recurring_id     TEXT NOT NULL REFERENCES fin_recurring (id) ON DELETE CASCADE,
                occurrence_date  TEXT NOT NULL,
                PRIMARY KEY (recurring_id, occurrence_date)
            ) STRICT;

            ALTER TABLE fin_transactions ADD COLUMN recurring_id TEXT REFERENCES fin_recurring (id) ON DELETE SET NULL;
            ALTER TABLE fin_transactions ADD COLUMN occurrence_date TEXT CHECK (occurrence_date IS NULL OR occurrence_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]');
            CREATE UNIQUE INDEX ux_fin_transactions_occurrence ON fin_transactions (recurring_id, occurrence_date) WHERE recurring_id IS NOT NULL;

            -- Orçamento por categoria principal: month NULL = padrão de todo mês; um mês específico substitui o padrão.
            CREATE TABLE fin_budgets (
                id            TEXT    NOT NULL PRIMARY KEY,
                category_id   TEXT    NOT NULL REFERENCES fin_categories (id) ON DELETE CASCADE,
                month         TEXT    CHECK (month IS NULL OR month GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-01'),
                limit_minor   INTEGER NOT NULL CHECK (limit_minor >= 0),
                warn_percent  INTEGER NOT NULL CHECK (warn_percent BETWEEN 1 AND 100),
                CHECK (month IS NOT NULL OR limit_minor > 0)
            ) STRICT;
            CREATE UNIQUE INDEX ux_fin_budgets_category_month ON fin_budgets (category_id, ifnull(month, ''));

            CREATE TABLE fin_goals (
                id              TEXT    NOT NULL PRIMARY KEY,
                name            TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
                target_minor    INTEGER NOT NULL CHECK (target_minor > 0),
                deadline        TEXT    CHECK (deadline IS NULL OR deadline GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                account_id      TEXT    REFERENCES fin_accounts (id) ON DELETE RESTRICT,
                color           TEXT    NOT NULL,
                archived_at_ms  INTEGER,
                created_at_ms   INTEGER NOT NULL,
                updated_at_ms   INTEGER NOT NULL
            ) STRICT;

            -- Real: dinheiro separado (com transferência, se a meta tem conta própria). Planejada: só intenção.
            CREATE TABLE fin_goal_contributions (
                id              TEXT    NOT NULL PRIMARY KEY,
                goal_id         TEXT    NOT NULL REFERENCES fin_goals (id) ON DELETE CASCADE,
                date            TEXT    NOT NULL CHECK (date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                amount_minor    INTEGER NOT NULL CHECK (amount_minor <> 0),
                kind            TEXT    NOT NULL CHECK (kind IN ('Real', 'Planned')),
                account_id      TEXT    REFERENCES fin_accounts (id) ON DELETE RESTRICT,
                transaction_id  TEXT    REFERENCES fin_transactions (id) ON DELETE RESTRICT,
                notes           TEXT,
                created_at_ms   INTEGER NOT NULL,
                CHECK (kind = 'Real' OR transaction_id IS NULL)
            ) STRICT;
            CREATE INDEX ix_fin_goal_contributions_goal ON fin_goal_contributions (goal_id);
            CREATE UNIQUE INDEX ux_fin_goal_contributions_transaction ON fin_goal_contributions (transaction_id) WHERE transaction_id IS NOT NULL;

            -- Cenários do simulador: hipóteses em JSON, nunca lançamentos.
            CREATE TABLE fin_scenarios (
                id             TEXT    NOT NULL PRIMARY KEY,
                name           TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
                items          TEXT    NOT NULL CHECK (json_valid(items)),
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            ) STRICT;
            """),
    ];
}
