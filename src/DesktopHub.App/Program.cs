using DesktopHub.Core.Activity;
using DesktopHub.Infrastructure.Activity;

namespace DesktopHub.App;

/// <summary>
/// Ponto de entrada. Com <c>--hook claude-code</c> o executável não abre interface: lê o JSON do hook do
/// Claude Code na entrada padrão, reduz ao contrato (<see cref="ActivityContract"/>) e repassa ao app pelo
/// canal local. Sempre sai com código 0 e em menos de ~1 s — um hook nunca pode travar o Claude Code.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--install":
                return Shell.SetupCommands.Install();
            case "--uninstall":
                return Shell.SetupCommands.Uninstall();
        }
        if (args.Length >= 2 && args[0] == "--hook")
        {
            return RunHook(args);
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunHook(string[] args)
    {
        try
        {
            string? dataDir = null;
            for (var i = 2; i < args.Length - 1; i++)
            {
                if (args[i] == "--data-dir")
                {
                    dataDir = args[i + 1];
                }
            }
            var line = args[1] switch
            {
                "claude-code" => ActivityContract.FromClaudeHook(ReadStdin()),
                "test" => ActivityContract.TestMessage(),
                _ => null,
            };
            if (line is not null)
            {
                ActivityPipe.Send(ActivityPipe.Name(dataDir), line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Silencioso de propósito: o Claude Code continua normalmente.
        }
        return 0;
    }

    /// <summary>Lê até 64 KB da entrada padrão (só se ela foi redirecionada), com limite de 1 s.</summary>
    private static string? ReadStdin()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }
        var read = Task.Run(() =>
        {
            var buffer = new char[ActivityContract.MaxMessageBytes];
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            var total = 0;
            int n;
            while (total < buffer.Length && (n = reader.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += n;
            }
            return new string(buffer, 0, total);
        });
        return read.Wait(TimeSpan.FromSeconds(1)) ? read.Result : null;
    }
}
