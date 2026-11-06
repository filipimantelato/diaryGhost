using System.Text.RegularExpressions;

namespace DesktopHub.Core.Settings;

public static class SettingsSections
{
    public const string General = "general";
    public const string Widget = "widget";
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

/// <summary>Painéis do widget. Os não implementados ainda exibem explicitamente a fase prevista.</summary>
public enum WidgetPanel
{
    Media,
    Focus,
    Agenda,
    Info,
    Activity,
}

/// <summary>Posição da janela do widget em pixels físicos da área de trabalho virtual.</summary>
public sealed record WidgetPosition(int X, int Y);

public sealed partial record GeneralSettings
{
    public const string DefaultAccent = "#8B5CF6";

    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.MinimizeToTray;
    public string AccentColor { get; init; } = DefaultAccent;
    public string Language { get; init; } = "pt-BR";
    public bool HasShownTrayHint { get; init; }

    /// <summary>Corrige valores inválidos (ex.: vindos de um banco editado à mão).</summary>
    public GeneralSettings Sanitize() => this with
    {
        AccentColor = HexColor().IsMatch(AccentColor ?? "") ? AccentColor! : DefaultAccent,
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
    public FullscreenBehavior Fullscreen { get; init; } = FullscreenBehavior.Hide;
    public bool ExcludeFromCapture { get; init; }
    public WidgetLayout Layout { get; init; } = WidgetLayout.Compact;
    public WidgetPanel Panel { get; init; } = WidgetPanel.Media;
    public WidgetPosition? Position { get; init; }

    public WidgetSettings Sanitize() => this with
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1.0) : 1.0,
        Fullscreen = Enum.IsDefined(Fullscreen) ? Fullscreen : FullscreenBehavior.Hide,
        Layout = Enum.IsDefined(Layout) ? Layout : WidgetLayout.Compact,
        Panel = Enum.IsDefined(Panel) ? Panel : WidgetPanel.Media,
    };
}
