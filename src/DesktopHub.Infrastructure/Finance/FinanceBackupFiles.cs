using System.Globalization;
using DesktopHub.Core;
using DesktopHub.Core.Finance;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Finance;

/// <summary>
/// Backup protegido do banco de finanças (Fase 13). Exportar: cópia consistente do banco (VACUUM INTO) cifrada com a
/// senha (<see cref="ProtectedBackup"/>). Restaurar: o arquivo é decifrado e verificado (integridade, tabelas, versão) e
/// fica pendente; na próxima abertura do app o banco atual vai para a pasta de backups e o restaurado entra no lugar —
/// nada é trocado com o banco aberto.
/// </summary>
public sealed partial class FinanceBackupFiles(AppPaths paths, FinanceDatabase database, ILogger<FinanceBackupFiles> logger)
{
    public string PendingRestoreFile => Path.Combine(paths.Root, "finance-restore.pending");

    public bool HasPendingRestore => File.Exists(PendingRestoreFile);

    /// <summary>Bytes de uma cópia consistente do banco atual (sem os arquivos -wal/-shm).</summary>
    public byte[] Snapshot()
    {
        var temp = Path.Combine(paths.Root, $"finance-export-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var connection = database.OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $path;";
                cmd.Parameters.AddWithValue("$path", temp);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(temp);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Exporta o backup cifrado. A senha não é guardada em lugar nenhum.</summary>
    public void Export(string target, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var encrypted = ProtectedBackup.Encrypt(Snapshot(), password);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, target, overwrite: true);
        LogExported(encrypted.Length);
    }

    /// <summary>
    /// Decifra e confere o backup; se estiver bom, deixa-o pendente para a próxima abertura. Devolve quantos lançamentos e
    /// contas ele tem (para o usuário conferir antes de reiniciar).
    /// </summary>
    public (int Accounts, int Transactions) StageRestore(string source, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (new FileInfo(source).Length > 512L * 1024 * 1024)
        {
            throw new FinanceValidationException("Arquivo grande demais para um backup de finanças.");
        }
        var plain = ProtectedBackup.Decrypt(File.ReadAllBytes(source), password);
        var temp = PendingRestoreFile + ".check";
        File.WriteAllBytes(temp, plain);
        try
        {
            int accounts, transactions;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                if (cmd.ExecuteScalar() as string != "ok")
                {
                    throw new FinanceValidationException("O backup está danificado (falha na verificação de integridade).");
                }
                var version = Migrator.GetVersion(connection);
                if (version < 1 || version > FinanceMigrations.All[^1].Version)
                {
                    throw new FinanceValidationException(version < 1
                        ? "O arquivo não contém um banco de finanças."
                        : "O backup é de uma versão mais nova do Diary Ghost. Atualize o app antes de restaurar.");
                }
                cmd.CommandText = "SELECT (SELECT count(*) FROM fin_accounts), (SELECT count(*) FROM fin_transactions);";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                accounts = reader.GetInt32(0);
                transactions = reader.GetInt32(1);
            }
            File.Move(temp, PendingRestoreFile, overwrite: true);
            LogStaged(accounts, transactions);
            return (accounts, transactions);
        }
        catch (SqliteException)
        {
            throw new FinanceValidationException("O arquivo não contém um banco de finanças válido.");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public void CancelPendingRestore() => TryDelete(PendingRestoreFile);

    /// <summary>
    /// Na abertura, antes de inicializar o banco: o banco atual é guardado em backups/ e o restaurado entra no lugar.
    /// Devolve o caminho da cópia de segurança, ou nulo se não havia nada pendente.
    /// </summary>
    public string? ApplyPendingRestore()
    {
        if (!HasPendingRestore)
        {
            return null;
        }
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(paths.Backups);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safety = Path.Combine(paths.Backups, $"finance-before-restore-{stamp}.db");
        var current = paths.FinanceDatabaseFile;
        if (File.Exists(current))
        {
            File.Copy(current, safety, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                TryDelete(current + suffix);
            }
        }
        File.Move(PendingRestoreFile, current, overwrite: true);
        LogApplied();
        return File.Exists(safety) ? safety : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup protegido de finanças exportado ({Bytes} bytes)")]
    private partial void LogExported(int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restauração de finanças pendente: {Accounts} contas, {Transactions} lançamentos")]
    private partial void LogStaged(int accounts, int transactions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restauração de finanças aplicada na abertura")]
    private partial void LogApplied();
}
