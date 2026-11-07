using System.Diagnostics;
using Microsoft.Win32;

namespace DesktopHub.Infrastructure.Setup;

/// <summary>Onde o app é instalado e registrado. Os testes usam pastas e chaves temporárias.</summary>
/// <param name="RegistryBase">Normalmente "Software"; nos testes, uma subchave descartável.</param>
public sealed record InstallLayout(string TargetDir, string ShortcutPath, string RegistryBase)
{
    public const string ExeName = "DiaryGhost.exe";
    public const string UninstallKeyName = "DiaryGhost";
    public const string Protocol = "diaryghost";

    public static InstallLayout Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DiaryGhost"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Diary Ghost.lnk"),
        "Software");

    public string TargetExe => Path.Combine(TargetDir, ExeName);
    public string UninstallKey => $@"{RegistryBase}\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";
    public string ProtocolKey => $@"{RegistryBase}\Classes\{Protocol}";
}

/// <summary>
/// Instalação por usuário, sem administrador e sem ferramenta de terceiros: copia o app para
/// <c>%LOCALAPPDATA%\Programs\DiaryGhost</c>, cria o atalho no Menu Iniciar, registra a entrada em
/// "Aplicativos instalados" (<c>HKCU\...\Uninstall</c>) e o protocolo <c>diaryghost:</c>. Rodar de novo
/// atualiza no lugar (arquivos antigos que não existem mais na versão nova são removidos).
/// </summary>
public static class PerUserInstaller
{
    public static bool IsRunningFrom(InstallLayout layout, string currentDir) =>
        string.Equals(Path.GetFullPath(currentDir).TrimEnd('\\'), Path.GetFullPath(layout.TargetDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copia os arquivos e registra. Devolve quantos arquivos foram copiados.</summary>
    public static int Install(string sourceDir, InstallLayout layout, string version, bool createShortcut = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var source = Path.GetFullPath(sourceDir);
        if (!File.Exists(Path.Combine(source, InstallLayout.ExeName)))
        {
            throw new FileNotFoundException("Pasta de origem sem o executável do Diary Ghost.", Path.Combine(source, InstallLayout.ExeName));
        }
        if (IsRunningFrom(layout, source))
        {
            throw new InvalidOperationException("O app já está rodando da pasta de instalação.");
        }
        Directory.CreateDirectory(layout.TargetDir);
        var copied = 0;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(layout.TargetDir, relative);
            expected.Add(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            copied++;
        }
        // Atualização: remove o que sobrou da versão anterior (só dentro da nossa pasta).
        foreach (var stale in Directory.EnumerateFiles(layout.TargetDir, "*", SearchOption.AllDirectories).Where(f => !expected.Contains(f)).ToList())
        {
            TryDelete(stale);
        }

        RegisterUninstall(layout, version, copied, SizeKb(layout.TargetDir));
        RegisterProtocol(layout.ProtocolKey, layout.TargetExe);
        if (createShortcut)
        {
            CreateShortcut(layout.ShortcutPath, layout.TargetExe);
        }
        return copied;
    }

    /// <summary>Remove registros e atalho. A pasta do programa é apagada por <see cref="ScheduleFolderRemoval"/> (o exe pode estar em uso).</summary>
    public static void Unregister(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Registry.CurrentUser.DeleteSubKeyTree(layout.UninstallKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(layout.ProtocolKey, throwOnMissingSubKey: false);
        TryDelete(layout.ShortcutPath);
    }

    /// <summary>Apaga a pasta do programa depois que este processo terminar (um exe não apaga a si mesmo).</summary>
    public static void ScheduleFolderRemoval(string dir)
    {
        var full = Path.GetFullPath(dir);
        // Segurança: só apaga a pasta de instalação conhecida, nunca algo genérico.
        if (!full.EndsWith(@"\Programs\DiaryGhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pasta inesperada para remoção: " + full);
        }
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Process.Start(new ProcessStartInfo(cmd, $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{full}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
    }

    /// <summary>
    /// Protocolo <c>diaryghost:</c> (só abre páginas do app — ver StartupOptions). Usado para reabrir o app ao
    /// clicar numa notificação antiga com o app fechado. Registrado também ao iniciar, apontando para o exe atual.
    /// </summary>
    public static void RegisterProtocol(string protocolKey, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(protocolKey, writable: true);
        key.SetValue("", "URL:Diary Ghost");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue("", $"\"{exePath}\",0");
        }
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exePath}\" \"%1\"");
    }

    private static void RegisterUninstall(InstallLayout layout, string version, int files, long sizeKb)
    {
        using var key = Registry.CurrentUser.CreateSubKey(layout.UninstallKey, writable: true);
        key.SetValue("DisplayName", "Diary Ghost");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Diary Ghost");
        key.SetValue("DisplayIcon", $"\"{layout.TargetExe}\",0");
        key.SetValue("InstallLocation", layout.TargetDir);
        key.SetValue("UninstallString", $"\"{layout.TargetExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{layout.TargetExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        _ = files;
    }

    public static string? InstalledVersion(InstallLayout layout)
    {
        using var key = Registry.CurrentUser.OpenSubKey(layout.UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    private static long SizeKb(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;

    /// <summary>Atalho .lnk pelo objeto WScript.Shell (COM do próprio Windows).</summary>
    private static void CreateShortcut(string shortcutPath, string exePath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = Path.GetDirectoryName(exePath);
            link.IconLocation = exePath + ",0";
            link.Description = "Diary Ghost — hub pessoal de mesa";
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Arquivo em uso: fica para a próxima atualização.
        }
    }
}
