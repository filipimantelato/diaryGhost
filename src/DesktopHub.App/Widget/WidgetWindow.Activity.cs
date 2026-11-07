using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopHub.App.Main.Pages;
using DesktopHub.Core.Activity;
using DesktopHub.Core.Agenda;

namespace DesktopHub.App.Widget;

/// <summary>Painel Atividade e cartão do Início: estado das sessões recentes do Claude Code.</summary>
public partial class WidgetWindow
{
    private readonly ActivityService _activity;

    internal void ShowActivity() => RenderActivity();

    private void RenderActivity()
    {
        var sessions = _activity.Sessions;
        var latest = sessions.Count > 0 ? sessions[0] : null;
        HomeActivityText.Text = latest is null
            ? _activity.IsListening ? "Sem atividade recente" : "Integração desligada"
            : $"{ActivityText.State(latest)}{(latest.Project is null ? "" : " · " + latest.Project)}";

        if (ActivityBody.Visibility != Visibility.Visible)
        {
            return;
        }
        ActivityList.Children.Clear();
        foreach (var e in sessions.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4), Height = 24 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var highlight = e.Kind == ActivityKind.Waiting;
            row.Children.Add(new TextBlock
            {
                Text = ActivityText.Glyph(e.Kind),
                Style = (Style)FindResource("IconText"),
                FontSize = 12,
                Foreground = highlight ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondaryBrush"),
            });
            var title = new TextBlock
            {
                Text = $"{e.Project ?? "Claude Code"} — {ActivityText.State(e)}",
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0),
                ToolTip = e.Message,
            };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            var ago = DateTimeOffset.Now - e.ReceivedAt;
            var when = new TextBlock
            {
                Text = ago < TimeSpan.FromMinutes(1) ? "agora" : $"há {AgendaFormat.Span(ago)}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = highlight ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondaryBrush"),
            };
            Grid.SetColumn(when, 2);
            row.Children.Add(when);
            AutomationProperties.SetName(row, $"{e.Project}, {ActivityText.State(e)}");
            ActivityList.Children.Add(row);
        }
        ActivityEmpty.Visibility = ActivityList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestResize();
    }
}
