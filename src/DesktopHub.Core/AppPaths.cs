namespace DesktopHub.Core;

/// <summary>Locais de dados do aplicativo. Tudo fica sob uma raiz por usuário, nunca em Program Files.</summary>
public sealed class AppPaths
{
    public const string FolderName = "DiaryGhost";

    /// <summary>Nome da pasta nas versões 0.1–0.2, quando o app se chamava Desktop Hub.</summary>
    public const string LegacyFolderName = "DesktopHub";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Padrão: %LOCALAPPDATA%\DiaryGhost.</summary>
    public static AppPaths Default() => new(DefaultRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    public static string DefaultRoot(string localAppData) => Path.Combine(localAppData, FolderName);

    /// <summary>
    /// Move a pasta da época "Desktop Hub" para o nome novo, uma única vez. Deve ser chamado depois de
    /// garantir instância única. Se a pasta antiga estiver em uso (versão antiga ainda aberta), não move e
    /// devolve a antiga — os dados nunca são duplicados nem perdidos.
    /// </summary>
    public static MigrationOutcome MigrateLegacyFolder(string localAppData, out AppPaths paths)
    {
        var current = DefaultRoot(localAppData);
        var legacy = Path.Combine(localAppData, LegacyFolderName);
        paths = new AppPaths(current);
        if (Directory.Exists(current) || !Directory.Exists(legacy))
        {
            return MigrationOutcome.NotNeeded;
        }
        try
        {
            Directory.Move(legacy, current);
            return MigrationOutcome.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            paths = new AppPaths(legacy);
            return MigrationOutcome.KeptLegacyInUse;
        }
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "hub.db");

    /// <summary>Banco próprio do módulo de finanças (ADR-028).</summary>
    public string FinanceDatabaseFile => Path.Combine(Root, "finance.db");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string Corrupt => Path.Combine(Root, "corrupt");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
    }
}

public enum MigrationOutcome
{
    NotNeeded,
    Moved,
    KeptLegacyInUse,
}
