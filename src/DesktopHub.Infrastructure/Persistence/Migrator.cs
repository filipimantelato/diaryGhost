using Microsoft.Data.Sqlite;

namespace DesktopHub.Infrastructure.Persistence;

public sealed class DatabaseNewerThanAppException(int databaseVersion, int appVersion)
    : Exception($"O banco de dados está na versão de esquema {databaseVersion}, mas este aplicativo conhece até {appVersion}. Atualize o aplicativo.")
{
    public int DatabaseVersion { get; } = databaseVersion;
    public int AppVersion { get; } = appVersion;
}

public sealed record MigrationResult(int FromVersion, int ToVersion)
{
    public bool Applied => ToVersion != FromVersion;
}

/// <summary>Aplica migrações em ordem, cada uma em sua própria transação; a versão fica em <c>PRAGMA user_version</c>.</summary>
public static class Migrator
{
    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <param name="beforeFirstPending">Chamado uma vez, antes da primeira migração pendente (usado para backup).</param>
    public static MigrationResult Migrate(SqliteConnection connection, IReadOnlyList<Migration> migrations, Action<int>? beforeFirstPending = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);
        Validate(migrations);

        var current = GetVersion(connection);
        var latest = migrations.Count == 0 ? 0 : migrations[^1].Version;
        if (current > latest)
        {
            throw new DatabaseNewerThanAppException(current, latest);
        }

        var pending = migrations.Where(m => m.Version > current).ToList();
        if (pending.Count == 0)
        {
            return new MigrationResult(current, current);
        }

        beforeFirstPending?.Invoke(current);

        foreach (var migration in pending)
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                // PRAGMA não aceita parâmetros; o valor é um int validado.
                cmd.CommandText = $"PRAGMA user_version = {migration.Version};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        return new MigrationResult(current, pending[^1].Version);
    }

    private static void Validate(IReadOnlyList<Migration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version <= 0 || (i > 0 && migrations[i].Version <= migrations[i - 1].Version))
            {
                throw new InvalidOperationException("Migrações devem ter versões positivas, únicas e em ordem crescente.");
            }
        }
    }
}
