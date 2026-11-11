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

/// <summary>
/// Visão geral em grade de cartões (layout inspirado na referência visual do usuário): saldo previsto com a
/// decomposição em colunas, contas a pagar por dia, para onde vai o dinheiro, cartões com a fatura, orçamento,
/// metas, gráficos e indicadores. Todo número vem do <see cref="FinanceService"/>; nada é de exemplo.
/// </summary>
public partial class FinancePage
{
    private static int s_cardIndex;
    private static int? s_billDay;

    /// <summary>Um item do mês a pagar ou receber (pendente, recorrência prevista ou fatura).</summary>
    private sealed record DueItem(DateOnly Date, string Label, long Signed, bool Planned, string Glyph, string Color, Action Open, Action? Check, string CheckName);

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

    private static Border Pill(string text, Brush foreground, Brush? background = null)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Background = background ?? FinanceUi.Res("SurfaceElevatedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = AgendaUi.Text(text, "CaptionText", 11, foreground),
        };
        return pill;
    }

    private static TextBlock Caps(string text)
    {
        var t = AgendaUi.Text(text.ToUpper(FinanceUi.PtBr), "CaptionText", 10.5);
        t.FontWeight = FontWeights.SemiBold;
        t.Margin = new Thickness(0, 14, 0, 6);
        return t;
    }

    /// <summary>Grade de 3 colunas; em janelas estreitas, uma coluna só (na ordem em que as peças foram dadas).</summary>
    private UIElement Grid3(params (UIElement Element, int Row, int Column, int Span)[] items)
    {
        if (_narrow)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, -12, 0) };
            foreach (var (element, _, _, _) in items)
            {
                stack.Children.Add(element);
            }
            return stack;
        }
        var grid = new Grid { Margin = new Thickness(0, 0, -12, 0) };
        for (var c = 0; c < 3; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = items.Max(i => i.Row) + 1;
        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        foreach (var (element, row, column, span) in items)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
        return grid;
    }

    // ---------- Visão geral ----------

    private void RenderOverview()
    {
        if (_finance.Accounts.Count == 0)
        {
            ListCard.Background = FinanceUi.Res("SurfaceBrush");
            ListPanel.Margin = new Thickness(16, 8, 16, 16);
            RenderTransactions(); // estado vazio: convite para cadastrar a primeira conta
            return;
        }
        var o = _finance.Overview(_year, _month);
        var (start, end) = FinanceService.MonthRange(_year, _month);
        var monthName = FinanceUi.MonthTitle(_year, _month).Split(' ')[0].ToLower(FinanceUi.PtBr);
        var previousName = new DateTime(_year, _month, 1).AddMonths(-1).ToString("MMMM", FinanceUi.PtBr);
        var isPast = o.Reference == end && end < _finance.Today;
        var due = DueItems(start, end);

        var hero = HeroTile(o, end, monthName, previousName, isPast);
        var bills = BillsTile(due, start, end, monthName);
        var spending = SpendingTile(start, end, monthName);
        var cards = CardsTile();
        var budget = BudgetTile(monthName);
        if (_narrow)
        {
            ListPanel.Children.Add(Grid3((hero, 0, 0, 1), (bills, 0, 0, 1), (cards, 0, 0, 1), (budget, 0, 0, 1), (spending, 0, 0, 1)));
        }
        else
        {
            ListPanel.Children.Add(Grid3((hero, 0, 0, 2), (bills, 0, 2, 1), (spending, 1, 0, 1), (cards, 1, 1, 1), (budget, 1, 2, 1)));
        }
        if (GoalsRow() is { } goals)
        {
            ListPanel.Children.Add(goals);
        }
        AddChartsAndIndicators(o, start, end, monthName, previousName, isPast);
        FooterText.Text = "Todos os valores são calculados dos seus lançamentos. Passe o mouse sobre um cartão para ver a definição.";
    }

    private Border HeroTile(MonthOverview o, DateOnly end, string monthName, string previousName, bool isPast)
    {
        var currency = _finance.BaseCurrency;
        var body = new StackPanel();
        var top = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.Children.Add(BigMoney(o.Forecast, 46, o.Forecast < 0 ? FinanceUi.Res("WarningBrush") : null, $"Saldo previsto: {FinanceUi.Amount(o.Forecast, currency)}"));
        var side = new StackPanel { Margin = new Thickness(18, 0, 0, 8), VerticalAlignment = VerticalAlignment.Bottom };
        if (o.HasPreviousMonth)
        {
            side.Children.Add(DeltaText(new Change(o.Forecast, o.PreviousForecast), $"vs. fim de {previousName}", higherIsGood: true));
        }
        side.Children.Add(Wrap(AgendaUi.Text(isPast ? $"saldo no último dia de {monthName}" : $"estimado para {FinanceUi.Date(end)}, depois de pagar contas e faturas", "CaptionText", 11.5)));
        Grid.SetColumn(side, 1);
        top.Children.Add(side);
        body.Children.Add(top);

        // Barra: o que entra (saldo + a receber) dividido em contas, faturas, outros e a sobra.
        var accent = (Brush)FindResource("AccentBrush");
        var receive = FinanceUi.Res("SuccessBrush");
        var pay = FinanceUi.Res("DangerBrush");
        var statements = FinanceUi.Res("WarningBrush");
        var other = FinanceUi.Res("TextTertiaryBrush");
        var inflow = Math.Max(0, o.AccountsBalance) + o.ToReceive + Math.Max(0, o.OtherChanges);
        string Share(long value) => inflow > 0 ? $" · {Percent(Math.Abs(value), inflow)}" : "";
        body.Children.Add(FinanceCharts.SegmentBar(
        [
            (o.ToPay, pay, $"Contas a pagar {FinanceUi.Amount(o.ToPay, currency)}"),
            (o.CardStatements, statements, $"Faturas {FinanceUi.Amount(o.CardStatements, currency)}"),
            (Math.Max(0, -o.OtherChanges), other, $"Outros {FinanceUi.Amount(o.OtherChanges, currency, showPlus: true)}"),
            (Math.Max(0, o.Forecast), receive, $"Sobra {FinanceUi.Amount(o.Forecast, currency)}"),
        ]));
        var legend = new UniformGrid { Rows = _narrow ? 0 : 1, Columns = _narrow ? 2 : 0, Margin = new Thickness(0, 10, 0, 0) };
        void Column(string label, long value, Brush? dot, string detail, bool plus = false, Brush? valueBrush = null)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (dot is not null)
            {
                head.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            }
            head.Children.Add(AgendaUi.Text(label, "CaptionText", 11.5));
            item.Children.Add(head);
            var amount = AgendaUi.Text(FinanceUi.Amount(value, currency, plus), "BodyText", 13.5, valueBrush);
            amount.FontWeight = FontWeights.SemiBold;
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(amount);
            if (detail.Length > 0)
            {
                var d = AgendaUi.Text(detail, "CaptionText", 11);
                d.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(d);
            }
            item.Children.Add(line);
            AutomationProperties.SetName(item, $"{label}: {FinanceUi.Amount(value, currency, plus)}");
            legend.Children.Add(item);
        }
        Column(isPast ? "Saldo confirmado" : "Saldo hoje", o.AccountsBalance, null, "");
        Column("A receber", o.ToReceive, receive, "", plus: true);
        Column("Contas a pagar", -o.ToPay, pay, Share(o.ToPay));
        if (o.CardStatements != 0)
        {
            Column("Faturas", -o.CardStatements, statements, Share(o.CardStatements));
        }
        if (o.OtherChanges != 0)
        {
            Column("Transf. e ajustes", o.OtherChanges, other, "", plus: true);
        }
        Column("Sobra", o.Forecast, receive, Share(Math.Max(0, o.Forecast)), valueBrush: o.Forecast >= 0 ? receive : FinanceUi.Res("WarningBrush"));
        body.Children.Add(legend);

        var notes = new List<string>();
        if (o.PlannedCount > 0)
        {
            notes.Add($"inclui {o.PlannedCount} {(o.PlannedCount == 1 ? "conta, assinatura ou receita prevista" : "contas, assinaturas e receitas previstas")}");
        }
        if (o.Reserved > 0)
        {
            notes.Add($"disponível hoje {FinanceUi.Amount(o.Available, currency)} ({FinanceUi.Amount(o.Reserved, currency)} reservados em metas)");
        }
        if (notes.Count > 0)
        {
            var text = string.Join(" · ", notes);
            body.Children.Add(Wrap(AgendaUi.Text(char.ToUpper(text[0], FinanceUi.PtBr) + text[1..] + ".", "CaptionText", 11.5)));
        }
        if (o.OverduePending > 0)
        {
            body.Children.Add(Wrap(AgendaUi.Text($"⚠ {o.OverduePending} {(o.OverduePending == 1 ? "lançamento pendente atrasado" : "lançamentos pendentes atrasados")}: confirme ou corrija a data (já estão no previsto).", "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
        }
        if (o.OverdueStatements > 0)
        {
            body.Children.Add(Wrap(AgendaUi.Text($"⚠ {o.OverdueStatements} {(o.OverdueStatements == 1 ? "fatura vencida" : "faturas vencidas")} com saldo em aberto: registre o pagamento em Cartões.", "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
        }
        return Panel("Saldo previsto", body,
            "Saldo previsto = saldo confirmado das contas do disponível + receitas ainda por entrar − contas e despesas ainda por sair (pendentes, com data futura ou recorrências previstas) " +
            "− faturas de cartão que vencem até o fim do mês ± transferências com contas fora do disponível e ajustes. Investimentos, contas arquivadas e outras moedas não entram.",
            Badge("\uE825", "#C9A94E"));
    }

    // ---------- Contas a pagar ----------

    private List<DueItem> DueItems(DateOnly start, DateOnly end)
    {
        var today = _finance.Today;
        var currency = _finance.BaseCurrency;
        var items = new List<DueItem>();
        foreach (var t in _finance.PendingUntil(end).Where(t => t.Date >= start || t.Date < today))
        {
            var tx = t;
            var signed = t.Kind == TransactionKind.Transfer ? -t.Amount : FinanceUi.SignedFor(t, null);
            items.Add(new DueItem(t.Date, t.Description, signed, false, FinanceUi.Glyph(t.Kind), signed > 0 ? "#8FB573" : "#C47A6B", () => OpenEditor(EditorFor(tx)),
                () => Try(() => _finance.Save(FinanceService.DraftFrom(tx) with { Status = TransactionStatus.Cleared })), $"Marcar {t.Description} como confirmado"));
        }
        if (end >= today)
        {
            foreach (var occ in _finance.OpenOccurrences(end).Where(x => x.Date >= start || x.Date < today))
            {
                var o = occ;
                items.Add(new DueItem(o.Date, o.Item.Description, o.Item.IsIncome ? o.Amount : -o.Amount, true,
                    o.Item.Kind == RecurringKind.Subscription ? "" : FinanceUi.Glyph(o.Item.IsIncome ? TransactionKind.Income : TransactionKind.Expense),
                    o.Item.IsIncome ? "#8FB573" : "#C9A94E", () => OpenEditor(new OccurrenceEditor(_finance, o)), () => OpenEditor(new OccurrenceEditor(_finance, o)),
                    $"Confirmar {o.Item.Description}"));
            }
            foreach (var card in _finance.Cards.Where(c => !c.IsArchived && c.Currency == currency))
            {
                foreach (var s in _finance.Statements(card.Id).Where(s => s.Due >= start && s.Due <= end && s.Remaining > 0 && s.Status != StatementStatus.Future))
                {
                    var cardId = card.Id;
                    var closing = s.Closing;
                    items.Add(new DueItem(s.Due, $"Fatura {card.Name}" + (s.Status == StatementStatus.Open ? " (aberta)" : ""), -s.Remaining, s.Status == StatementStatus.Open, "", card.Color,
                        () =>
                        {
                            _tab = Tab.Cards;
                            _selectedCard = cardId;
                            _selectedStatement = closing;
                            Render();
                        },
                        () => OpenEditor(NewPayment(cardId)), $"Pagar a fatura {card.Name}"));
                }
            }
        }
        return [.. items.OrderBy(i => i.Date).ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    private Border BillsTile(List<DueItem> due, DateOnly start, DateOnly end, string monthName)
    {
        var currency = _finance.BaseCurrency;
        var today = _finance.Today;
        var body = new StackPanel();
        var toPay = due.Where(d => d.Signed < 0).ToList();
        var total = -Money.Sum(toPay.Select(d => d.Signed));
        var occurrences = _finance.Occurrences(start, end).Where(o => !o.Item.IsIncome && o.State != OccurrenceState.Skipped).ToList();
        var paid = occurrences.Count(o => o.State == OccurrenceState.Confirmed);
        var statementsPaid = _finance.Cards.Where(c => !c.IsArchived).SelectMany(c => _finance.Statements(c.Id)).Count(s => s.Due >= start && s.Due <= end && s.Status == StatementStatus.Paid);
        var count = toPay.Count + paid + statementsPaid;
        var next = toPay.FirstOrDefault(d => d.Date >= today) ?? toPay.FirstOrDefault();

        body.Children.Add(BigMoney(total, 30, null, $"Total a pagar em {monthName}: {FinanceUi.Amount(total, currency)}"));
        var caption = next is null ? "nada a pagar até o fim do mês" :
            (next.Date < today ? "há contas atrasadas" : next.Date == today ? "a próxima vence hoje" : $"a próxima em {next.Date.DayNumber - today.DayNumber} {(next.Date.DayNumber - today.DayNumber == 1 ? "dia" : "dias")}") +
            (count > 0 ? $" · {paid + statementsPaid} de {count} pagas" : "");
        body.Children.Add(AgendaUi.Text(caption, "CaptionText", 11.5, next is not null && next.Date < today ? FinanceUi.Res("WarningBrush") : null));

        // Dias com vencimento (chips), como na referência; clicar filtra a lista.
        var days = due.Select(d => d.Date < today && d.Date < start ? today : d.Date).Select(d => d.Day).Distinct().OrderBy(d => d).ToList();
        if (s_billDay is { } sel && !days.Contains(sel))
        {
            s_billDay = null;
        }
        if (days.Count > 0)
        {
            var strip = new UniformGrid { Rows = 1, Columns = Math.Min(5, days.Count), Margin = new Thickness(0, 12, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
            var accent = (Brush)FindResource("AccentBrush");
            foreach (var day in days.Take(5))
            {
                var selected = s_billDay == day;
                var dayItems = due.Count(d => d.Date.Day == day);
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(AgendaUi.Text(day.ToString("00", FinanceUi.PtBr), "BodyText", 14, selected ? FinanceUi.Res("BgBrush") : null));
                content.Children.Add(AgendaUi.Text(new string('•', Math.Min(3, dayItems)), "CaptionText", 10, selected ? FinanceUi.Res("BgBrush") : accent));
                foreach (TextBlock t in content.Children)
                {
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                }
                var button = new Button
                {
                    Content = content,
                    Width = 52,
                    Height = 46,
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Background = selected ? accent : FinanceUi.Res("SurfaceElevatedBrush"),
                    BorderBrush = day == today.Day && today >= start && today <= end ? accent : FinanceUi.Res("BorderStrongBrush"),
                    BorderThickness = new Thickness(1),
                    Template = DayTemplate(),
                };
                AutomationProperties.SetName(button, $"Dia {day}: {dayItems} {(dayItems == 1 ? "item" : "itens")}{(selected ? ", filtrando" : "")}");
                var d0 = day;
                button.Click += (_, _) =>
                {
                    s_billDay = s_billDay == d0 ? null : d0;
                    Render();
                };
                strip.Children.Add(button);
            }
            body.Children.Add(strip);
        }

        var shown = due.Where(d => s_billDay is null || d.Date.Day == s_billDay).ToList();
        if (shown.Count == 0)
        {
            body.Children.Add(Paragraph("Nada a vencer até o fim do mês. Contas e assinaturas previstas, lançamentos pendentes e faturas aparecem aqui."));
        }
        foreach (var item in shown.Take(8))
        {
            body.Children.Add(DueRow(item));
        }
        if (shown.Count > 8)
        {
            body.Children.Add(AgendaUi.Text($"+ {shown.Count - 8} itens em Planejamento", "CaptionText", 11.5));
        }
        var open = Chip("Ver todas", (_, _) => OpenPlanning(PlanningView.Bills));
        AutomationProperties.SetName(open, "Ver todas as contas e assinaturas");
        return Panel("Contas a pagar" + (next is not null ? $" · dia {next.Date.Day:00}" : ""), body,
            "Lançamentos pendentes, contas, assinaturas e receitas previstas e faturas de cartão até o fim do mês, incluindo atrasados. O círculo confirma o pagamento (ou abre o pagamento da fatura).", open);
    }

    private static ControlTemplate? s_dayTemplate;

    private static ControlTemplate DayTemplate()
    {
        if (s_dayTemplate is not null)
        {
            return s_dayTemplate;
        }
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        s_dayTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
        s_dayTemplate.Seal();
        return s_dayTemplate;
    }

    private Grid DueRow(DueItem item)
    {
        var currency = _finance.BaseCurrency;
        var today = _finance.Today;
        var overdue = item.Date < today;
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Círculo de "pago" (como a lista da referência): confirma a ocorrência, o pendente ou abre o pagamento da fatura.
        var check = new Button
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 10, 0),
            Background = Brushes.Transparent,
            BorderBrush = overdue ? FinanceUi.Res("WarningBrush") : FinanceUi.Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1.5),
            Template = CircleTemplate(),
            Cursor = Cursors.Hand,
            ToolTip = item.CheckName,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(check, item.CheckName);
        check.Click += (_, _) => item.Check?.Invoke();
        row.Children.Add(check);
        var text = new StackPanel { Margin = new Thickness(0, 4, 8, 4) };
        text.Children.Add(AgendaUi.Text(item.Label, "BodyText", 13, item.Planned ? FinanceUi.Res("TextSecondaryBrush") : null));
        text.Children.Add(AgendaUi.Text(item.Date.ToString("dd/MM", FinanceUi.PtBr) + (overdue ? " · atrasado" : item.Planned ? " · previsto" : " · pendente"), "CaptionText", 11, overdue ? FinanceUi.Res("WarningBrush") : null));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var value = AgendaUi.Text(FinanceUi.Amount(item.Signed, currency, showPlus: true), "BodyText", 13, item.Signed > 0 ? FinanceUi.Res("SuccessBrush") : null);
        value.FontWeight = FontWeights.SemiBold;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (!check.IsMouseOver)
            {
                item.Open();
            }
        };
        AutomationProperties.SetName(row, $"{item.Label}, {FinanceUi.Date(item.Date)}, {(overdue ? "atrasado, " : "")}{FinanceUi.Amount(item.Signed, currency, showPlus: true)}");
        return row;
    }

    private static ControlTemplate? s_circleTemplate;

    private static ControlTemplate CircleTemplate()
    {
        if (s_circleTemplate is not null)
        {
            return s_circleTemplate;
        }
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        s_circleTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, Application.Current.FindResource("AccentSoftBrush")));
        s_circleTemplate.Triggers.Add(hover);
        s_circleTemplate.Seal();
        return s_circleTemplate;
    }

    // ---------- Para onde vai o dinheiro ----------

    private Border SpendingTile(DateOnly start, DateOnly end, string monthName)
    {
        var currency = _finance.BaseCurrency;
        var spending = _finance.SpendingByCategory(start, end);
        var body = new StackPanel();
        var total = Money.Sum(spending.Select(s => s.Amount));
        body.Children.Add(AgendaUi.Text("Total gasto no mês", "CaptionText", 11.5));
        body.Children.Add(BigMoney(total, 28, null, $"Total gasto em {monthName}: {FinanceUi.Amount(total, currency)}"));
        if (spending.Count == 0)
        {
            body.Children.Add(Paragraph($"Nenhuma despesa em {monthName}."));
        }
        else if (s_tables)
        {
            body.Children.Add(FinanceCharts.CategoryTable(spending, currency));
        }
        else
        {
            var top = spending.Where(s => s.Amount > 0).Take(6).ToList();
            var rest = total - Money.Sum(top.Select(s => s.Amount));
            List<(long, Brush, string)> segments = [.. top.Select(s => (s.Amount, (Brush)AgendaUi.ColorBrush(s.Color), $"{s.Name} {FinanceUi.Amount(s.Amount, currency)}"))];
            if (rest > 0)
            {
                segments.Add((rest, FinanceUi.Res("TextTertiaryBrush"), $"Outras {FinanceUi.Amount(rest, currency)}"));
            }
            body.Children.Add(FinanceCharts.SegmentBar(segments));
            if (top.Count >= 2)
            {
                var ends = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var right = AgendaUi.Text(Percent(top[1].Amount, total) + " " + top[1].Name, "CaptionText", 11);
                DockPanel.SetDock(right, Dock.Right);
                ends.Children.Add(right);
                ends.Children.Add(AgendaUi.Text(top[0].Name + " " + Percent(top[0].Amount, total), "CaptionText", 11));
                body.Children.Add(ends);
            }
            body.Children.Add(Caps("Por categoria"));
            foreach (var s in spending.Take(7))
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var value = AgendaUi.Text(FinanceUi.Amount(s.Amount, currency), "BodyText", 13);
                value.FontWeight = FontWeights.SemiBold;
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(value);
                var dot = FinanceUi.ColorDot(s.Color, 8);
                dot.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(dot);
                row.Children.Add(AgendaUi.Text(s.Name, "BodyText", 13));
                AutomationProperties.SetName(row, $"{s.Name}: {FinanceUi.Amount(s.Amount, currency)}, {Percent(s.Amount, total)}");
                body.Children.Add(row);
            }
        }
        return Panel("Gastos do mês", body, "Despesas do mês menos estornos, somando as subcategorias em cada categoria principal. Inclui pendentes e parcelas de cartão do mês.",
            TablesToggle());
    }

    // ---------- Cartões ----------

    private Border CardsTile()
    {
        var currency = _finance.BaseCurrency;
        var cards = _finance.Cards.Where(c => !c.IsArchived).ToList();
        var body = new StackPanel();
        if (cards.Count == 0)
        {
            body.Children.Add(Paragraph("Nenhum cartão cadastrado. Cadastre para ver faturas, limite e parcelas aqui."));
            var add = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Cadastrar cartão", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) => OpenEditor(new CardEditor(_finance, _finance.NewCard(), isNew: true));
            body.Children.Add(add);
            return Panel("Cartões", body, "Cartões de crédito ativos.", Badge("", "#6B9AC4"));
        }
        s_cardIndex = Math.Clamp(s_cardIndex, 0, cards.Count - 1);
        var card = cards[s_cardIndex];
        var summary = _finance.Summary(card.Id);
        var toPay = _finance.StatementToPay(card.Id);
        var dueMonth = toPay is null ? "" : toPay.Due.ToString("MMMM", FinanceUi.PtBr);
        body.Children.Add(AgendaUi.Text(toPay is null ? "Sem fatura em aberto" : $"Fatura de {dueMonth}", "CaptionText", 11.5));
        body.Children.Add(BigMoney(Math.Max(0, toPay?.Remaining ?? 0), 30, null, $"Fatura de {dueMonth}: {FinanceUi.Amount(Math.Max(0, toPay?.Remaining ?? 0), currency)}"));

        var nav = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(arrows, Dock.Right);
        var prev = AgendaUi.IconButton("", "Cartão anterior", (_, _) => { s_cardIndex = (s_cardIndex - 1 + cards.Count) % cards.Count; Render(); });
        var nextButton = AgendaUi.IconButton("", "Próximo cartão", (_, _) => { s_cardIndex = (s_cardIndex + 1) % cards.Count; Render(); });
        prev.IsEnabled = nextButton.IsEnabled = cards.Count > 1;
        arrows.Children.Add(prev);
        arrows.Children.Add(nextButton);
        nav.Children.Add(arrows);
        var view = Chip("Ver fatura", (_, _) =>
        {
            _tab = Tab.Cards;
            _selectedCard = card.Id;
            _selectedStatement = toPay?.Closing;
            Render();
        });
        AutomationProperties.SetName(view, $"Ver fatura do cartão {card.Name}");
        view.HorizontalAlignment = HorizontalAlignment.Left;
        nav.Children.Add(view);
        body.Children.Add(nav);

        body.Children.Add(CardVisual(card, summary, toPay));

        // Próximas faturas: a que vai ser paga e as seguintes (parcelas já lançadas).
        var upcoming = _finance.Statements(card.Id).Where(s => toPay is null ? s.Due >= _finance.Today : s.Closing >= toPay.Closing).Take(3).ToList();
        if (upcoming.Count > 0)
        {
            body.Children.Add(Caps("Próximas faturas"));
            var strip = new UniformGrid { Rows = 1, Columns = 3 };
            var max = Math.Max(1, upcoming.Max(s => Math.Max(0, s.Remaining)));
            var accent = (Brush)FindResource("AccentBrush");
            foreach (var s in upcoming)
            {
                var selected = s.Closing == toPay?.Closing;
                var tile = new Grid { Height = 92 };
                var fillBrush = accent.Clone();
                fillBrush.Opacity = selected ? 0.22 : 0.10;
                var height = 70.0 * Math.Max(0, s.Remaining) / max;
                tile.Children.Add(new Border { Background = fillBrush, Height = Math.Max(2, height), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 9, 9) });
                var labels = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
                var month = AgendaUi.Text(s.Due.ToString("MMM yy", FinanceUi.PtBr).Replace(".", "", StringComparison.Ordinal), "CaptionText", 11.5);
                month.HorizontalAlignment = HorizontalAlignment.Center;
                labels.Children.Add(month);
                if (selected || s.Status is StatementStatus.Open or StatementStatus.Overdue or StatementStatus.PartiallyPaid)
                {
                    var pill = Pill(FinanceUi.StatementStatus(s.Status), s.Status == StatementStatus.Overdue ? FinanceUi.Res("WarningBrush") : accent);
                    pill.HorizontalAlignment = HorizontalAlignment.Center;
                    pill.Margin = new Thickness(0, 2, 0, 0);
                    labels.Children.Add(pill);
                }
                tile.Children.Add(labels);
                var amount = AgendaUi.Text(FinanceUi.Amount(Math.Max(0, s.Remaining), currency), "CaptionText", 11.5, selected ? null : FinanceUi.Res("TextSecondaryBrush"));
                amount.HorizontalAlignment = HorizontalAlignment.Center;
                amount.VerticalAlignment = VerticalAlignment.Bottom;
                amount.Margin = new Thickness(0, 0, 0, 6);
                tile.Children.Add(amount);
                var frame = new Border
                {
                    Child = tile,
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(selected ? 1.5 : 1),
                    BorderBrush = selected ? accent : FinanceUi.Res("BorderTokenBrush"),
                    Margin = new Thickness(0, 0, 6, 0),
                    ClipToBounds = true,
                };
                AutomationProperties.SetName(frame, $"Fatura que vence em {FinanceUi.Date(s.Due)}: {FinanceUi.Amount(Math.Max(0, s.Remaining), currency)}, {FinanceUi.StatementStatus(s.Status)}");
                strip.Children.Add(frame);
            }
            body.Children.Add(strip);
        }
        return Panel("Cartões", body, "Fatura a pagar (a mais antiga fechada com saldo, ou a aberta), limite livre e as próximas faturas com as parcelas já lançadas.",
            cards.Count > 1 ? AgendaUi.Text($"{s_cardIndex + 1} de {cards.Count}", "CaptionText", 11.5) : Badge("", card.Color));
    }

    /// <summary>Desenho do cartão: nome, situação da fatura, vencimento e limite livre (nunca o número completo).</summary>
    private Border CardVisual(CreditCard card, CardSummary summary, CardStatement? toPay)
    {
        var currency = _finance.BaseCurrency;
        var baseColor = ((SolidColorBrush)AgendaUi.ColorBrush(card.Color)).Color;
        Color Shade(double f) => Color.FromRgb((byte)(baseColor.R * f), (byte)(baseColor.G * f), (byte)(baseColor.B * f));
        var white = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
        var soft = new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0xF5, 0xF5));
        var grid = new Grid { Height = 150 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new DockPanel();
        if (toPay is not null)
        {
            var pill = Pill(FinanceUi.StatementStatus(toPay.Status), white, new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
            DockPanel.SetDock(pill, Dock.Right);
            top.Children.Add(pill);
        }
        var name = AgendaUi.Text(card.Institution is { Length: > 0 } inst ? inst : card.Name, "BodyText", 14, white);
        name.FontWeight = FontWeights.SemiBold;
        top.Children.Add(name);
        grid.Children.Add(top);

        var middle = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var chip = new Border { Width = 34, Height = 25, CornerRadius = new CornerRadius(5), Background = new LinearGradientBrush(Color.FromRgb(0xE3, 0xC7, 0x7B), Color.FromRgb(0xB8, 0x94, 0x3E), 45) };
        DockPanel.SetDock(chip, Dock.Left);
        middle.Children.Add(chip);
        var title = AgendaUi.Text(card.Name.ToUpper(FinanceUi.PtBr) + (card.LastDigits is { } d ? $"  ••{d}" : ""), "CaptionText", 12, soft);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        middle.Children.Add(title);
        Grid.SetRow(middle, 1);
        grid.Children.Add(middle);

        var bottom = new DockPanel();
        var limit = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        limit.Children.Add(AgendaUi.Text("LIMITE LIVRE", "CaptionText", 9.5, soft));
        var free = AgendaUi.Text(FinanceUi.Amount(summary.Available, currency), "BodyText", 13, white);
        free.HorizontalAlignment = HorizontalAlignment.Right;
        limit.Children.Add(free);
        DockPanel.SetDock(limit, Dock.Right);
        bottom.Children.Add(limit);
        var dueBox = new StackPanel();
        dueBox.Children.Add(AgendaUi.Text("VENCE", "CaptionText", 9.5, soft));
        dueBox.Children.Add(AgendaUi.Text(toPay is null ? "—" : toPay.Due.ToString("dd/MM", FinanceUi.PtBr), "BodyText", 13, white));
        bottom.Children.Add(dueBox);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        var visual = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 12),
            Background = new LinearGradientBrush(Shade(0.62), Shade(0.28), new Point(0, 0), new Point(1, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = grid,
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(visual, $"Cartão {card.Name}: limite livre {FinanceUi.Amount(summary.Available, currency)}" + (toPay is null ? "" : $", fatura vence em {FinanceUi.Date(toPay.Due)}"));
        return visual;
    }

    // ---------- Orçamento ----------

    private Border BudgetTile(string monthName)
    {
        var currency = _finance.BaseCurrency;
        var budget = _finance.BudgetMonth(_year, _month);
        var body = new StackPanel();
        if (budget.Lines.Count == 0)
        {
            body.Children.Add(Paragraph("Defina limites por categoria (ex.: alimentação, moradia) e acompanhe aqui quanto já foi gasto, com aviso em 80% e no estouro."));
            var create = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Criar orçamento", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            create.Click += (_, _) => OpenPlanning(PlanningView.Budget);
            body.Children.Add(create);
            return Panel($"Orçamento de {monthName}", body, "Limites de gasto por categoria.", Badge("", "#8FB573"));
        }
        body.Children.Add(AgendaUi.Text("Quanto você já gastou", "CaptionText", 11.5));
        body.Children.Add(BigMoney(budget.TotalSpent, 30, null, $"Gasto no orçamento: {FinanceUi.Amount(budget.TotalSpent, currency)} de {FinanceUi.Amount(budget.TotalLimit, currency)}"));
        var over = budget.TotalSpent > budget.TotalLimit;
        var warnLevel = budget.TotalLimit > 0 && budget.TotalSpent * 100 >= 80 * budget.TotalLimit;
        var status = over ? $"✕ {Percent(budget.TotalSpent, budget.TotalLimit)} do limite · passou {FinanceUi.Amount(-budget.TotalRemaining, currency)}"
            : $"{(warnLevel ? "⚠" : "✓")} {Percent(budget.TotalSpent, budget.TotalLimit)} do limite · restam {FinanceUi.Amount(budget.TotalRemaining, currency)}";
        body.Children.Add(AgendaUi.Text(status, "CaptionText", 12, over ? FinanceUi.Res("DangerBrush") : warnLevel ? FinanceUi.Res("WarningBrush") : FinanceUi.Res("SuccessBrush")));
        var brush = over ? FinanceUi.Res("DangerBrush") : warnLevel ? FinanceUi.Res("WarningBrush") : FinanceUi.Res("SuccessBrush");
        body.Children.Add(Meter.Build(budget.TotalSpent, budget.TotalPlanned, budget.TotalLimit, brush, $"Orçamento total: {Percent(budget.TotalSpent, budget.TotalLimit)}", 12));
        var scale = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var limitLabel = AgendaUi.Text(FinanceUi.Amount(budget.TotalLimit, currency), "CaptionText", 10.5);
        DockPanel.SetDock(limitLabel, Dock.Right);
        scale.Children.Add(limitLabel);
        scale.Children.Add(AgendaUi.Text("0", "CaptionText", 10.5));
        body.Children.Add(scale);

        body.Children.Add(Caps("Por categoria"));
        var grid = new UniformGrid { Columns = 2 };
        foreach (var line in budget.Lines.OrderByDescending(l => l.Percent ?? 0).Take(6))
        {
            var cell = new Grid { Margin = new Thickness(0, 0, 8, 10) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = Badge("", line.Category.Color, 30);
            icon.Margin = new Thickness(0, 0, 8, 0);
            cell.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(AgendaUi.Text(line.Category.Name, "CaptionText", 11.5));
            var amount = AgendaUi.Text(FinanceUi.Amount(line.Spent, currency), "BodyText", 12.5,
                line.State == BudgetState.Over ? FinanceUi.Res("DangerBrush") : line.State == BudgetState.Warning ? FinanceUi.Res("WarningBrush") : null);
            amount.FontWeight = FontWeights.SemiBold;
            text.Children.Add(amount);
            text.Children.Add(AgendaUi.Text($"de {FinanceUi.Amount(line.Limit, currency)} · {Percent(line.Spent, line.Limit)}", "CaptionText", 10.5));
            Grid.SetColumn(text, 1);
            cell.Children.Add(text);
            AutomationProperties.SetName(cell, $"{line.Category.Name}: {FinanceUi.Amount(line.Spent, currency)} de {FinanceUi.Amount(line.Limit, currency)}");
            grid.Children.Add(cell);
        }
        body.Children.Add(grid);
        var open = Chip("Abrir", (_, _) => OpenPlanning(PlanningView.Budget));
        AutomationProperties.SetName(open, "Abrir orçamento");
        return Panel($"Orçamento de {monthName}", body,
            "Gasto do mês (despesas confirmadas e pendentes, menos estornos) × limites por categoria. Faixas: atenção em 80% e estouro em 100%. Parte clara da barra = contas previstas.", open);
    }

    // ---------- Metas ----------

    private UIElement? GoalsRow()
    {
        var currency = _finance.BaseCurrency;
        var goals = _finance.Goals.Where(g => !g.IsArchived).Take(3).ToList();
        if (goals.Count == 0)
        {
            return null;
        }
        var tiles = new List<(UIElement, int, int, int)>();
        for (var i = 0; i < goals.Count; i++)
        {
            var goal = goals[i];
            var p = _finance.Progress(goal);
            var body = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(BigMoney(p.Real, 26, null, $"{goal.Name}: {FinanceUi.Amount(p.Real, currency)} guardados"));
            var pill = p.Reached ? Pill("atingida", FinanceUi.Res("SuccessBrush"))
                : p.Late ? Pill("prazo passou", FinanceUi.Res("WarningBrush"))
                : p.MonthlyNeeded is { } m ? Pill($"{FinanceUi.Amount(m, currency)}/mês", (Brush)FindResource("AccentBrush"))
                : Pill("sem prazo", FinanceUi.Res("TextSecondaryBrush"));
            pill.Margin = new Thickness(10, 0, 0, 4);
            pill.VerticalAlignment = VerticalAlignment.Bottom;
            line.Children.Add(pill);
            body.Children.Add(line);
            body.Children.Add(AgendaUi.Text($"de {FinanceUi.Amount(goal.Target, currency)}" + (goal.Deadline is { } d ? $" até {FinanceUi.Date(d)}" : "") + $" · {Percent(p.Real, goal.Target)}", "CaptionText", 11.5));
            body.Children.Add(Meter.Build(p.Real, p.PlannedAhead, goal.Target, AgendaUi.ColorBrush(goal.Color), $"{goal.Name}: {Percent(p.Real, goal.Target)} guardado", 10));
            var g = goal;
            var open = Chip("Guardar", (_, _) => OpenEditor(new ContributionEditor(_finance, g)));
            AutomationProperties.SetName(open, $"Guardar ou planejar dinheiro para {goal.Name}");
            tiles.Add((Panel($"Meta · {goal.Name}", body, "Barra cheia = guardado de fato; clara = planejado (não entra no Reservado).", open), 0, i, 1));
        }
        return Grid3([.. tiles]);
    }

    // ---------- Gráficos e indicadores ----------

    private void AddChartsAndIndicators(MonthOverview o, DateOnly start, DateOnly end, string monthName, string previousName, bool isPast)
    {
        var currency = _finance.BaseCurrency;
        var accent = (Brush)FindResource("AccentBrush");
        var flows = _finance.MonthlyFlows(_year, _month, 6);
        var days = _finance.DailyBalances(_year, _month);
        FrameworkElement flowContent = s_tables ? FinanceCharts.FlowTable(flows, currency) : FinanceCharts.FlowBars(flows, _year, _month, currency);
        FrameworkElement balanceContent = s_tables ? FinanceCharts.BalanceTable(days, currency) : new BalanceChart(days, currency) { Margin = new Thickness(0, 10, 0, 0) };
        var balancePanel = new StackPanel();
        balancePanel.Children.Add(balanceContent);
        if (!s_tables && days.Count > 0)
        {
            var lowest = days.MinBy(d => d.Balance)!;
            var todayPoint = days.LastOrDefault(d => !d.IsForecast);
            var keyPoints = $"Dia {days[0].Date.Day}: {FinanceUi.Amount(days[0].Balance, currency)}" +
                (todayPoint is not null && todayPoint != days[^1] ? $" · hoje: {FinanceUi.Amount(todayPoint.Balance, currency)}" : "") +
                $" · dia {days[^1].Date.Day}{(days[^1].IsForecast ? " (previsto)" : "")}: {FinanceUi.Amount(days[^1].Balance, currency)}" +
                $" · menor: {FinanceUi.Amount(lowest.Balance, currency)} em {lowest.Date.ToString("dd/MM", FinanceUi.PtBr)}";
            var keyText = Wrap(AgendaUi.Text(keyPoints, "CaptionText", 12, lowest.Balance < 0 ? FinanceUi.Res("WarningBrush") : null));
            keyText.Margin = new Thickness(0, 6, 0, 0);
            balancePanel.Children.Add(keyText);
            var forecastSwatch = accent.Clone();
            forecastSwatch.Opacity = 0.45;
            balancePanel.Children.Add(FinanceCharts.Legend(("Confirmado", accent), ("Previsto (tracejado)", forecastSwatch)));
        }
        ListPanel.Children.Add(Columns(
            (Panel("Receitas × despesas · 6 meses", flowContent, "Receitas e despesas de cada mês, incluindo pendentes. O mês exibido fica destacado."), 1),
            (Panel($"Saldo ao longo de {monthName}", balancePanel,
                "Saldo das contas do disponível ao fim de cada dia: confirmado até hoje; depois, previsto com pendentes, recorrências previstas e faturas no vencimento."), 1)));

        var indicators = new UniformGrid { Columns = _narrow ? 2 : 4, Margin = new Thickness(0, 0, -12, 0) };
        indicators.Children.Add(Indicator("Receitas recebidas", o.Totals.IncomeCleared, o.Totals.IncomePending > 0 ? $"+ {FinanceUi.Amount(o.Totals.IncomePending, currency)} pendentes" : "nada pendente",
            o.HasPreviousMonth ? new Change(o.Totals.Income, o.PreviousTotals.Income) : null, previousName, true,
            "Receitas confirmadas no mês. A comparação usa receitas totais (com pendentes). Recorrências previstas aparecem no saldo previsto."));
        indicators.Children.Add(Indicator("Despesas pagas", o.Totals.ExpenseCleared, o.Totals.ExpensePending > 0 ? $"+ {FinanceUi.Amount(o.Totals.ExpensePending, currency)} pendentes" : "nada pendente",
            o.HasPreviousMonth ? new Change(o.Totals.Expense, o.PreviousTotals.Expense) : null, previousName, false,
            "Despesas confirmadas no mês, menos estornos. A comparação usa despesas totais (com pendentes). Transferências e ajustes não contam."));
        indicators.Children.Add(Indicator("Resultado do mês", o.Totals.Net, "receitas − despesas, com pendentes",
            o.HasPreviousMonth ? new Change(o.Totals.Net, o.PreviousTotals.Net) : null, previousName, true, "Receitas menos despesas do mês, incluindo pendentes.", showPlus: true));
        indicators.Children.Add(Indicator("Patrimônio financeiro", o.NetWorth, isPast ? $"em {FinanceUi.Date(o.Reference)}" : "contas − dívida dos cartões", null, previousName, true,
            "Saldos confirmados de todas as contas ativas na moeda base (incluindo investimentos) menos a dívida dos cartões, contando as parcelas futuras de compras já feitas."));
        if (o.Reserved > 0)
        {
            indicators.Children.Add(Indicator("Reservado em metas", o.Reserved, $"disponível {FinanceUi.Amount(o.Available, currency)}", null, previousName, true,
                "Dinheiro guardado de fato em metas ativas, em contas do disponível. Contribuições planejadas não entram. Disponível = saldo em contas − reservado."));
        }
        if (_finance.Cards.Count > 0)
        {
            indicators.Children.Add(Indicator("Gastos no cartão", o.CardSpending, "compras e parcelas do mês, menos estornos", null, previousName, false,
                "Despesas no cartão pela data de cada parcela (competência). Já estão somadas em \"Despesas\": o pagamento da fatura não é outra despesa."));
            indicators.Children.Add(Indicator("Faturas do mês", o.StatementsDue, "valor devido das faturas que vencem no mês", null, previousName, false,
                "Soma do valor devido (inclui saldo de faturas anteriores) das faturas com vencimento neste mês. É o que sai do caixa, não o que foi gasto."));
        }
        if (!_narrow)
        {
            indicators.Columns = indicators.Children.Count is 3 or 6 or 5 ? 3 : 4;
        }
        ListPanel.Children.Add(indicators);
        _ = start;
        _ = end;
    }
}
