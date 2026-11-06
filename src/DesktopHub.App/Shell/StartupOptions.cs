using DesktopHub.Infrastructure.Persistence;

namespace DesktopHub.App.Shell;

/// <summary>
/// Argumentos de linha de comando:
/// <c>--minimized</c> (usado pela inicialização com o Windows), <c>--show-main</c>, <c>--no-widget</c>
/// (oculta o widget só nesta execução), <c>--quit</c> (encerra a instância em execução) e
/// <c>--data-dir &lt;pasta&gt;</c> (dados isolados, para testes e benchmarks).
/// </summary>
public sealed record StartupOptions(bool Minimized, bool ShowMain, bool NoWidget, bool Quit, string? DataDir)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool minimized = false, showMain = false, noWidget = false, quit = false;
        string? dataDir = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized": minimized = true; break;
                case "--show-main": showMain = true; break;
                case "--no-widget": noWidget = true; break;
                case "--quit": quit = true; break;
                case "--data-dir" when i + 1 < args.Count: dataDir = args[++i]; break;
            }
        }
        return new StartupOptions(minimized, showMain, noWidget, quit, dataDir);
    }

    /// <summary>Instâncias com pastas de dados diferentes podem coexistir (ex.: benchmark ao lado do uso diário).</summary>
    public string InstanceId => DataDir is null
        ? "DesktopHub"
        : "DesktopHub-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(DataDir).ToUpperInvariant())))[..16];
}

/// <summary>Estado conhecido só após a inicialização (resultado do banco), compartilhado com a UI.</summary>
public sealed class StartupState
{
    public required StartupOptions Options { get; init; }
    public DatabaseStartupReport? Database { get; set; }
}
