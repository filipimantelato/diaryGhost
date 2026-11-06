using System.Windows.Controls;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Página de módulo ainda não implementado. Diz explicitamente em que fase ele chega e o que fará —
/// em vez de exibir dados de exemplo que poderiam ser confundidos com dados reais.
/// </summary>
public partial class PlaceholderPage : UserControl
{
    public PlaceholderPage(string glyph, string title, int phase, string description, IReadOnlyList<string> planned)
    {
        InitializeComponent();
        GlyphText.Text = glyph;
        TitleBlock.Text = title;
        PhaseText.Text = phase > 0 ? $"Ainda não implementado · chega na Fase {phase} (ver ROADMAP.md)" : "Ainda não implementado";
        DescriptionText.Text = description;
        PlannedList.ItemsSource = planned;
    }
}
