using System.Diagnostics;
using DesktopHub.App.Agenda;
using DesktopHub.App.Platform;
using DesktopHub.App.Theme;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

public sealed record AccentOption(string Name, string Hex, bool IsSelected);

public sealed record HotkeyRow(string Gesture, string Label, string Status, bool Ok);

public sealed record NotificationRow(string When, string Title, string Outcome);

public sealed record CategoryRow(Guid Id, string Name, System.Windows.Media.Brush Brush, string DeleteLabel);

/// <summary>
/// Cada setter aplica a mudança de verdade (registro do Windows, janela do widget, tema) e a persiste.
/// Mudanças vindas de fora (bandeja, atalho, widget) atualizam a tela via <see cref="SettingsChanged"/>.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly WidgetController _widget;
    private readonly AppPaths _paths;
    private readonly NotificationCenter _notifications;
    private readonly List<IDisposable> _subscriptions = [];
    private string? _statusMessage;

    internal SettingsViewModel(SettingsService settings, IEventBus bus, WidgetController widget, GlobalHotkeys hotkeys, AppPaths paths,
        NotificationCenter notifications, AgendaService agenda)
    {
        _settings = settings;
        _widget = widget;
        _paths = paths;
        _notifications = notifications;
        _agenda = agenda;
        Hotkeys = [.. hotkeys.Bindings.Select(b => new HotkeyRow(b.Gesture, b.Label, b.Registered ? "Ativo" : "Indisponível — em uso por outro programa", b.Registered))];
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<SettingsChanged>(_ => OnPropertyChanged(string.Empty), ui));
        _subscriptions.Add(bus.Subscribe<NotificationProcessed>(_ => OnPropertyChanged(nameof(RecentNotifications)), ui));
        _subscriptions.Add(bus.Subscribe<AgendaChanged>(_ => OnPropertyChanged(nameof(Categories)), ui));
    }

    // ---------- Agenda ----------

    private readonly AgendaService _agenda;
    private string? _categoryError;

    public IReadOnlyList<CategoryRow> Categories =>
        [.. _agenda.Categories.Select(c => new CategoryRow(c.Id, c.Name, AgendaUi.ColorBrush(c.Color), $"Excluir categoria {c.Name}"))];

    public string? CategoryError
    {
        get => _categoryError;
        private set
        {
            _categoryError = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Cria uma categoria com a próxima cor da paleta ainda não usada.</summary>
    public bool AddCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40)
        {
            CategoryError = "Digite um nome de 1 a 40 caracteres.";
            return false;
        }
        if (_agenda.Categories.Any(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            CategoryError = "Já existe uma categoria com esse nome.";
            return false;
        }
        var used = _agenda.Categories.Select(c => c.Color.ToUpperInvariant()).ToHashSet();
        var color = AgendaUi.CategoryPalette.FirstOrDefault(c => !used.Contains(c)) ?? AgendaUi.CategoryPalette[_agenda.Categories.Count % AgendaUi.CategoryPalette.Length];
        _agenda.SaveCategory(new AgendaCategory(Guid.CreateVersion7(), name.Trim(), color));
        CategoryError = null;
        return true;
    }

    public void DeleteCategory(Guid id) => _agenda.DeleteCategory(id);

    public bool AgendaNotifications
    {
        get => Notify.AgendaNotifications;
        set => _settings.UpdateNotifications(n => n with { AgendaNotifications = value });
    }

    // ---------- Notificações ----------

    private NotificationSettings Notify => _settings.Notifications;

    public string WindowsNotificationStatus => _notifications.ChannelStatus switch
    {
        NotificationChannelStatus.Enabled => "Notificações do Windows: ativas para o Diary Ghost.",
        NotificationChannelStatus.DisabledByUser => "O Windows está bloqueando as notificações do Diary Ghost (Configurações do Windows › Sistema › Notificações). Elas ficam só no histórico abaixo.",
        NotificationChannelStatus.Unavailable => "Notificações do Windows indisponíveis neste sistema; os avisos aparecem só no widget e no histórico.",
        _ => "Não foi possível consultar o estado das notificações do Windows.",
    };

    public bool TimerNotifications
    {
        get => Notify.TimerNotifications;
        set => _settings.UpdateNotifications(n => n with { TimerNotifications = value });
    }

    public bool SuppressInFullscreen
    {
        get => Notify.SuppressInFullscreen;
        set => _settings.UpdateNotifications(n => n with { SuppressInFullscreen = value });
    }

    public bool QuietHoursEnabled
    {
        get => Notify.QuietHoursEnabled;
        set => _settings.UpdateNotifications(n => n with { QuietHoursEnabled = value });
    }

    public string QuietStart
    {
        get => FormatMinutes(Notify.QuietStartMinutes);
        set => SetQuietTime(value, start: true);
    }

    public string QuietEnd
    {
        get => FormatMinutes(Notify.QuietEndMinutes);
        set => SetQuietTime(value, start: false);
    }

    public string PauseStatus => Notify.PausedUntil switch
    {
        null => "Notificações ativas.",
        { } until when until <= DateTimeOffset.UtcNow => "Notificações ativas.",
        { } until when until == DateTimeOffset.MaxValue => "Pausadas até você reativar (só alertas críticos passam).",
        { } until => $"Pausadas até {until.ToLocalTime():HH:mm} (só alertas críticos passam).",
    };

    public void PauseNotifications(TimeSpan? duration) => _notifications.PauseFor(duration);

    public void ResumeNotifications() => _notifications.Resume();

    public IReadOnlyList<NotificationRow> RecentNotifications =>
        [.. _notifications.Recent(8).Select(r => new NotificationRow(
            r.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")),
            r.Title,
            OutcomeText(r.Outcome)))];

    private static string OutcomeText(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.Shown => "exibida",
        DeliveryOutcome.SuppressedPaused => "silenciada (pausa)",
        DeliveryOutcome.SuppressedQuietHours => "silenciada (horário)",
        DeliveryOutcome.SuppressedFullscreen => "silenciada (tela cheia)",
        DeliveryOutcome.SuppressedCategoryOff => "desligada nas configurações",
        DeliveryOutcome.Duplicate => "duplicada",
        DeliveryOutcome.RateLimited => "limite de frequência",
        DeliveryOutcome.BlockedByWindows => "bloqueada pelo Windows",
        _ => "falhou",
    };

    private static string FormatMinutes(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private void SetQuietTime(string value, bool start)
    {
        // Validação da entrada: "HH:mm" (ou "H") entre 00:00 e 23:59.
        if (TimeSpan.TryParseExact(value?.Trim(), [@"h\:mm", @"hh\:mm", "%h"], System.Globalization.CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1))
        {
            var minutes = (int)t.TotalMinutes;
            _settings.UpdateNotifications(n => start ? n with { QuietStartMinutes = minutes } : n with { QuietEndMinutes = minutes });
            StatusMessage = null;
        }
        else
        {
            StatusMessage = "Horário inválido: use o formato HH:mm (por exemplo, 22:30).";
        }
        OnPropertyChanged(start ? nameof(QuietStart) : nameof(QuietEnd));
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

    public void Dispose() => _subscriptions.ForEach(s => s.Dispose());
}
