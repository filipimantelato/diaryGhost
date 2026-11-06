using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Widget;

/// <summary>
/// Painel Agenda do widget: compromissos em andamento e próximos (hoje e amanhã) e tarefas de hoje/atrasadas,
/// com conclusão direto pelo widget. Atualiza ao mudar a agenda e uma vez por minuto (relógio do cabeçalho).
/// </summary>
public partial class WidgetWindow
{
    private const int MaxAgendaRows = 5;
    private readonly AgendaService _agenda;

    /// <summary>Clima novo ou opção alterada: só o texto do cabeçalho muda.</summary>
    internal void ShowWeather() => UpdateClock();

    internal void ShowAgenda()
    {
        if (_appliedPanel == WidgetPanel.Agenda && _appliedLayout == WidgetLayout.Expanded)
        {
            RenderAgenda();
        }
    }

    private void RenderAgenda()
    {
        var now = _agenda.Now;
        var local = _agenda.LocalNow;
        var endOfTomorrow = ZonedTime.ToUtc(local.Date.AddDays(2), _agenda.LocalZone);
        AgendaList.Children.Clear();

        var events = _agenda.OccurrencesBetween(now, endOfTomorrow)
            .Where(o => !o.Event.AllDay || ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone).Date <= local.Date)
            .Take(3)
            .ToList();
        foreach (var o in events)
        {
            var start = ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone);
            string when;
            if (o.Event.AllDay)
            {
                when = "hoje";
            }
            else if (o.StartUtc <= now)
            {
                when = $"agora · até {AgendaFormat.Time(ZonedTime.ToWall(o.EndUtc, _agenda.LocalZone))}";
            }
            else if (o.StartUtc - now <= TimeSpan.FromHours(3))
            {
                when = $"em {AgendaFormat.Span(o.StartUtc - now)}";
            }
            else
            {
                when = start.Date == local.Date ? AgendaFormat.Time(start) : $"amanhã {AgendaFormat.Time(start)}";
            }
            var dot = new Ellipse { Width = 8, Height = 8, Fill = AgendaUi.CategoryBrush(_agenda, o.Event.CategoryId), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            AgendaList.Children.Add(AgendaRow(dot, o.Event.Title, when, o.StartUtc <= now && !o.Event.AllDay));
        }

        var tasks = _agenda.Tasks
            .Where(t => !t.IsCompleted && t.Due is { } due && due.Date <= local.Date)
            .OrderBy(t => _agenda.DueUtc(t))
            .ThenByDescending(t => t.Priority)
            .Take(MaxAgendaRows - events.Count)
            .ToList();
        foreach (var t in tasks)
        {
            var check = AgendaUi.IconButton("", $"Concluir {t.Title}", (_, _) => _agenda.CompleteTask(t.Id), size: 22);
            check.FontSize = 12;
            var when = _agenda.IsOverdue(t) ? "atrasada" : t.DueHasTime ? AgendaFormat.Time(t.Due!.Value) : "hoje";
            AgendaList.Children.Add(AgendaRow(check, t.Title, when, _agenda.IsOverdue(t)));
        }

        AgendaEmpty.Visibility = AgendaList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestResize();
    }

    private Grid AgendaRow(UIElement leading, string title, string when, bool highlight)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4), Height = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(leading);
        var titleText = new TextBlock { Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
        Grid.SetColumn(titleText, 1);
        row.Children.Add(titleText);
        var whenText = new TextBlock
        {
            Text = when,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = highlight ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextSecondaryBrush"),
        };
        Grid.SetColumn(whenText, 2);
        row.Children.Add(whenText);
        AutomationProperties.SetName(row, $"{title}, {when}");
        return row;
    }

    private void AgendaOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
        {
            _bus.Publish(new OpenMainRequested(page));
        }
    }
}
