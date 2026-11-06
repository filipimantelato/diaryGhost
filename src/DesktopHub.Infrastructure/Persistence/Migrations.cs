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
    ];
}
