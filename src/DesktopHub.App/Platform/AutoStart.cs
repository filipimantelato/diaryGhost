using Microsoft.Win32;

namespace DesktopHub.App.Platform;

/// <summary>
/// Inicialização com o Windows pela chave por usuário <c>HKCU\...\Run</c> (não exige administrador).
/// O estado real é sempre lido do registro — a configuração salva é só a intenção do usuário.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopHub";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (existing?.GetValue(ValueName) is not null)
        {
            existing.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
