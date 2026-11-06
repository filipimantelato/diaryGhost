using System.Windows;
using System.Windows.Media;

namespace DesktopHub.App.Theme;

internal static class ThemeManager
{
    /// <summary>
    /// Cores de destaque oferecidas nas configurações. O destaque é usado em indicadores, bordas e
    /// interruptores — nunca como fundo de texto pequeno, porque nem todas têm contraste suficiente com branco.
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> AccentPresets { get; } =
    [
        ("Roxo", "#8B5CF6"),
        ("Azul", "#3B82F6"),
        ("Ciano", "#06B6D4"),
        ("Verde", "#10B981"),
        ("Âmbar", "#F59E0B"),
        ("Rosa", "#EC4899"),
    ];

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
