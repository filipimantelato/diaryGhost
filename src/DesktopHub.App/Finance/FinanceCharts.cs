using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Finance;

/// <summary>
/// Gráficos do módulo com primitivas do WPF (sem biblioteca). Todo gráfico tem um nome acessível com o resumo e
/// uma alternativa em tabela; cor nunca é o único sinal (rótulos e sinais acompanham).
/// </summary>
internal static class FinanceCharts
{
    private static string MonthShort(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMM", FinanceUi.PtBr).TrimEnd('.');

    /// <summary>Barra horizontal segmentada (decomposição do saldo previsto).</summary>
    public static FrameworkElement SegmentBar(IReadOnlyList<(long Value, Brush Brush, string Label)> segments)
    {
        var grid = new Grid { Height = 12, Margin = new Thickness(0, 14, 0, 0) };
        var total = segments.Sum(s => Math.Abs((decimal)s.Value));
        if (total == 0)
        {
            grid.Children.Add(new Border { Background = FinanceUi.Res("SurfaceElevatedBrush"), CornerRadius = new CornerRadius(6) });
            return grid;
        }
        var nonZero = segments.Where(s => s.Value != 0).ToList();
        for (var i = 0; i < nonZero.Count; i++)
        {
            var (value, brush, label) = nonZero[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)(Math.Abs((decimal)value) / total), GridUnitType.Star), MinWidth = 4 });
            var part = new Border
            {
                Background = brush,
                Margin = new Thickness(0, 0, i < nonZero.Count - 1 ? 2 : 0, 0),
                CornerRadius = new CornerRadius(i == 0 ? 6 : 0, i == nonZero.Count - 1 ? 6 : 0, i == nonZero.Count - 1 ? 6 : 0, i == 0 ? 6 : 0),
                ToolTip = label,
            };
            Grid.SetColumn(part, i);
            grid.Children.Add(part);
        }
        AutomationProperties.SetName(grid, string.Join("; ", nonZero.Select(s => s.Label)));
        return grid;
    }

    /// <summary>Barras de receitas × despesas por mês (o mês exibido destacado).</summary>
    public static FrameworkElement FlowBars(IReadOnlyList<MonthFlow> flows, int year, int month, string currency)
    {
        const double height = 120;
        var max = Math.Max(1, flows.Max(f => Math.Max(f.Income, f.Expense)));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var income = FinanceUi.Res("SuccessBrush");
        var expense = FinanceUi.Res("DangerBrush");
        for (var i = 0; i < flows.Count; i++)
        {
            var f = flows[i];
            var current = f.Year == year && f.Month == month;
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var (value, brush) in new[] { (f.Income, income), (f.Expense, expense) })
            {
                bars.Children.Add(new Rectangle
                {
                    Width = 12,
                    Height = Math.Max(value > 0 ? 2 : 0, value / (double)max * height),
                    Fill = brush,
                    Opacity = current ? 1 : 0.55,
                    RadiusX = 2,
                    RadiusY = 2,
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
            }
            bars.ToolTip = $"{FinanceUi.MonthTitle(f.Year, f.Month)}\nReceitas {FinanceUi.Amount(f.Income, currency)}\nDespesas {FinanceUi.Amount(f.Expense, currency)}\nResultado {FinanceUi.Amount(f.Net, currency, showPlus: true)}";
            Grid.SetColumn(bars, i);
            grid.Children.Add(bars);
            var label = AgendaUi.Text(MonthShort(f.Year, f.Month), "CaptionText", 11.5, current ? FinanceUi.Res("TextPrimaryBrush") : null);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 6, 0, 0);
            if (current)
            {
                label.FontWeight = FontWeights.SemiBold;
            }
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            grid.Children.Add(label);
        }
        AutomationProperties.SetName(grid, "Receitas e despesas por mês: " + string.Join("; ", flows.Select(f =>
            $"{FinanceUi.MonthTitle(f.Year, f.Month)}: receitas {FinanceUi.Amount(f.Income, currency)}, despesas {FinanceUi.Amount(f.Expense, currency)}")));
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(Legend(("Receitas", income), ("Despesas", expense)));
        return panel;
    }

    public static FrameworkElement FlowTable(IReadOnlyList<MonthFlow> flows, string currency) =>
        Table(["Mês", "Receitas", "Despesas", "Resultado"],
            flows.Select(f => new[] { FinanceUi.MonthTitle(f.Year, f.Month), FinanceUi.Amount(f.Income, currency), FinanceUi.Amount(f.Expense, currency), FinanceUi.Amount(f.Net, currency, showPlus: true) }));

    /// <summary>Gasto por categoria em barras horizontais (comparar comprimentos é mais fácil que ângulos).</summary>
    public static FrameworkElement CategoryBars(IReadOnlyList<CategorySpending> items, string currency, int max = 7)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var shown = items.Take(max).ToList();
        var rest = items.Skip(max).Sum(c => c.Amount);
        if (rest != 0)
        {
            shown.Add(new CategorySpending(null, $"Outras ({items.Count - max})", "#6E6E6E", rest, []));
        }
        var total = items.Sum(c => c.Amount);
        var top = Math.Max(1, shown.Max(c => c.Amount));
        foreach (var c in shown)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(FinanceUi.ColorDot(c.Color, 8));
            var text = AgendaUi.Text(c.Name, "CaptionText", 12.5, FinanceUi.Res("TextPrimaryBrush"));
            text.Margin = new Thickness(6, 0, 4, 0);
            text.MaxWidth = 104;
            name.Children.Add(text);
            row.Children.Add(name);

            var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            var ratio = Math.Max(0, c.Amount) / (double)top;
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            track.Children.Add(new Border { Background = AgendaUi.ColorBrush(c.Color), CornerRadius = new CornerRadius(4), MinWidth = c.Amount > 0 ? 3 : 0 });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);

            var pct = Money.Percent(c.Amount, total);
            var value = AgendaUi.Text($"{FinanceUi.Amount(c.Amount, currency)}  {(pct is { } p ? p.ToString("0", FinanceUi.PtBr) + "%" : "")}", "CaptionText", 12);
            value.MinWidth = 120;
            value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            if (c.Children.Count > 0)
            {
                row.ToolTip = string.Join("\n", c.Children.Select(ch => $"{ch.Name}: {FinanceUi.Amount(ch.Amount, currency)}"));
            }
            AutomationProperties.SetName(row, $"{c.Name}: {FinanceUi.Amount(c.Amount, currency)}{(pct is { } q ? $", {q.ToString("0", FinanceUi.PtBr)}% do total" : "")}");
            panel.Children.Add(row);
        }
        return panel;
    }

    public static FrameworkElement CategoryTable(IReadOnlyList<CategorySpending> items, string currency)
    {
        var total = items.Sum(c => c.Amount);
        var rows = new List<string[]>();
        foreach (var c in items)
        {
            rows.Add([c.Name, FinanceUi.Amount(c.Amount, currency), Money.Percent(c.Amount, total) is { } p ? p.ToString("0.0", FinanceUi.PtBr) + "%" : ""]);
            rows.AddRange(c.Children.Select(ch => new[] { "   " + ch.Name, FinanceUi.Amount(ch.Amount, currency), "" }));
        }
        return Table(["Categoria", "Gasto", "% do total"], rows);
    }

    public static FrameworkElement BalanceTable(IReadOnlyList<DailyBalance> days, string currency) =>
        Table(["Dia", "Saldo", ""], days.Select(d => new[] { FinanceUi.Date(d.Date), FinanceUi.Amount(d.Balance, currency), d.IsForecast ? "previsto" : "" }));

    public static StackPanel Legend(params (string Label, Brush Brush)[] items)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, brush) in items)
        {
            legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center });
            var text = AgendaUi.Text(label, "CaptionText", 11.5);
            text.Margin = new Thickness(5, 0, 14, 0);
            legend.Children.Add(text);
        }
        return legend;
    }

    /// <summary>Tabela simples de texto (alternativa acessível aos gráficos).</summary>
    public static Grid Table(string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (var c = 0; c < headers.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }
        var r = 0;
        void AddRow(string[] cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < cells.Length && c < headers.Length; c++)
            {
                var t = AgendaUi.Text(cells[c], header ? "CaptionText" : "BodyText", header ? 11.5 : 13);
                t.Margin = new Thickness(c == 0 ? 0 : 16, 2, 0, 2);
                if (c > 0)
                {
                    t.TextAlignment = TextAlignment.Right;
                }
                Grid.SetRow(t, r);
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
            r++;
        }
        AddRow(headers, header: true);
        foreach (var row in rows)
        {
            AddRow(row, header: false);
        }
        return grid;
    }
}

/// <summary>
/// Linha do saldo ao longo do mês: contínua até hoje (confirmado), tracejada depois (previsto). Desenhada em
/// <see cref="OnRender"/> para acompanhar o tamanho sem recriar elementos.
/// </summary>
internal sealed class BalanceChart : FrameworkElement
{
    private readonly IReadOnlyList<DailyBalance> _days;
    private readonly Pen _actual;
    private readonly Pen _forecast;
    private readonly Pen _zero;
    private readonly Brush _fill;
    private readonly Brush _label;
    private readonly Typeface _typeface = new("Segoe UI");

    public BalanceChart(IReadOnlyList<DailyBalance> days, string currency)
    {
        _days = days;
        Height = 150;
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        _actual = new Pen(accent, 2) { LineJoin = PenLineJoin.Round };
        _forecast = new Pen(accent, 2) { DashStyle = new DashStyle([2, 2], 0), LineJoin = PenLineJoin.Round };
        _zero = new Pen(FinanceUi.Res("BorderStrongBrush"), 1);
        _fill = accent.Clone();
        _fill.Opacity = 0.10;
        _label = FinanceUi.Res("TextTertiaryBrush");
        var first = days.Count > 0 ? days[0].Balance : 0;
        var last = days.Count > 0 ? days[^1].Balance : 0;
        var min = days.Count > 0 ? days.Min(d => d.Balance) : 0;
        AutomationProperties.SetName(this, $"Saldo ao longo do mês: começa em {FinanceUi.Amount(first, currency)}, termina em {FinanceUi.Amount(last, currency)} (previsto), menor saldo {FinanceUi.Amount(min, currency)}");
        ToolTip = AutomationProperties.GetName(this);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (_days.Count < 2 || ActualWidth < 20)
        {
            return;
        }
        var min = Math.Min(0, _days.Min(d => d.Balance));
        var max = Math.Max(0, _days.Max(d => d.Balance));
        if (max == min)
        {
            max = min + 1;
        }
        const double top = 8, bottom = 18;
        var h = ActualHeight - top - bottom;
        var w = ActualWidth;
        Point P(int i) => new(i * w / (_days.Count - 1), top + (1 - ((_days[i].Balance - min) / (double)(max - min))) * h);
        var zeroY = top + (1 - ((0 - min) / (double)(max - min))) * h;
        dc.DrawLine(_zero, new Point(0, zeroY), new Point(w, zeroY));

        // Área sob a parte confirmada.
        var lastActual = -1;
        for (var i = 0; i < _days.Count; i++)
        {
            if (!_days[i].IsForecast)
            {
                lastActual = i;
            }
        }
        if (lastActual > 0)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, zeroY), true, true);
                for (var i = 0; i <= lastActual; i++)
                {
                    g.LineTo(P(i), false, false);
                }
                g.LineTo(new Point(P(lastActual).X, zeroY), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(_fill, null, area);
        }
        for (var i = 1; i < _days.Count; i++)
        {
            dc.DrawLine(_days[i].IsForecast ? _forecast : _actual, P(i - 1), P(i));
        }
        if (lastActual >= 0 && lastActual < _days.Count - 1)
        {
            dc.DrawEllipse(_actual.Brush, null, P(lastActual), 3.5, 3.5);
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string text, double x, TextAlignment align)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, 11, _label, dpi) { TextAlignment = align };
            dc.DrawText(ft, new Point(x, ActualHeight - 14));
        }
        Label(_days[0].Date.Day.ToString(CultureInfo.InvariantCulture), 0, TextAlignment.Left);
        Label(_days[^1].Date.Day.ToString(CultureInfo.InvariantCulture), w, TextAlignment.Right);
        if (lastActual > 0 && lastActual < _days.Count - 1)
        {
            Label("hoje", P(lastActual).X, TextAlignment.Center);
        }
    }
}
