using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopHub.Core.Tests;

/// <summary>
/// Contraste (WCAG 2.1) das cores do tema, lidas do próprio Tokens.xaml: texto normal ≥ 4,5:1 (AA) em todos
/// os fundos usados; o destaque (amarelo) ≥ 3:1 como elemento gráfico/texto grande.
/// </summary>
public sealed partial class ContrastTests
{
    private static readonly Dictionary<string, (double R, double G, double B)> Colors = Load();

    private static Dictionary<string, (double, double, double)> Load()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DesktopHub.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        var xaml = File.ReadAllText(Path.Combine(dir!, "src", "DesktopHub.App", "Theme", "Tokens.xaml"));
        var result = new Dictionary<string, (double, double, double)>();
        foreach (Match m in ColorToken().Matches(xaml))
        {
            var hex = m.Groups[2].Value;
            hex = hex.Length == 8 ? hex[2..] : hex; // ignora alfa
            result[m.Groups[1].Value] = (Channel(hex[..2]), Channel(hex[2..4]), Channel(hex[4..6]));
        }
        // Destaque padrão (definido como pincel, não como Color).
        result["Accent"] = (Channel("C9"), Channel("A9"), Channel("4E"));
        return result;
    }

    private static double Channel(string hex)
    {
        var c = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static double Ratio(string fg, string bg)
    {
        var (a, b) = (Luminance(Colors[fg]), Luminance(Colors[bg]));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Theory]
    [InlineData("TextPrimaryColor")]
    [InlineData("TextSecondaryColor")]
    [InlineData("TextTertiaryColor")]
    [InlineData("DangerColor")]
    [InlineData("WarningColor")]
    [InlineData("SuccessColor")]
    [InlineData("Accent")]
    public void Text_colors_meet_AA_on_every_background(string text)
    {
        foreach (var bg in new[] { "BgColor", "SurfaceColor", "SurfaceElevatedColor", "IslandColor" })
        {
            Assert.True(Ratio(text, bg) >= 4.5, $"{text} sobre {bg}: {Ratio(text, bg):0.00}:1");
        }
    }

    [Fact]
    public void Dark_text_on_accent_buttons_meets_AA()
    {
        Colors["ButtonText"] = (Channel("14"), Channel("14"), Channel("14"));
        Assert.True(Ratio("ButtonText", "Accent") >= 4.5);
    }

    [GeneratedRegex("""<Color x:Key="(\w+)">#([0-9A-Fa-f]{6,8})</Color>""")]
    private static partial Regex ColorToken();
}
