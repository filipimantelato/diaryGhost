using System.Globalization;
using DesktopHub.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Persistence;

public sealed record DatabaseStartupReport(
    MigrationResult Migration,
    string? QuarantinedFile,
    string? BackupFile)
{
    public bool RecoveredFromCorruption => QuarantinedFile is not null;
}

/// <summary>
/// Dono do arquivo SQLite: verifica integridade, isola arquivos corrompidos, faz backup antes de
/// migrar e entrega conexões configuradas. Conexões são baratas (pool do Microsoft.Data.Sqlite).
/// </summary>
public sealed partial class SqliteDatabase
{
    private const int MaxBackups = 5;
    private readonly AppPaths _paths;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly string _connectionString;

    public SqliteDatabase(AppPaths paths, ILogger<SqliteDatabase> logger)
    {
        _paths = paths;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string FilePath => _paths.DatabaseFile;

    public DatabaseStartupReport Initialize(IReadOnlyList<Migration>? migrations = null)
    {
        _paths.EnsureCreated();
        string? quarantined = null;

        if (File.Exists(FilePath) && !PassesIntegrityCheck(out var reason))
        {
            quarantined = Quarantine();
            LogQuarantined(reason, quarantined);
        }

        string? backup = null;
        using var connection = OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            // WAL é persistente no arquivo; leitores não bloqueiam o escritor.
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }

        var result = Migrator.Migrate(connection, migrations ?? Migrations.All, fromVersion =>
        {
            if (fromVersion > 0)
            {
                backup = Backup(connection, fromVersion);
            }
        });

        if (result.Applied)
        {
            LogMigrated(result.FromVersion, result.ToVersion);
        }
        return new DatabaseStartupReport(result, quarantined, backup);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private bool PassesIntegrityCheck(out string reason)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var first = cmd.ExecuteScalar() as string;
            reason = first ?? "sem resultado";
            return string.Equals(first, "ok", StringComparison.Ordinal);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* CORRUPT */ or 26 /* NOTADB */)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            // Libera o handle do arquivo para que ele possa ser movido.
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Move o banco (e arquivos WAL/SHM) para <c>corrupt/</c>; nunca apaga dados do usuário.</summary>
    private string Quarantine()
    {
        Directory.CreateDirectory(_paths.Corrupt);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(_paths.Corrupt, $"hub-{stamp}.db");
        File.Move(FilePath, target, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(FilePath + suffix))
            {
                File.Move(FilePath + suffix, target + suffix, overwrite: false);
            }
        }
        return target;
    }

    private string Backup(SqliteConnection connection, int fromVersion)
    {
        Directory.CreateDirectory(_paths.Backups);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(_paths.Backups, $"hub-v{fromVersion}-{stamp}.db");
        using (var cmd = connection.CreateCommand())
        {
            // VACUUM INTO gera uma cópia consistente mesmo com WAL.
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", target);
            cmd.ExecuteNonQuery();
        }

        foreach (var old in new DirectoryInfo(_paths.Backups).GetFiles("hub-v*.db")
                     .OrderByDescending(f => f.CreationTimeUtc).Skip(MaxBackups))
        {
            old.Delete();
        }
        LogBackup(target);
        return target;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Banco de dados falhou na verificação de integridade ({Reason}); movido para {QuarantinedFile} e recriado")]
    private partial void LogQuarantined(string reason, string quarantinedFile);

    [LoggerMessage(Level = LogLevel.Information, Message = "Esquema migrado da versão {From} para {To}")]
    private partial void LogMigrated(int from, int to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup pré-migração criado em {BackupFile}")]
    private partial void LogBackup(string backupFile);
}
