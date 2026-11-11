using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Calendário com visões de dia e semana (grade de horas, eventos sobrepostos lado a lado), mês e lista.
/// Tudo é desenhado a partir das ocorrências calculadas; nada fica rodando com a página fechada.
/// </summary>
public partial class CalendarPage : UserControl, IDisposable
{
    private enum CalendarView
    {
        Day,
        Week,
        Month,
        List,
    }

    private const double HourHeight = 44;
    private const int ListDays = 60;
    private static CalendarView s_lastView = CalendarView.Week;

    private readonly AgendaService _agenda;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _minute;
    private CalendarView _view = s_lastView;
    private DateTime _anchor;
    private double? _scrollOffset;

    private static bool s_showOverlays = true;
    private readonly IReadOnlyList<ICalendarOverlaySource> _overlays;
    private readonly IEventBus _bus;

    internal CalendarPage(AgendaService agenda, IEventBus bus, IReadOnlyList<ICalendarOverlaySource>? overlays = null)
    {
        _agenda = agenda;
        _bus = bus;
        _overlays = overlays ?? [];
        InitializeComponent();
        if (_overlays.Count > 0)
        {
            OverlayToggle.Visibility = Visibility.Visible;
            OverlayToggle.Content = string.Join(" · ", _overlays.Select(o => o.Name));
            foreach (var source in _overlays)
            {
                var context = SynchronizationContext.Current;
                _subscriptions.Add(source.Watch(() => context?.Post(_ => Render(), null)));
            }
        }
        _anchor = agenda.LocalNow.Date;
        _subscriptions.Add(bus.Subscribe<AgendaChanged>(_ => Render(), SynchronizationContext.Current));
        // Linha do "agora" e dia atual: atualiza uma vez por minuto, só com a página aberta.
        _minute = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _minute.Tick += (_, _) => Render();
        _minute.Start();
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Render()
    {
        OverlayToggle.DataContext = new ChipState(s_showOverlays);
        AutomationProperties.SetName(OverlayToggle, s_showOverlays ? "Ocultar vencimentos de finanças no calendário" : "Mostrar vencimentos de finanças no calendário");
        foreach (var (button, view) in new[] { (ViewDay, CalendarView.Day), (ViewWeek, CalendarView.Week), (ViewMonth, CalendarView.Month), (ViewList, CalendarView.List) })
        {
            button.DataContext = new ChipState(view == _view);
        }
        if (ViewHost.Content is FrameworkElement old && FindScroll(old) is { } scroll)
        {
            _scrollOffset = scroll.VerticalOffset;
        }
        switch (_view)
        {
            case CalendarView.Day:
                PeriodText.Text = AgendaFormat.DayTitle(_anchor);
                ViewHost.Content = BuildTimeGrid(_anchor, 1);
                break;
            case CalendarView.Week:
                var first = StartOfWeek(_anchor);
                var last = first.AddDays(6);
                PeriodText.Text = first.Month == last.Month
                    ? $"{first.Day}–{last.Day} de {last.ToString("MMMM 'de' yyyy", AgendaUi.PtBr)}"
                    : $"{first.ToString("d MMM", AgendaUi.PtBr)} – {last.ToString("d MMM yyyy", AgendaUi.PtBr)}";
                ViewHost.Content = BuildTimeGrid(first, 7);
                break;
            case CalendarView.Month:
                PeriodText.Text = AgendaFormat.MonthTitle(_anchor);
                ViewHost.Content = BuildMonth();
                break;
            default:
                PeriodText.Text = $"Próximos {ListDays} dias a partir de {AgendaFormat.Day(_anchor, _agenda.LocalNow)}";
                ViewHost.Content = BuildList();
                break;
        }
    }

    private static ScrollViewer? FindScroll(FrameworkElement root) =>
        root as ScrollViewer ?? (root is Panel p ? p.Children.OfType<ScrollViewer>().FirstOrDefault() : null);

    private static DateTime StartOfWeek(DateTime day)
    {
        var first = AgendaUi.PtBr.DateTimeFormat.FirstDayOfWeek;
        return day.Date.AddDays(-(((int)day.DayOfWeek - (int)first + 7) % 7));
    }

    // ---------- Navegação ----------

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CalendarView>(tag, out var view))
        {
            _view = s_lastView = view;
            _scrollOffset = null;
            Render();
        }
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _anchor = _agenda.LocalNow.Date;
        Render();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Shift(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Shift(1);

    private void Shift(int direction)
    {
        _anchor = _view switch
        {
            CalendarView.Day => _anchor.AddDays(direction),
            CalendarView.Week => _anchor.AddDays(7 * direction),
            CalendarView.Month => new DateTime(_anchor.Year, _anchor.Month, 1).AddMonths(direction),
            _ => _anchor.AddDays(ListDays * direction),
        };
        Render();
    }

    private void OpenDay(DateTime day)
    {
        _anchor = day.Date;
        _view = s_lastView = CalendarView.Day;
        _scrollOffset = null;
        Render();
    }

    // ---------- Edição ----------

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        var now = _agenda.LocalNow;
        var start = _anchor == now.Date ? now.Date.AddHours(now.Hour + 1) : _anchor.AddHours(9);
        OpenEditor(new EventEditor(_agenda, _agenda.NewEvent(start), isNew: true));
    }

    private void NewEventAt(DateTime start, bool allDay = false) =>
        OpenEditor(new EventEditor(_agenda, _agenda.NewEvent(start, allDay), isNew: true));

    private void Edit(EventOccurrence o)
    {
        if (_agenda.FindEvent(o.Event.Id) is { } current)
        {
            OpenEditor(new EventEditor(_agenda, current, isNew: false, o.Start));
        }
    }

    private void OpenEditor(EventEditor editor)
    {
        editor.Done += CloseEditor;
        EditorHost.Content = editor;
        EditorHost.Visibility = Visibility.Visible;
    }

    private void CloseEditor()
    {
        EditorHost.Content = null;
        EditorHost.Visibility = Visibility.Collapsed;
    }

    // ---------- Elementos ----------

    private Button EventChip(EventOccurrence o, string text, bool compact)
    {
        var e = o.Event;
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap };
        var button = new Button
        {
            Style = (Style)FindResource("EventChip"),
            Background = AgendaUi.CategoryBrush(_agenda, e.CategoryId, 0x44),
            BorderBrush = AgendaUi.CategoryBrush(_agenda, e.CategoryId),
            Content = label,
            ToolTip = Describe(o),
        };
        AutomationProperties.SetName(button, Describe(o));
        button.Click += (_, args) =>
        {
            args.Handled = true;
            Edit(o);
        };
        return button;
    }

    // ---------- Itens de outros módulos (somente leitura) ----------

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        s_showOverlays = !s_showOverlays;
        Render();
    }

    /// <summary>Itens sobrepostos por dia (vazio com o filtro desligado).</summary>
    private Dictionary<DateTime, List<CalendarOverlayItem>> OverlaysByDay(DateTime from, DateTime to)
    {
        var result = new Dictionary<DateTime, List<CalendarOverlayItem>>();
        if (!s_showOverlays)
        {
            return result;
        }
        foreach (var item in _overlays.SelectMany(s => s.Between(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to))))
        {
            var day = item.Date.ToDateTime(TimeOnly.MinValue);
            (result.TryGetValue(day, out var list) ? list : result[day] = []).Add(item);
        }
        return result;
    }

    private Button OverlayChip(CalendarOverlayItem item, bool compact)
    {
        var label = new TextBlock
        {
            Text = compact ? "◇ " + item.Title : $"◇ {item.Title}\n{item.Detail}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
        };
        var button = new Button
        {
            Style = (Style)FindResource("EventChip"),
            Background = AgendaUi.ColorBrush(item.Color, 0x22),
            BorderBrush = AgendaUi.ColorBrush(item.Color),
            Content = label,
            Opacity = item.Done ? 0.6 : 1,
            ToolTip = $"{item.Title} · {item.Detail} · abre Finanças",
        };
        AutomationProperties.SetName(button, $"{item.Title}, {item.Detail}, somente leitura: abre Finanças");
        button.Click += (_, args) =>
        {
            args.Handled = true;
            _bus.Publish(new OpenMainRequested(item.Page));
        };
        return button;
    }

    private string Describe(EventOccurrence o)
    {
        var start = ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone);
        var when = o.Event.AllDay ? $"{AgendaFormat.Day(start, _agenda.LocalNow)}, dia inteiro" : $"{AgendaFormat.Day(start, _agenda.LocalNow)}, {AgendaUi.TimeRange(o, start.Date, _agenda.LocalZone)}";
        var parts = new List<string> { o.Event.Title, when };
        if (o.Event.Location is { } location)
        {
            parts.Add(location);
        }
        if (o.Event.Recurrence is { } rule)
        {
            parts.Add(AgendaFormat.Describe(rule, o.Event.Start));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Dias locais cobertos por uma ocorrência (fim exclusivo à meia-noite).</summary>
    private IEnumerable<DateTime> DaysOf(EventOccurrence o)
    {
        var start = ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone).Date;
        var endWall = ZonedTime.ToWall(o.EndUtc, _agenda.LocalZone);
        var end = endWall.TimeOfDay == TimeSpan.Zero && endWall > start ? endWall.Date.AddDays(-1) : endWall.Date;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    private bool CoversWholeDay(EventOccurrence o, DateTime day)
    {
        if (o.Event.AllDay)
        {
            return true;
        }
        var start = ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone);
        var end = ZonedTime.ToWall(o.EndUtc, _agenda.LocalZone);
        return start <= day && end >= day.AddDays(1);
    }

    // ---------- Mês ----------

    private Grid BuildMonth()
    {
        var first = new DateTime(_anchor.Year, _anchor.Month, 1);
        var start = StartOfWeek(first);
        var today = _agenda.LocalNow.Date;
        var byDay = new Dictionary<DateTime, List<EventOccurrence>>();
        var overlays = OverlaysByDay(start, start.AddDays(41));
        foreach (var o in _agenda.OccurrencesForDays(start, start.AddDays(41)))
        {
            foreach (var d in DaysOf(o))
            {
                (byDay.TryGetValue(d, out var list) ? list : byDay[d] = []).Add(o);
            }
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
        }
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var name = AgendaUi.PtBr.DateTimeFormat.GetAbbreviatedDayName(start.AddDays(i).DayOfWeek);
            var header = AgendaUi.Text(name.TrimEnd('.'), "CaptionText");
            header.Margin = new Thickness(8, 8, 0, 6);
            Grid.SetColumn(header, i);
            grid.Children.Add(header);
        }

        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var inMonth = day.Month == first.Month;
            var panel = new StackPanel();
            var number = new TextBlock
            {
                Text = day.Day.ToString(AgendaUi.PtBr),
                FontSize = 12,
                FontWeight = day == today ? FontWeights.Bold : FontWeights.Normal,
                Foreground = day == today ? (Brush)FindResource("AccentBrush") : inMonth ? Res("TextPrimaryBrush") : Res("TextTertiaryBrush"),
                Margin = new Thickness(2, 0, 0, 3),
            };
            panel.Children.Add(number);
            var items = byDay.GetValueOrDefault(day) ?? [];
            var extra = overlays.GetValueOrDefault(day) ?? [];
            const int maxChips = 3;
            foreach (var o in items.OrderByDescending(o => CoversWholeDay(o, day)).ThenBy(o => o.StartUtc).Take(maxChips))
            {
                var text = CoversWholeDay(o, day) ? o.Event.Title : $"{AgendaFormat.Time(ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone))} {o.Event.Title}";
                var chip = EventChip(o, text, compact: true);
                chip.Margin = new Thickness(0, 0, 0, 2);
                panel.Children.Add(chip);
            }
            foreach (var item in extra.Take(Math.Max(0, maxChips - items.Count)))
            {
                var chip = OverlayChip(item, compact: true);
                chip.Margin = new Thickness(0, 0, 0, 2);
                panel.Children.Add(chip);
            }
            var total = items.Count + extra.Count;
            if (total > maxChips)
            {
                panel.Children.Add(AgendaUi.Text($"+{total - maxChips} mais", "CaptionText", 11));
            }
            var cell = new Button
            {
                Style = (Style)FindResource("DayCell"),
                Background = inMonth ? Brushes.Transparent : Res("BgBrush"),
                Content = panel,
                ToolTip = "Clique para ver o dia; clique duplo para criar um evento",
            };
            AutomationProperties.SetName(cell, $"{AgendaFormat.DayTitle(day)}, {items.Count} eventos" + (extra.Count > 0 ? $", {extra.Count} vencimentos" : ""));
            cell.Click += (_, _) => OpenDay(day);
            cell.MouseDoubleClick += (_, args) =>
            {
                args.Handled = true;
                NewEventAt(day.AddHours(9));
            };
            Grid.SetRow(cell, 1 + i / 7);
            Grid.SetColumn(cell, i % 7);
            grid.Children.Add(cell);
        }
        return grid;
    }

    // ---------- Dia e semana ----------

    private sealed record Segment(EventOccurrence Occurrence, DateTime Start, DateTime End)
    {
        public int Column { get; set; }
        public int Columns { get; set; } = 1;
    }

    private Grid BuildTimeGrid(DateTime firstDay, int days)
    {
        var today = _agenda.LocalNow;
        var occurrences = _agenda.OccurrencesForDays(firstDay, firstDay.AddDays(days - 1));
        var overlays = OverlaysByDay(firstDay, firstDay.AddDays(days - 1));

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // cabeçalho
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // dia inteiro
        outer.RowDefinitions.Add(new RowDefinition());                            // horas
        var header = NewDayColumns();
        var allDay = NewDayColumns();
        var body = NewDayColumns();
        body.Height = 24 * HourHeight;

        Grid NewDayColumns()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            for (var i = 0; i < days; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
            }
            return g;
        }

        // Rótulos das horas
        var labels = new Canvas();
        for (var h = 1; h < 24; h++)
        {
            var label = AgendaUi.Text($"{h:00}:00", "CaptionText", 10.5);
            Canvas.SetTop(label, h * HourHeight - 8);
            Canvas.SetLeft(label, 12);
            labels.Children.Add(label);
        }
        body.Children.Add(labels);

        for (var i = 0; i < days; i++)
        {
            var day = firstDay.AddDays(i);
            var isToday = day == today.Date;

            var title = new StackPanel { Margin = new Thickness(8, 8, 4, 6) };
            title.Children.Add(AgendaUi.Text(AgendaUi.PtBr.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek).TrimEnd('.'), "CaptionText",
                foreground: isToday ? (Brush)FindResource("AccentBrush") : null));
            var dayNumber = AgendaUi.Text(day.Day.ToString(AgendaUi.PtBr), "SubtitleText", 18, isToday ? (Brush)FindResource("AccentBrush") : null);
            title.Children.Add(dayNumber);
            var titleButton = new Button { Style = (Style)FindResource("DayCell"), Background = Brushes.Transparent, Content = title };
            AutomationProperties.SetName(titleButton, AgendaFormat.DayTitle(day));
            titleButton.Click += (_, _) => OpenDay(day);
            Grid.SetColumn(titleButton, i + 1);
            header.Children.Add(titleButton);

            var dayOccurrences = occurrences.Where(o => DaysOf(o).Contains(day)).ToList();
            var allDayPanel = new StackPanel { Margin = new Thickness(4, 2, 4, 4), MinHeight = 8 };
            foreach (var o in dayOccurrences.Where(o => CoversWholeDay(o, day)))
            {
                var chip = EventChip(o, o.Event.Title, compact: true);
                chip.Margin = new Thickness(0, 0, 0, 2);
                allDayPanel.Children.Add(chip);
            }
            foreach (var item in overlays.GetValueOrDefault(day) ?? [])
            {
                var chip = OverlayChip(item, compact: true);
                chip.Margin = new Thickness(0, 0, 0, 2);
                allDayPanel.Children.Add(chip);
            }
            Grid.SetColumn(allDayPanel, i + 1);
            allDay.Children.Add(allDayPanel);

            var canvas = BuildDayCanvas(day, isToday ? today : null,
                [.. dayOccurrences.Where(o => !CoversWholeDay(o, day)).Select(o => ToSegment(o, day))]);
            Grid.SetColumn(canvas, i + 1);
            body.Children.Add(canvas);
        }

        var allDayBorder = new Border { BorderBrush = Res("BorderTokenBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = allDay };
        var allDayLabel = AgendaUi.Text("dia todo", "CaptionText", 10);
        allDayLabel.Margin = new Thickness(8, 4, 0, 0);
        allDay.Children.Add(allDayLabel);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var offset = _scrollOffset ?? Math.Max(0, (firstDay <= today.Date && today.Date < firstDay.AddDays(days) ? today.Hour - 1 : 7) * HourHeight);
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Grid.SetRow(allDayBorder, 1);
        Grid.SetRow(scroll, 2);
        outer.Children.Add(header);
        outer.Children.Add(allDayBorder);
        outer.Children.Add(scroll);
        return outer;
    }

    private Segment ToSegment(EventOccurrence o, DateTime day)
    {
        var start = ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone);
        var end = ZonedTime.ToWall(o.EndUtc, _agenda.LocalZone);
        return new Segment(o, start < day ? day : start, end > day.AddDays(1) ? day.AddDays(1) : end);
    }

    private Canvas BuildDayCanvas(DateTime day, DateTime? now, List<Segment> segments)
    {
        var canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
        AutomationProperties.SetName(canvas, $"Horários de {AgendaFormat.DayTitle(day)}");
        canvas.ToolTip = "Clique duplo para criar um evento neste horário";
        var line = Res("BorderTokenBrush");
        var lines = new List<FrameworkElement>();
        for (var h = 0; h < 24; h++)
        {
            var hourLine = new Rectangle { Height = 1, Fill = line, IsHitTestVisible = false };
            Canvas.SetTop(hourLine, h * HourHeight);
            canvas.Children.Add(hourLine);
            lines.Add(hourLine);
        }
        var left = new Rectangle { Width = 1, Height = 24 * HourHeight, Fill = line, IsHitTestVisible = false };
        canvas.Children.Add(left);

        // Sobreposição: grupos de eventos que se cruzam dividem a largura em colunas.
        segments.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var group = new List<Segment>();
        var columnsEnd = new List<DateTime>();
        var groupEnd = DateTime.MinValue;
        void Flush()
        {
            group.ForEach(s => s.Columns = Math.Max(1, columnsEnd.Count));
            group.Clear();
            columnsEnd.Clear();
        }
        foreach (var s in segments)
        {
            if (group.Count > 0 && s.Start >= groupEnd)
            {
                Flush();
            }
            var column = columnsEnd.FindIndex(end => end <= s.Start);
            if (column < 0)
            {
                columnsEnd.Add(s.End);
                column = columnsEnd.Count - 1;
            }
            else
            {
                columnsEnd[column] = s.End;
            }
            s.Column = column;
            group.Add(s);
            groupEnd = group.Count == 1 ? s.End : (s.End > groupEnd ? s.End : groupEnd);
        }
        Flush();

        var blocks = new List<(Button Block, Segment Segment)>();
        foreach (var s in segments)
        {
            var minutes = (s.Start - day).TotalMinutes;
            var height = Math.Max(20, (s.End - s.Start).TotalMinutes * HourHeight / 60 - 2);
            var text = height >= 34
                ? $"{s.Occurrence.Event.Title}\n{AgendaUi.TimeRange(s.Occurrence, day, _agenda.LocalZone)}"
                : $"{AgendaFormat.Time(s.Start)} {s.Occurrence.Event.Title}";
            var block = EventChip(s.Occurrence, text, compact: height < 34);
            block.Height = height;
            Canvas.SetTop(block, minutes * HourHeight / 60 + 1);
            canvas.Children.Add(block);
            blocks.Add((block, s));
        }

        Rectangle? nowLine = null;
        if (now is { } n)
        {
            nowLine = new Rectangle { Height = 2, Fill = (Brush)FindResource("AccentBrush"), IsHitTestVisible = false };
            Canvas.SetTop(nowLine, (n - day).TotalMinutes * HourHeight / 60);
            canvas.Children.Add(nowLine);
        }

        canvas.SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            lines.ForEach(l => l.Width = width);
            if (nowLine is not null)
            {
                nowLine.Width = width;
            }
            foreach (var (block, s) in blocks)
            {
                var columnWidth = (width - 6) / s.Columns;
                block.Width = Math.Max(10, columnWidth - 2);
                Canvas.SetLeft(block, 3 + s.Column * columnWidth);
            }
        };
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && ReferenceEquals(e.OriginalSource, canvas))
            {
                var y = e.GetPosition(canvas).Y;
                var slot = Math.Clamp((int)(y / (HourHeight / 2)), 0, 47);
                NewEventAt(day.AddMinutes(slot * 30));
            }
        };
        return canvas;
    }

    // ---------- Lista ----------

    private ScrollViewer BuildList()
    {
        var today = _agenda.LocalNow;
        var panel = new StackPanel { Margin = new Thickness(18, 12, 18, 18) };
        var occurrences = _agenda.OccurrencesForDays(_anchor, _anchor.AddDays(ListDays - 1));
        var overlays = OverlaysByDay(_anchor, _anchor.AddDays(ListDays - 1));
        if (occurrences.Count == 0 && overlays.Count == 0)
        {
            panel.Children.Add(AgendaUi.Text("Nada marcado neste período. Use \"Novo evento\" ou dê um clique duplo num horário.", "CaptionText"));
        }
        var groups = occurrences.GroupBy(o => (ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone).Date < _anchor ? _anchor : ZonedTime.ToWall(o.StartUtc, _agenda.LocalZone).Date))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var day in groups.Keys.Concat(overlays.Keys).Distinct().Order())
        {
            var dayGroup = groups.GetValueOrDefault(day) ?? [];
            var relative = AgendaFormat.Day(day, today);
            var title = AgendaFormat.Capitalize(relative) + (relative.Contains('/', StringComparison.Ordinal) ? "" : " · " + AgendaFormat.DayTitle(day));
            var heading = AgendaUi.Text(title, "SubtitleText", 13.5, day == today.Date ? (Brush)FindResource("AccentBrush") : null);
            heading.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 16, 0, 6);
            panel.Children.Add(heading);
            foreach (var o in dayGroup)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(AgendaUi.Text(AgendaUi.TimeRange(o, day, _agenda.LocalZone), "CaptionText"));
                var meta = new List<string>();
                if (o.Event.Location is { } location)
                {
                    meta.Add(location);
                }
                if (_agenda.Category(o.Event.CategoryId) is { } category)
                {
                    meta.Add(category.Name);
                }
                if (o.Event.Recurrence is not null)
                {
                    meta.Add("↻ " + AgendaFormat.Describe(o.Event.Recurrence, o.Event.Start));
                }
                if (o.Event.ReminderMinutes.Count > 0)
                {
                    meta.Add("aviso " + string.Join(", ", o.Event.ReminderMinutes.Select(m => AgendaFormat.ReminderOffset(m).ToLower(AgendaUi.PtBr))));
                }
                var chip = EventChip(o, o.Event.Title + (meta.Count > 0 ? "\n" + string.Join(" · ", meta) : ""), compact: false);
                chip.FontSize = 12.5;
                Grid.SetColumn(chip, 1);
                row.Children.Add(chip);
                panel.Children.Add(row);
            }
            foreach (var item in overlays.GetValueOrDefault(day) ?? [])
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(AgendaUi.Text("vencimento", "CaptionText"));
                var chip = OverlayChip(item, compact: false);
                chip.FontSize = 12.5;
                Grid.SetColumn(chip, 1);
                row.Children.Add(chip);
                panel.Children.Add(row);
            }
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void Dispose()
    {
        _minute.Stop();
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
