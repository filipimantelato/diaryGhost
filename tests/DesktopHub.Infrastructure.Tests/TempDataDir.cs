using DesktopHub.Core;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Infrastructure.Tests;

/// <summary>Pasta de dados isolada por teste, removida ao final.</summary>
internal sealed class TempDataDir : IDisposable
{
    public TempDataDir()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "DesktopHubTests", Guid.NewGuid().ToString("N")));
        Database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
    }

    public AppPaths Paths { get; }
    public SqliteDatabase Database { get; private set; }

    /// <summary>Simula reiniciar o aplicativo: nova instância sobre o mesmo arquivo.</summary>
    public SqliteDatabase Reopen()
    {
        SqliteConnection.ClearAllPools();
        Database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
        return Database;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária; o SO limpa depois.
        }
    }
}
