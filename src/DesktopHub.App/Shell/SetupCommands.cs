using System.Diagnostics;
using System.Reflection;
using System.Windows;
using DesktopHub.App.Platform;
using DesktopHub.Core;
using DesktopHub.Infrastructure.Activity;
using DesktopHub.Infrastructure.Setup;
using Microsoft.Win32;

namespace DesktopHub.App.Shell;

/// <summary><c>--install</c> e <c>--uninstall</c>: rodam antes da interface, com confirmações explícitas.</summary>
internal static class SetupCommands
{
    private const string Title = "Diary Ghost";

    private static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public static int Install()
    {
        var layout = InstallLayout.Default;
        var source = AppContext.BaseDirectory;
        if (PerUserInstaller.IsRunningFrom(layout, source))
        {
            MessageBox.Show("O Diary Ghost já está instalado e rodando desta pasta.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var installed = PerUserInstaller.InstalledVersion(layout);
        var question = installed is null
            ? $"Instalar o Diary Ghost {Version} para este usuário?\n\nPasta: {layout.TargetDir}\nAtalho no Menu Iniciar e entrada em \"Aplicativos instalados\". Não precisa de administrador."
            : $"Atualizar o Diary Ghost de {installed} para {Version}?\n\nSeus dados (agenda, tarefas, configurações) são mantidos.";
        if (MessageBox.Show(question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return 1;
        }
        if (!StopRunningInstance())
        {
            MessageBox.Show("Não foi possível fechar o Diary Ghost em execução. Feche-o pela bandeja e tente de novo.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        try
        {
            PerUserInstaller.Install(source, layout, Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"A instalação falhou: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        Process.Start(new ProcessStartInfo(layout.TargetExe, "--show-main") { UseShellExecute = false })?.Dispose();
        return 0;
    }

    public static int Uninstall()
    {
        var layout = InstallLayout.Default;
        var hooks = new ClaudeHooksInstaller(ClaudeHooksInstaller.DefaultPath);
        if (MessageBox.Show(
                "Desinstalar o Diary Ghost?\n\nSerão removidos: o programa, o atalho do Menu Iniciar, a inicialização com o Windows, " +
                "o registro de notificações e, se instalados por ele, os hooks do Claude Code (com cópia de segurança do settings.json).",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return 1;
        }
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.FolderName);
        var deleteData = Directory.Exists(data) && MessageBox.Show(
            $"Apagar também os seus dados?\n\n{data}\n\nAgenda, tarefas, lembretes, notícias salvas, configurações e endereços de agendas assinadas. " +
            "Isso não pode ser desfeito. Escolha \"Não\" para manter (uma reinstalação os reaproveita).",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        StopRunningInstance();
        AutoStart.Apply(false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Infrastructure.Notifications.WindowsToastSink.AppUserModelId}", throwOnMissingSubKey: false);
        try
        {
            if (hooks.IsInstalled())
            {
                hooks.Uninstall();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            // O settings.json do Claude Code não pôde ser lido: fica como está (os hooks só falham em silêncio).
        }
        PerUserInstaller.Unregister(layout);
        if (deleteData)
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Alguns dados não puderam ser apagados: {ex.Message}\n{data}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (Directory.Exists(layout.TargetDir))
        {
            PerUserInstaller.ScheduleFolderRemoval(layout.TargetDir);
        }
        MessageBox.Show(deleteData ? "Diary Ghost desinstalado e dados apagados." : $"Diary Ghost desinstalado. Seus dados continuam em:\n{data}",
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    /// <summary>Pede à instância em execução que encerre (como <c>--quit</c>) e espera até 10 s.</summary>
    private static bool StopRunningInstance()
    {
        var id = StartupOptions.Parse([]).InstanceId;
        if (!SingleInstance.IsRunning(id))
        {
            return true;
        }
        SingleInstance.TryAcquire(id, requestQuit: true)?.Dispose(); // sinaliza o --quit
        for (var i = 0; i < 100 && SingleInstance.IsRunning(id); i++)
        {
            Thread.Sleep(100);
        }
        return !SingleInstance.IsRunning(id);
    }
}
