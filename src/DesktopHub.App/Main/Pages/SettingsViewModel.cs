using System.Diagnostics;
using DesktopHub.App.Platform;
using DesktopHub.App.Theme;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

public sealed record AccentOption(string Name, string Hex, bool IsSelected);

public sealed record HotkeyRow(string Gesture, string Label, string Status, bool Ok);

/// <summary>
/// Cada setter aplica a mudança de verdade (registro do Windows, janela do widget, tema) e a persiste.
/// Mudanças vindas de fora (bandeja, atalho, widget) atualizam a tela via <see cref="SettingsChanged"/>.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly WidgetController _widget;
    private readonly AppPaths _paths;
    private readonly IDisposable _subscription;
    private string? _statusMessage;

    internal SettingsViewModel(SettingsService settings, IEventBus bus, WidgetController widget, GlobalHotkeys hotkeys, AppPaths paths)
    {
        _settings = settings;
        _widget = widget;
        _paths = paths;
        Hotkeys = [.. hotkeys.Bindings.Select(b => new HotkeyRow(b.Gesture, b.Label, b.Registered ? "Ativo" : "Indisponível — em uso por outro programa", b.Registered))];
        _subscription = bus.Subscribe<SettingsChanged>(_ => OnPropertyChanged(string.Empty), SynchronizationContext.Current);
    }

    private GeneralSettings General => _settings.General;
    private WidgetSettings Widget => _settings.Widget;

    // ---------- Geral ----------

    /// <summary>Reflete o registro do Windows, não apenas o que foi salvo.</summary>
    public bool StartWithWindows
    {
        get => AutoStart.IsEnabled();
        set
        {
            try
            {
                AutoStart.Apply(value);
                _settings.UpdateGeneral(g => g with { StartWithWindows = value });
                StatusMessage = null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                StatusMessage = $"Não foi possível alterar a inicialização com o Windows: {ex.Message}";
            }
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => General.StartMinimized;
        set => _settings.UpdateGeneral(g => g with { StartMinimized = value });
    }

    public bool CloseToTray
    {
        get => General.CloseBehavior == CloseBehavior.MinimizeToTray;
        set => _settings.UpdateGeneral(g => g with { CloseBehavior = value ? CloseBehavior.MinimizeToTray : CloseBehavior.Exit });
    }

    public IReadOnlyList<AccentOption> Accents =>
        [.. ThemeManager.AccentPresets.Select(a => new AccentOption(a.Name, a.Hex, string.Equals(a.Hex, General.AccentColor, StringComparison.OrdinalIgnoreCase)))];

    public void SetAccent(string hex)
    {
        _settings.UpdateGeneral(g => g with { AccentColor = hex });
        ThemeManager.ApplyAccent(_settings.General.AccentColor);
    }

    // ---------- Widget ----------

    public bool WidgetVisible
    {
        get => Widget.Visible;
        set => _settings.UpdateWidget(w => w with { Visible = value });
    }

    public bool AlwaysOnTop
    {
        get => Widget.AlwaysOnTop;
        set => _settings.UpdateWidget(w => w with { AlwaysOnTop = value });
    }

    public bool Locked
    {
        get => Widget.Locked;
        set => _settings.UpdateWidget(w => w with { Locked = value });
    }

    public bool AnimationsEnabled
    {
        get => Widget.AnimationsEnabled;
        set => _settings.UpdateWidget(w => w with { AnimationsEnabled = value });
    }

    public bool AnimatedPlaybackIndicator
    {
        get => Widget.AnimatedPlaybackIndicator;
        set => _settings.UpdateWidget(w => w with { AnimatedPlaybackIndicator = value });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Precisa ser de instância para o binding do WPF.")]
    public bool SystemAnimationsDisabled => !System.Windows.SystemParameters.ClientAreaAnimation;

    public bool HideInFullscreen
    {
        get => Widget.Fullscreen == FullscreenBehavior.Hide;
        set => _settings.UpdateWidget(w => w with { Fullscreen = value ? FullscreenBehavior.Hide : FullscreenBehavior.KeepVisible });
    }

    public bool ExcludeFromCapture
    {
        get => Widget.ExcludeFromCapture;
        set => _settings.UpdateWidget(w => w with { ExcludeFromCapture = value });
    }

    public bool CaptureExclusionUnsupported => Widget.ExcludeFromCapture && !_widget.CaptureExclusionSupported;

    public double OpacityPercent
    {
        get => Math.Round(Widget.Opacity * 100);
        set => _settings.UpdateWidget(w => w with { Opacity = value / 100.0 });
    }

    public void ResetWidgetPosition() => _widget.ResetPosition();

    // ---------- Atalhos e dados ----------

    public IReadOnlyList<HotkeyRow> Hotkeys { get; }

    public string DataFolder => _paths.Root;

    public void OpenDataFolder() => OpenFolder(_paths.Root);

    public void OpenLogsFolder() => OpenFolder(_paths.Logs);

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        // Caminho local controlado pelo app; aberto pelo Explorer, nunca interpretado como comando.
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    public void Dispose() => _subscription.Dispose();
}
