using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DesktopHub.Infrastructure.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public void Fresh_database_is_migrated_to_latest_without_backup()
    {
        using var dir = new TempDataDir();
        var report = dir.Database.Initialize();

        Assert.Equal(0, report.Migration.FromVersion);
        Assert.Equal(Migrations.All[^1].Version, report.Migration.ToVersion);
        Assert.Null(report.BackupFile);
        Assert.False(report.RecoveredFromCorruption);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var second = dir.Reopen().Initialize();

        Assert.False(second.Migration.Applied);
    }

    [Fact]
    public void Pending_migration_on_existing_database_creates_backup_first()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();

        var withNew = Migrations.All.Append(new Migration(Migrations.All[^1].Version + 1, "teste", "CREATE TABLE extra (id TEXT PRIMARY KEY) STRICT;")).ToList();
        var report = dir.Reopen().Initialize(withNew);

        Assert.True(report.Migration.Applied);
        Assert.NotNull(report.BackupFile);
        Assert.True(File.Exists(report.BackupFile));

        // O backup está na versão anterior.
        using var backup = new SqliteConnection($"Data Source={report.BackupFile};Pooling=False");
        backup.Open();
        Assert.Equal(Migrations.All[^1].Version, Migrator.GetVersion(backup));
    }

    [Fact]
    public void Failing_migration_rolls_back_and_keeps_previous_version()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var latest = Migrations.All[^1].Version;
        var broken = Migrations.All.Append(new Migration(latest + 1, "quebrada", "CREATE TABLE ok_table (id TEXT) STRICT; SELECT * FROM tabela_inexistente;")).ToList();

        Assert.Throws<SqliteException>(() => dir.Reopen().Initialize(broken));

        using var connection = dir.Reopen().OpenConnection();
        Assert.Equal(latest, Migrator.GetVersion(connection));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'ok_table';";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Database_newer_than_app_is_rejected()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize([.. Migrations.All, new Migration(999, "futuro", "SELECT 1;")]);

        Assert.Throws<DatabaseNewerThanAppException>(() => dir.Reopen().Initialize());
    }

    [Fact]
    public void Corrupted_file_is_quarantined_and_a_new_database_is_created()
    {
        using var dir = new TempDataDir();
        dir.Paths.EnsureCreated();
        File.WriteAllText(dir.Paths.DatabaseFile, "isto não é um banco SQLite — conteúdo corrompido de propósito");

        var report = dir.Database.Initialize();

        Assert.True(report.RecoveredFromCorruption);
        Assert.True(File.Exists(report.QuarantinedFile));
        Assert.Equal(Migrations.All[^1].Version, report.Migration.ToVersion);
    }
}
