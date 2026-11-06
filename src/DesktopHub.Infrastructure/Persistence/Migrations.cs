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
    ];
}
