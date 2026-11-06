using Microsoft.Win32;

namespace DesktopHub.App.Platform;

/// <summary>
/// Inicialização com o Windows pela chave por usuário <c>HKCU\...\Run</c> (não exige administrador).
/// O estado real é sempre lido do registro — a configuração salva é só a intenção do usuário.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiaryGhost";

    /// <summary>Entrada da época "Desktop Hub" (aponta para o executável antigo); removida ao aplicar.</summary>
    private const string LegacyValueName = "DesktopHub";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Havia uma entrada do nome antigo? (então o usuário tinha ligado a opção.)</summary>
    public static bool HasLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(LegacyValueName) is not null;
    }

    public static void Apply(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (existing is null)
        {
            return;
        }
        existing.DeleteValue(ValueName, throwOnMissingValue: false);
        existing.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }
}
