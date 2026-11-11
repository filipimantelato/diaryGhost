using System.Windows;
using System.Windows.Media;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Theme;

internal static class ThemeManager
{
    /// <summary>
    /// Cores de destaque oferecidas nas configurações. O destaque é usado em indicadores, bordas e
    /// interruptores — nunca como fundo de texto pequeno, porque nem todas têm contraste suficiente com branco.
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> AccentPresets { get; } =
    [
        ("Amarelo", "#C9A94E"),
        ("Azul", "#3B82F6"),
        ("Ciano", "#06B6D4"),
        ("Verde", "#10B981"),
        ("Grafite", "#A3A3A3"),
        ("Rosa", "#EC4899"),
        ("Roxo", "#8B5CF6"),
    ];

    /// <summary>Paletas dos tokens (mesmas chaves de Tokens.xaml). O claro segue a referência visual: fundo cinza-azulado, cartões brancos.</summary>
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Bg"] = ("#141414", "#F3F5F9"),
        ["Surface"] = ("#1C1C1C", "#FFFFFF"),
        ["SurfaceElevated"] = ("#262626", "#EEF1F6"),
        ["Border"] = ("#2E2E2E", "#E4E8EF"),
        ["BorderStrong"] = ("#3D3D3D", "#C9D0DB"),
        ["TextPrimary"] = ("#EDEDED", "#171B22"),
        ["TextSecondary"] = ("#A1A1A1", "#566070"),
        ["TextTertiary"] = ("#8F8F8F", "#6B7585"),
        ["Success"] = ("#34D399", "#15803D"),
        ["Warning"] = ("#F0985A", "#C2410C"),
        ["Danger"] = ("#F87171", "#DC2626"),
        ["Island"] = ("#F50F0F0F", "#F7FFFFFF"),
    };

    private static readonly Dictionary<string, string> BrushKeys = new()
    {
        ["Bg"] = "BgBrush",
        ["Surface"] = "SurfaceBrush",
        ["SurfaceElevated"] = "SurfaceElevatedBrush",
        ["Border"] = "BorderTokenBrush",
        ["BorderStrong"] = "BorderStrongBrush",
        ["TextPrimary"] = "TextPrimaryBrush",
        ["TextSecondary"] = "TextSecondaryBrush",
        ["TextTertiary"] = "TextTertiaryBrush",
        ["Success"] = "SuccessBrush",
        ["Warning"] = "WarningBrush",
        ["Danger"] = "DangerBrush",
        ["Island"] = "IslandBrush",
    };

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsLight => Current == AppTheme.Light;

    /// <summary>Tema trocado (janelas atualizam a barra de título; telas desenhadas em código redesenham).</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// Troca o tema em tempo real: os tokens são referenciados com DynamicResource no XAML, então trocar os pincéis
    /// atualiza o que já está na tela; telas montadas em código são redesenhadas pelo evento <see cref="ThemeChanged"/>.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var changed = theme != Current;
        Current = theme;
        var resources = Application.Current.Resources;
        foreach (var (token, colors) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(theme == AppTheme.Light ? colors.Light : colors.Dark);
            resources[token + "Color"] = color;
            resources[BrushKeys[token]] = Freeze(new SolidColorBrush(color));
        }
        resources["ArtworkShadeBrush"] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme == AppTheme.Light ? "#C8FFFFFF" : "#A6000000")));
        resources["GlassBrush"] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme == AppTheme.Light ? "#14000000" : "#40FFFFFF")));
        if (changed)
        {
            ThemeChanged?.Invoke();
        }
    }

    public static void ApplyAccent(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var resources = Application.Current.Resources;
        resources["AccentBrush"] = Freeze(new SolidColorBrush(color));
        resources["AccentSoftBrush"] = Freeze(new SolidColorBrush(color) { Opacity = 0.18 });
    }

    /// <summary>Respeita "Efeitos de animação" do Windows e a preferência do próprio app.</summary>
    public static bool AnimationsAllowed(bool appSetting) => appSetting && SystemParameters.ClientAreaAnimation;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze(); // pincéis congelados não geram notificações de mudança: mais leves
        return brush;
    }
}
