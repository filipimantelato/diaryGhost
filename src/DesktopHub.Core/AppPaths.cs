namespace DesktopHub.Core;

/// <summary>Locais de dados do aplicativo. Tudo fica sob uma raiz por usuário, nunca em Program Files.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Padrão: %LOCALAPPDATA%\DesktopHub.</summary>
    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopHub"));

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "hub.db");
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
