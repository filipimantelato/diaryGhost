namespace DesktopHub.Infrastructure.Persistence;

/// <summary>Uma migração de esquema. Versões são inteiros crescentes e nunca reutilizados.</summary>
public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Catálogo de migrações. Regras:
/// 1. Nunca edite uma migração já publicada — crie uma nova.
/// 2. Cada fase acrescenta as suas tabelas aqui.
/// 3. Datas em milissegundos Unix UTC (<c>*_at_ms</c>); IDs em TEXT (GUID v7).
/// </summary>
public static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "fundacao", """
            CREATE TABLE settings (
                key            TEXT    NOT NULL PRIMARY KEY,
                value_json     TEXT    NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE secrets (
                name            TEXT    NOT NULL PRIMARY KEY,
                protected_blob  BLOB    NOT NULL,
                created_at_ms   INTEGER NOT NULL,
                updated_at_ms   INTEGER NOT NULL
            ) STRICT;
            """),

        new(2, "produtividade_e_notificacoes", """
            -- Estado ativo (cronômetro, temporizador, Pomodoro) num documento JSON: uma linha, regravada a cada mudança.
            CREATE TABLE productivity_state (
                id             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                value_json     TEXT    NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE focus_sessions (
                id             TEXT    NOT NULL PRIMARY KEY,
                kind           TEXT    NOT NULL CHECK (kind IN ('PomodoroFocus', 'PomodoroBreak', 'Timer', 'Stopwatch')),
                label          TEXT,
                started_at_ms  INTEGER NOT NULL,
                ended_at_ms    INTEGER NOT NULL CHECK (ended_at_ms >= started_at_ms),
                planned_ms     INTEGER CHECK (planned_ms IS NULL OR planned_ms > 0),
                actual_ms      INTEGER NOT NULL CHECK (actual_ms >= 0),
                completed      INTEGER NOT NULL CHECK (completed IN (0, 1))
            ) STRICT;
            CREATE INDEX ix_focus_sessions_started ON focus_sessions (started_at_ms);

            CREATE TABLE notifications (
                id             TEXT    NOT NULL PRIMARY KEY,
                category       TEXT    NOT NULL,
                priority       INTEGER NOT NULL,
                title          TEXT    NOT NULL,
                body           TEXT,
                dedup_key      TEXT,
                created_at_ms  INTEGER NOT NULL,
                outcome        TEXT    NOT NULL
            ) STRICT;
            CREATE INDEX ix_notifications_created ON notifications (created_at_ms);
            CREATE INDEX ix_notifications_dedup ON notifications (dedup_key, created_at_ms) WHERE dedup_key IS NOT NULL;
            CREATE INDEX ix_notifications_category ON notifications (category, created_at_ms);
            """),

        new(3, "agenda_e_lembretes", """
            -- Horários de parede (sem fuso) em texto ISO 'yyyy-MM-ddTHH:mm:ss'; o fuso fica em time_zone.
            CREATE TABLE agenda_categories (
                id     TEXT NOT NULL PRIMARY KEY,
                name   TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 40),
                color  TEXT NOT NULL CHECK (color GLOB '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]')
            ) STRICT;
            INSERT INTO agenda_categories (id, name, color) VALUES
                ('0197a000-0000-7000-8000-000000000001', 'Pessoal',  '#C9A94E'),
                ('0197a000-0000-7000-8000-000000000002', 'Trabalho', '#6B9AC4'),
                ('0197a000-0000-7000-8000-000000000003', 'Estudos',  '#8FB573'),
                ('0197a000-0000-7000-8000-000000000004', 'Saúde',    '#C47A6B');

            CREATE TABLE calendar_events (
                id             TEXT    NOT NULL PRIMARY KEY,
                title          TEXT    NOT NULL,
                start_local    TEXT    NOT NULL,
                duration_ms    INTEGER NOT NULL CHECK (duration_ms >= 0),
                all_day        INTEGER NOT NULL CHECK (all_day IN (0, 1)),
                time_zone      TEXT    NOT NULL,
                rrule          TEXT,
                exdates        TEXT,
                category_id    TEXT REFERENCES agenda_categories (id) ON DELETE SET NULL,
                location       TEXT,
                notes          TEXT,
                reminders      TEXT,
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE tasks (
                id                TEXT    NOT NULL PRIMARY KEY,
                title             TEXT    NOT NULL,
                notes             TEXT,
                priority          TEXT    NOT NULL CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent')),
                category_id       TEXT REFERENCES agenda_categories (id) ON DELETE SET NULL,
                due_local         TEXT,
                due_has_time      INTEGER NOT NULL CHECK (due_has_time IN (0, 1)),
                time_zone         TEXT    NOT NULL,
                rrule             TEXT,
                recurrence_start  TEXT,
                reminder_minutes  INTEGER CHECK (reminder_minutes IS NULL OR reminder_minutes >= 0),
                completed_at_ms   INTEGER,
                completed_count   INTEGER NOT NULL DEFAULT 0,
                created_at_ms     INTEGER NOT NULL,
                updated_at_ms     INTEGER NOT NULL
            ) STRICT;

            -- Avisos já tratados (entregues, adiados, dispensados), pela chave estável origem+ocorrência+antecedência.
            CREATE TABLE reminder_log (
                key               TEXT    NOT NULL PRIMARY KEY,
                source            TEXT    NOT NULL CHECK (source IN ('Event', 'Task')),
                source_id         TEXT    NOT NULL,
                title             TEXT    NOT NULL,
                anchor_ms         INTEGER NOT NULL,
                fire_at_ms        INTEGER NOT NULL,
                state             TEXT    NOT NULL CHECK (state IN ('Fired', 'Snoozed', 'Dismissed', 'Completed', 'Skipped')),
                snoozed_until_ms  INTEGER,
                outcome           TEXT,
                late              INTEGER NOT NULL CHECK (late IN (0, 1)),
                updated_at_ms     INTEGER NOT NULL
            ) STRICT;
            CREATE INDEX ix_reminder_log_snoozed ON reminder_log (snoozed_until_ms) WHERE state = 'Snoozed';
            CREATE INDEX ix_reminder_log_updated ON reminder_log (updated_at_ms);

            CREATE TABLE agenda_meta (
                key       TEXT    NOT NULL PRIMARY KEY,
                value_ms  INTEGER NOT NULL
            ) STRICT;
            """),

        new(4, "noticias_e_clima", """
            CREATE TABLE news_sources (
                id                    TEXT    NOT NULL PRIMARY KEY,
                name                  TEXT    NOT NULL,
                feed_url              TEXT    NOT NULL,
                category              TEXT    NOT NULL,
                built_in              INTEGER NOT NULL CHECK (built_in IN (0, 1)),
                enabled               INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                etag                  TEXT,
                last_modified         TEXT,
                last_attempt_at_ms    INTEGER,
                last_success_at_ms    INTEGER,
                last_error            TEXT,
                consecutive_failures  INTEGER NOT NULL DEFAULT 0,
                next_fetch_at_ms      INTEGER
            ) STRICT;

            CREATE TABLE news_articles (
                id               TEXT    NOT NULL PRIMARY KEY,
                source_id        TEXT    NOT NULL REFERENCES news_sources (id) ON DELETE CASCADE,
                item_key         TEXT    NOT NULL,
                title            TEXT    NOT NULL,
                url              TEXT    NOT NULL,
                summary          TEXT,
                author           TEXT,
                published_at_ms  INTEGER,
                fetched_at_ms    INTEGER NOT NULL,
                canonical_url    TEXT    NOT NULL,
                title_key        TEXT    NOT NULL,
                cluster_id       TEXT    NOT NULL,
                is_read          INTEGER NOT NULL DEFAULT 0,
                is_saved         INTEGER NOT NULL DEFAULT 0,
                is_favorite      INTEGER NOT NULL DEFAULT 0,
                UNIQUE (source_id, item_key)
            ) STRICT;
            CREATE INDEX ix_news_articles_fetched ON news_articles (fetched_at_ms);
            CREATE INDEX ix_news_articles_canonical ON news_articles (canonical_url);

            -- Cache genérico (ex.: última previsão do tempo, para mostrar offline).
            CREATE TABLE cache (
                key            TEXT    NOT NULL PRIMARY KEY,
                value_json     TEXT    NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            ) STRICT;
            """),
    ];
}
