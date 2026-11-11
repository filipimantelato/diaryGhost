using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>Peças visuais compartilhadas pelas telas de Finanças: valores grandes, selos de ícone e títulos de seção.</summary>
public partial class FinancePage
{
    // ---------- Peças visuais ----------

    /// <summary>Valor grande: símbolo e centavos menores, como na referência ("R$ 5.304,28").</summary>
    private StackPanel BigMoney(long minor, double size, Brush? brush = null, string? automationName = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var currency = _finance.BaseCurrency;
        var symbol = Money.Symbol(currency);
        var secondary = FinanceUi.Res("TextTertiaryBrush");
        TextBlock Part(string text, double s, Brush? b, FontWeight weight)
        {
            var t = AgendaUi.Text(text, "TitleText", s, b);
            t.FontWeight = weight;
            t.VerticalAlignment = VerticalAlignment.Bottom;
            return t;
        }
        var sign = minor < 0 ? "−" : "";
        var symbolPart = Part(sign + symbol + " ", size * 0.5, secondary, FontWeights.SemiBold);
        symbolPart.Margin = new Thickness(0, 0, 0, size * 0.1);
        panel.Children.Add(symbolPart);
        if (FinanceUi.Privacy)
        {
            panel.Children.Add(Part("••••", size, brush, FontWeights.Bold));
        }
        else
        {
            var text = Money.Format(Math.Abs(minor), currency, symbol: false);
            var comma = text.LastIndexOf(',');
            panel.Children.Add(Part(comma < 0 ? text : text[..comma], size, brush, FontWeights.Bold));
            if (comma >= 0)
            {
                var cents = Part(text[comma..], size * 0.5, secondary, FontWeights.SemiBold);
                cents.Margin = new Thickness(1, 0, 0, size * 0.1);
                panel.Children.Add(cents);
            }
        }
        AutomationProperties.SetName(panel, automationName ?? FinanceUi.Amount(minor, currency));
        return panel;
    }

    /// <summary>Selo com ícone no canto do cartão (o lugar das ilustrações da referência).</summary>
    private static Border Badge(string glyph, string color, double size = 44)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = size * 0.45,
            Foreground = AgendaUi.ColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.3),
            Background = AgendaUi.ColorBrush(color, 38),
            Child = text,
            VerticalAlignment = VerticalAlignment.Top,
        };
        return badge;
    }

    private static TextBlock Caps(string text)
    {
        var t = AgendaUi.Text(text.ToUpper(FinanceUi.PtBr), "CaptionText", 10.5);
        t.FontWeight = FontWeights.SemiBold;
        t.Margin = new Thickness(0, 14, 0, 6);
        return t;
    }

    /// <summary>Título de seção (organiza a tela em blocos com nome).</summary>
    private TextBlock SectionTitle(string title, string subtitle)
    {
        var text = new TextBlock { Margin = new Thickness(2, ListPanel.Children.Count == 0 ? 0 : 10, 0, 10), TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new System.Windows.Documents.Run(title) { FontSize = 16, FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run("   " + subtitle) { FontSize = 12, Foreground = FinanceUi.Res("TextTertiaryBrush") });
        AutomationProperties.SetName(text, $"{title}: {subtitle}");
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        return text;
    }
}
