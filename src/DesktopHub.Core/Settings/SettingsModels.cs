using System.Text.RegularExpressions;

namespace DesktopHub.Core.Settings;

public static class SettingsSections
{
    public const string General = "general";
    public const string Widget = "widget";
    public const string Media = "media";
    public const string Notifications = "notifications";
    public const string Productivity = "productivity";
    public const string News = "news";
    public const string Weather = "weather";
    public const string Dashboard = "dashboard";
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

public sealed record NewsSettings
{
    /// <summary>Desligado até o usuário ativar: buscar notícias é acesso à rede, então é uma escolha explícita.</summary>
    public bool Enabled { get; init; }
    public int RefreshMinutes { get; init; } = 30;
    public int RetentionDays { get; init; } = 14;
    public IReadOnlyList<string> FollowedTopics { get; init; } = [];
    public bool NotifyFollowedTopics { get; init; }

    public NewsSettings Sanitize() => this with
    {
        RefreshMinutes = Math.Clamp(RefreshMinutes, 15, 24 * 60),
        RetentionDays = Math.Clamp(RetentionDays, 1, 90),
        FollowedTopics = [.. (FollowedTopics ?? []).Select(t => t?.Trim() ?? "").Where(t => t.Length is > 1 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(30)],
    };
}

/// <summary>Local escolhido pelo usuário (busca por nome; nunca localização automática sem consentimento).</summary>
public sealed record WeatherLocation(string Name, string? Region, string? Country, double Latitude, double Longitude, string? TimeZone)
{
    public string DisplayName => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public sealed record WeatherSettings
{
    public WeatherLocation? Location { get; init; }
    public int RefreshMinutes { get; init; } = 30;
    public bool ShowInWidget { get; init; } = true;

    public WeatherSettings Sanitize() => this with
    {
        RefreshMinutes = Math.Clamp(RefreshMinutes, 15, 6 * 60),
        Location = Location is { } l && double.IsFinite(l.Latitude) && double.IsFinite(l.Longitude) &&
            Math.Abs(l.Latitude) <= 90 && Math.Abs(l.Longitude) <= 180 && !string.IsNullOrWhiteSpace(l.Name) ? l : null,
    };
}

/// <summary>Cartões do dashboard (persistidos pelo nome — não renomeie).</summary>
public enum DashboardCardKind
{
    Weather,
    NextEvent,
    Tasks,
    News,
    Media,
    Focus,
    System,
}

public sealed record DashboardCardSettings(DashboardCardKind Kind, bool Wide = false, bool Hidden = false);

public sealed record DashboardSettings
{
    public static IReadOnlyList<DashboardCardSettings> Default { get; } =
    [
        new(DashboardCardKind.Weather), new(DashboardCardKind.NextEvent), new(DashboardCardKind.Tasks),
        new(DashboardCardKind.News, Wide: true), new(DashboardCardKind.Media), new(DashboardCardKind.Focus), new(DashboardCardKind.System),
    ];

    public IReadOnlyList<DashboardCardSettings> Cards { get; init; } = Default;

    /// <summary>Remove repetidos e desconhecidos e acrescenta cartões novos que ainda não estão na lista.</summary>
    public DashboardSettings Sanitize()
    {
        var cards = (Cards ?? []).Where(c => c is not null && Enum.IsDefined(c.Kind)).DistinctBy(c => c.Kind).ToList();
        cards.AddRange(Default.Where(d => cards.All(c => c.Kind != d.Kind)));
        return this with { Cards = cards };
    }
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
