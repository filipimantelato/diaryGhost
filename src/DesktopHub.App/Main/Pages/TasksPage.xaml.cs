using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;

namespace DesktopHub.App.Main.Pages;

/// <summary>Tarefas agrupadas por prazo, com filtros, criação rápida, conclusão, adiamento e edição.</summary>
public partial class TasksPage : UserControl, IDisposable
{
    private enum TaskFilter
    {
        Open,
        Today,
        Upcoming,
        NoDate,
        Done,
    }

    private static TaskFilter s_lastFilter = TaskFilter.Open;
    private readonly AgendaService _agenda;
    private readonly List<IDisposable> _subscriptions = [];
    private TaskFilter _filter = s_lastFilter;

    internal TasksPage(AgendaService agenda, IEventBus bus)
    {
        _agenda = agenda;
        InitializeComponent();
        QuickAddBox.TextChanged += (_, _) => QuickAddHint.Visibility = QuickAddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<Choice<Guid?>> categories = [new("Todas as categorias", null), .. agenda.Categories.Select(c => new Choice<Guid?>(c.Name, c.Id))];
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedIndex = 0;
        _subscriptions.Add(bus.Subscribe<AgendaChanged>(_ => Render(), SynchronizationContext.Current));
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Render()
    {
        foreach (var (button, filter) in new[] { (FilterOpen, TaskFilter.Open), (FilterToday, TaskFilter.Today), (FilterUpcoming, TaskFilter.Upcoming), (FilterNoDate, TaskFilter.NoDate), (FilterDone, TaskFilter.Done) })
        {
            button.DataContext = new ChipState(filter == _filter);
        }

        var today = _agenda.LocalNow.Date;
        var category = (CategoryFilter.SelectedItem as Choice<Guid?>)?.Value;
        var all = _agenda.Tasks.Where(t => category is null || t.CategoryId == category).ToList();
        var open = all.Where(t => !t.IsCompleted).ToList();
        var overdue = open.Count(_agenda.IsOverdue);
        var dueToday = open.Count(t => t.Due?.Date == today && !_agenda.IsOverdue(t));
        SummaryText.Text = open.Count == 0
            ? "Nenhuma tarefa pendente."
            : $"{Plural(open.Count, "pendente", "pendentes")} · {Plural(overdue, "atrasada", "atrasadas")} · {dueToday} para hoje";

        ListPanel.Children.Clear();
        IEnumerable<(string Title, List<TaskItem> Items)> groups = _filter switch
        {
            TaskFilter.Done => [("Concluídas recentemente", [.. all.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAt).Take(100)])],
            TaskFilter.NoDate => [("Sem prazo", Sorted(open.Where(t => t.Due is null)))],
            TaskFilter.Today => [("Atrasadas", Sorted(open.Where(_agenda.IsOverdue))), ("Hoje", Sorted(open.Where(t => t.Due?.Date == today && !_agenda.IsOverdue(t))))],
            TaskFilter.Upcoming => [.. Enumerable.Range(0, 8).Select(i => today.AddDays(i)).Select(d =>
                (AgendaFormat.Capitalize(AgendaFormat.Day(d, today)) + " · " + AgendaFormat.DayTitle(d), Sorted(open.Where(t => t.Due?.Date == d && !_agenda.IsOverdue(t)))))],
            _ =>
            [
                ("Atrasadas", Sorted(open.Where(_agenda.IsOverdue))),
                ("Hoje", Sorted(open.Where(t => t.Due?.Date == today && !_agenda.IsOverdue(t)))),
                ("Amanhã", Sorted(open.Where(t => t.Due?.Date == today.AddDays(1)))),
                ("Próximos 7 dias", Sorted(open.Where(t => t.Due?.Date > today.AddDays(1) && t.Due?.Date <= today.AddDays(7)))),
                ("Mais tarde", Sorted(open.Where(t => t.Due?.Date > today.AddDays(7)))),
                ("Sem prazo", Sorted(open.Where(t => t.Due is null))),
            ],
        };

        foreach (var (title, items) in groups)
        {
            if (items.Count == 0)
            {
                continue;
            }
            var heading = AgendaUi.Text($"{title} ({items.Count})", "SubtitleText", 13.5, title == "Atrasadas" ? Res("DangerBrush") : null);
            heading.Margin = new Thickness(0, ListPanel.Children.Count == 0 ? 6 : 18, 0, 4);
            ListPanel.Children.Add(heading);
            items.ForEach(t => ListPanel.Children.Add(Row(t, today)));
        }
        if (ListPanel.Children.Count == 0)
        {
            var empty = AgendaUi.Text(_filter == TaskFilter.Done ? "Nenhuma tarefa concluída ainda." : "Nada aqui. Digite acima para criar uma tarefa.", "CaptionText");
            empty.Margin = new Thickness(0, 8, 0, 0);
            ListPanel.Children.Add(empty);
        }
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private List<TaskItem> Sorted(IEnumerable<TaskItem> items) =>
        [.. items.OrderBy(t => _agenda.DueUtc(t) ?? DateTimeOffset.MaxValue).ThenByDescending(t => t.Priority).ThenBy(t => t.CreatedAt)];

    private Grid Row(TaskItem t, DateTime today)
    {
        var row = new Grid { Margin = new Thickness(-6, 0, 0, 2), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = AgendaUi.IconButton(t.IsCompleted ? "" : "", t.IsCompleted ? $"Reabrir {t.Title}" : $"Concluir {t.Title}", (_, _) =>
        {
            if (t.IsCompleted)
            {
                _agenda.ReopenTask(t.Id);
            }
            else
            {
                _agenda.CompleteTask(t.Id);
            }
        }, size: 32);
        check.FontSize = 15;
        if (t.IsCompleted)
        {
            check.Foreground = Res("SuccessBrush");
        }
        else if (t.Priority >= TaskPriority.High)
        {
            check.Foreground = AgendaUi.PriorityBrush(t.Priority);
        }
        row.Children.Add(check);

        var text = new StackPanel { Margin = new Thickness(4, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
        var title = AgendaUi.Text(t.Title, "BodyText", foreground: t.IsCompleted ? Res("TextTertiaryBrush") : null);
        title.TextWrapping = TextWrapping.Wrap;
        if (t.IsCompleted)
        {
            title.TextDecorations = TextDecorations.Strikethrough;
        }
        text.Children.Add(title);
        var meta = new WrapPanel();
        void Meta(string value, Brush? brush = null)
        {
            var block = AgendaUi.Text(value, "CaptionText", 11.5, brush);
            block.Margin = new Thickness(0, 1, 12, 0);
            meta.Children.Add(block);
        }
        if (t.IsCompleted && t.CompletedAt is { } done)
        {
            var doneWall = ZonedTime.ToWall(done, _agenda.LocalZone);
            Meta($"concluída {AgendaFormat.Day(doneWall, today)} às {AgendaFormat.Time(doneWall)}");
        }
        else if (t.Due is { } due)
        {
            var dueText = t.DueHasTime ? $"{AgendaFormat.Day(due, today)} às {AgendaFormat.Time(due)}" : AgendaFormat.Day(due, today);
            Meta((_agenda.IsOverdue(t) ? "venceu " : "prazo ") + dueText, _agenda.IsOverdue(t) ? Res("DangerBrush") : null);
        }
        if (t.Priority != TaskPriority.Normal)
        {
            Meta("prioridade " + AgendaFormat.Priority(t.Priority).ToLower(AgendaUi.PtBr), AgendaUi.PriorityBrush(t.Priority));
        }
        if (_agenda.Category(t.CategoryId) is { } category)
        {
            Meta("● " + category.Name, AgendaUi.ColorBrush(category.Color));
        }
        if (t.Recurrence is { } rule && t.Due is { } start)
        {
            Meta("↻ " + AgendaFormat.Describe(rule, t.RecurrenceStart ?? start));
        }
        if (t.ReminderMinutes is { } minutes && !t.IsCompleted)
        {
            Meta("aviso " + AgendaFormat.ReminderOffset(minutes).ToLower(AgendaUi.PtBr));
        }
        if (meta.Children.Count > 0)
        {
            text.Children.Add(meta);
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.0 };
        if (!t.IsCompleted)
        {
            actions.Children.Add(AgendaUi.IconButton("", "Adiar ou reagendar", (s, _) => ShowSnoozeMenu((Button)s, t)));
        }
        actions.Children.Add(AgendaUi.IconButton("", "Editar", (_, _) => OpenEditor(new TaskEditor(_agenda, t, isNew: false))));
        actions.Children.Add(AgendaUi.IconButton("", "Excluir", (_, _) => _agenda.DeleteTask(t.Id)));
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        // Ações aparecem ao passar o mouse ou com foco do teclado na linha (continuam acessíveis por Tab).
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = row.IsKeyboardFocusWithin ? 1 : 0;
        row.IsKeyboardFocusWithinChanged += (_, e) => actions.Opacity = (bool)e.NewValue || row.IsMouseOver ? 1 : 0;
        row.MouseLeftButtonUp += (_, e) =>
        {
            // Clique na linha (fora dos botões) abre a edição.
            for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, row); node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase)
                {
                    return;
                }
            }
            OpenEditor(new TaskEditor(_agenda, t, isNew: false));
        };
        AutomationProperties.SetName(row, t.Title);
        return row;
    }

    private void ShowSnoozeMenu(Button anchor, TaskItem t)
    {
        var now = _agenda.LocalNow;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Add(string header, DateTime due, bool hasTime)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _agenda.SaveTask(t with { Due = due, DueHasTime = hasTime, TimeZoneId = hasTime ? _agenda.LocalZone.Id : t.TimeZoneId });
            menu.Items.Add(item);
        }
        var baseTime = t.DueHasTime && t.Due is { } d ? d.TimeOfDay : TimeSpan.FromHours(9);
        Add("Daqui a 1 hora", now.AddHours(1).AddSeconds(-now.Second), true);
        Add("Hoje às 18:00", now.Date.AddHours(18), true);
        Add("Amanhã", now.Date.AddDays(1) + baseTime, t.DueHasTime);
        Add("Próxima semana", now.Date.AddDays(7) + baseTime, t.DueHasTime);
        var other = new MenuItem { Header = "Escolher data…" };
        other.Click += (_, _) => OpenEditor(new TaskEditor(_agenda, t, isNew: false));
        menu.Items.Add(other);
        menu.IsOpen = true;
    }

    private void QuickAdd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(QuickAddBox.Text))
        {
            return;
        }
        var category = (CategoryFilter.SelectedItem as Choice<Guid?>)?.Value;
        var due = _filter == TaskFilter.NoDate ? (DateTime?)null : _agenda.LocalNow.Date;
        _agenda.SaveTask(_agenda.NewTask() with { Title = QuickAddBox.Text, Due = due, CategoryId = category });
        QuickAddBox.Clear();
        e.Handled = true;
    }

    private void NewDetailed_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(new TaskEditor(_agenda, _agenda.NewTask() with { Title = QuickAddBox.Text.Trim() }, isNew: true));

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TaskFilter>(tag, out var filter))
        {
            _filter = s_lastFilter = filter;
            Render();
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Render();
        }
    }

    private void OpenEditor(TaskEditor editor)
    {
        editor.Done += () =>
        {
            EditorHost.Content = null;
            EditorHost.Visibility = Visibility.Collapsed;
            QuickAddBox.Clear();
        };
        EditorHost.Content = editor;
        EditorHost.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
