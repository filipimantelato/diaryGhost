using System.Text.RegularExpressions;

namespace DesktopHub.Core.Settings;

public static class SettingsSections
{
    public const string General = "general";
    public const string Widget = "widget";
    public const string Media = "media";
    public const string Notifications = "notifications";
    public const string Productivity = "productivity";
}

public sealed record NotificationSettings
{
    public bool QuietHoursEnabled { get; init; }
    /// <summary>Minutos desde 00:00 (hora local). Padrão 22:00–07:00; pode atravessar a meia-noite.</summary>
    public int QuietStartMinutes { get; init; } = 22 * 60;
    public int QuietEndMinutes { get; init; } = 7 * 60;
    /// <summary>Pausa manual ("não incomodar" do app). null = ativo; DateTimeOffset.MaxValue = até reativar.</summary>
    public DateTimeOffset? PausedUntil { get; init; }
    public bool SuppressInFullscreen { get; init; } = true;
    public bool TimerNotifications { get; init; } = true;
    /// <summary>Lembretes de tarefas e avisos de eventos do calendário.</summary>
    public bool AgendaNotifications { get; init; } = true;

    public NotificationSettings Sanitize() => this with
    {
        QuietStartMinutes = Math.Clamp(QuietStartMinutes, 0, 1439),
        QuietEndMinutes = Math.Clamp(QuietEndMinutes, 0, 1439),
    };
}

public sealed record ProductivitySettings
{
    public Productivity.PomodoroConfig Pomodoro { get; init; } = new();
    /// <summary>Última duração escolhida no temporizador, em minutos.</summary>
    public int LastTimerMinutes { get; init; } = 25;

    public ProductivitySettings Sanitize() => this with
    {
        Pomodoro = (Pomodoro ?? new()).Sanitize(),
        LastTimerMinutes = Math.Clamp(LastTimerMinutes, 1, 24 * 60),
    };
}

public sealed record MediaSettings
{
    /// <summary>ID (AUMID) da fonte preferencial, ex.: "Spotify.exe". null = automático.</summary>
    public string? PreferredSourceId { get; init; }
    public bool ShowArtwork { get; init; } = true;

    public MediaSettings Sanitize() => this with
    {
        PreferredSourceId = string.IsNullOrWhiteSpace(PreferredSourceId) ? null : PreferredSourceId.Trim(),
    };
}

public enum CloseBehavior
{
    /// <summary>Fechar a janela principal mantém o app na bandeja (padrão).</summary>
    MinimizeToTray,
    /// <summary>Fechar a janela principal encerra o aplicativo.</summary>
    Exit,
}

public enum FullscreenBehavior
{
    /// <summary>Oculta o widget enquanto um app em tela cheia (ex.: jogo) estiver em primeiro plano.</summary>
    Hide,
    KeepVisible,
}

public enum WidgetLayout
{
    Compact,
    Expanded,
}

/// <summary>
/// Painéis do widget (abas na base da ilha). Os não implementados exibem explicitamente a fase prevista.
/// Valores são persistidos pelo nome — não renomeie; <see cref="Info"/> existe só por compatibilidade
/// com a Fase 1 e é convertido em <see cref="Home"/>.
/// </summary>
public enum WidgetPanel
{
    Media,
    Focus,
    Agenda,
    Info,
    Activity,
    Home,
    Volume,
}

/// <summary>Ferramenta exibida no painel Foco do widget.</summary>
public enum FocusTool
{
    Pomodoro,
    Timer,
    Stopwatch,
}

/// <summary>Posição da janela do widget em pixels físicos da área de trabalho virtual.</summary>
public sealed record WidgetPosition(int X, int Y);

public sealed partial record GeneralSettings
{
    /// <summary>Amarelo dessaturado ("dourado discreto") sobre cinzas neutros.</summary>
    public const string DefaultAccent = "#C9A94E";

    /// <summary>Padrão das versões 0.1–0.2 (roxo), substituído por <see cref="DefaultAccent"/>.</summary>
    public const string LegacyDefaultAccent = "#8B5CF6";

    /// <summary>Versão atual da paleta padrão; usada para migrar quem ainda tem o padrão antigo salvo.</summary>
    public const int CurrentPaletteVersion = 2;

    public bool StartWithWindows { get; init; }

    /// <summary>
    /// 0 em configurações salvas antes da troca de paleta (o campo não existia). Como o app grava a seção
    /// inteira a cada mudança, o roxo antigo aparece salvo mesmo para quem nunca o escolheu — por isso a
    /// migração acontece uma única vez, e uma escolha posterior de roxo é respeitada.
    /// </summary>
    public int PaletteVersion { get; init; }
    public bool StartMinimized { get; init; }
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.MinimizeToTray;
    public string AccentColor { get; init; } = DefaultAccent;
    public string Language { get; init; } = "pt-BR";
    public bool HasShownTrayHint { get; init; }

    /// <summary>Corrige valores inválidos (ex.: vindos de um banco editado à mão).</summary>
    public GeneralSettings Sanitize() => this with
    {
        AccentColor = !HexColor().IsMatch(AccentColor ?? "")
            ? DefaultAccent
            : PaletteVersion < CurrentPaletteVersion && string.Equals(AccentColor, LegacyDefaultAccent, StringComparison.OrdinalIgnoreCase)
                ? DefaultAccent
                : AccentColor!,
        PaletteVersion = CurrentPaletteVersion,
        Language = string.IsNullOrWhiteSpace(Language) ? "pt-BR" : Language,
        CloseBehavior = Enum.IsDefined(CloseBehavior) ? CloseBehavior : CloseBehavior.MinimizeToTray,
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}

public sealed record WidgetSettings
{
    public const double MinOpacity = 0.35;

    public bool Visible { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public bool Locked { get; init; }
    public double Opacity { get; init; } = 1.0;
    public bool AnimationsEnabled { get; init; } = true;

    /// <summary>
    /// Equalizador animado no modo compacto enquanto toca. Desligado por padrão: animação contínua numa
    /// janela em camadas custa ~0,18% de CPU contra ~0,01% com o indicador estático (benchmark Fase 2).
    /// </summary>
    public bool AnimatedPlaybackIndicator { get; init; }
    public FullscreenBehavior Fullscreen { get; init; } = FullscreenBehavior.Hide;
    public bool ExcludeFromCapture { get; init; }
    public WidgetLayout Layout { get; init; } = WidgetLayout.Compact;
    public WidgetPanel Panel { get; init; } = WidgetPanel.Media;
    public FocusTool FocusTool { get; init; } = FocusTool.Pomodoro;
    public WidgetPosition? Position { get; init; }

    public WidgetSettings Sanitize() => this with
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1.0) : 1.0,
        Fullscreen = Enum.IsDefined(Fullscreen) ? Fullscreen : FullscreenBehavior.Hide,
        Layout = Enum.IsDefined(Layout) ? Layout : WidgetLayout.Compact,
        Panel = !Enum.IsDefined(Panel) || Panel == WidgetPanel.Info ? WidgetPanel.Home : Panel,
    };
}
