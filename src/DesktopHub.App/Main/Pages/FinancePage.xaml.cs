using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Finanças (Fase 8): resumo do mês, lançamentos com filtros e busca, contas e categorias. Todo número é
/// calculado pelo <see cref="FinanceService"/> a partir dos lançamentos; a tela nunca soma valores por conta própria
/// além do total da lista filtrada.
/// </summary>
public partial class FinancePage : UserControl, IDisposable
{
    private enum Tab
    {
        Overview,
        Transactions,
        Accounts,
        Categories,
    }

    private const int PageSize = 300;
    private static Tab s_lastTab = Tab.Overview;
    private static bool s_tables;
    private static (int Year, int Month)? s_lastMonth;

    private readonly FinanceService _finance;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private Tab _tab = s_lastTab;
    private int _year;
    private int _month;
    private bool _allTime;
    private bool _showArchived;
    private int _limit = PageSize;
    private DeletedTransaction? _undo;
    private bool _ready;

    internal FinancePage(FinanceService finance, IEventBus bus)
    {
        _finance = finance;
        InitializeComponent();
        var today = finance.IsAvailable ? finance.Today : DateOnly.FromDateTime(DateTime.Today);
        (_year, _month) = s_lastMonth ?? (today.Year, today.Month);
        PrivacyButton.IsChecked = FinanceUi.Privacy;

        if (!finance.IsAvailable)
        {
            ShowUnavailable();
            return;
        }

        SearchBox.TextChanged += (_, _) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<Choice<TransactionKind?>> kinds = [new("Todos os tipos", null), .. Enum.GetValues<TransactionKind>().Select(k => new Choice<TransactionKind?>(FinanceUi.Kind(k), k))];
        KindFilter.ItemsSource = kinds;
        KindFilter.SelectedIndex = 0;
        StatusFilter.ItemsSource = new List<Choice<TransactionStatus?>> { new("Qualquer situação", null), new("Confirmados", TransactionStatus.Cleared), new("Pendentes", TransactionStatus.Pending) };
        StatusFilter.SelectedIndex = 0;
        FillFilterChoices();

        _undoTimer.Tick += (_, _) => HideUndo();
        _subscriptions.Add(bus.Subscribe<FinanceChanged>(_ =>
        {
            FillFilterChoices();
            Render();
        }, SynchronizationContext.Current));
        FinanceUi.PrivacyChanged += OnPrivacyChanged;
        PreviewKeyDown += OnKeyDown;
        _ready = true;
        Render();
    }

    private void ShowUnavailable()
    {
        FilterBar.Visibility = Visibility.Collapsed;
        NewButton.IsEnabled = false;
        foreach (var b in new UIElement[] { PrevMonth, NextMonth, TodayButton, TabTransactions, TabAccounts, TabCategories })
        {
            b.IsEnabled = false;
        }
        MonthText.Text = "";
        ListPanel.Children.Add(Paragraph("O módulo de finanças não pôde ser aberto.", "SubtitleText"));
        ListPanel.Children.Add(Paragraph(_finance.UnavailableReason ?? "Motivo desconhecido."));
    }

    private void OnPrivacyChanged()
    {
        PrivacyButton.IsChecked = FinanceUi.Privacy;
        Render();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            FinanceUi.Privacy = !FinanceUi.Privacy;
            e.Handled = true;
        }
    }

    private void FillFilterChoices()
    {
        var account = (AccountFilter.SelectedItem as Choice<Guid?>)?.Value;
        var category = (CategoryFilter.SelectedItem as Choice<Guid?>)?.Value;
        _ready = false;
        List<Choice<Guid?>> accounts = [new("Todas as contas", null), .. _finance.Accounts.Select(a => new Choice<Guid?>(a.Name + (a.IsArchived ? " (arquivada)" : ""), a.Id))];
        AccountFilter.ItemsSource = accounts;
        AccountFilter.SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Value == account));
        List<Choice<Guid?>> categories =
        [
            new("Todas as categorias", null),
            .. _finance.OrderedCategories(CategoryKind.Expense, includeArchived: true).Select(c => new Choice<Guid?>((c.ParentId is null ? "" : "      ") + c.Name, c.Id)),
            .. _finance.OrderedCategories(CategoryKind.Income, includeArchived: true).Select(c => new Choice<Guid?>((c.ParentId is null ? "↑ " : "      ") + c.Name, c.Id)),
        ];
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedIndex = Math.Max(0, categories.FindIndex(c => c.Value == category));
        _ready = true;
    }

    // ---------- Renderização ----------

    private void Render()
    {
        if (!_finance.IsAvailable)
        {
            return;
        }
        s_lastTab = _tab;
        s_lastMonth = (_year, _month);
        foreach (var (button, tab) in new[] { (TabOverview, Tab.Overview), (TabTransactions, Tab.Transactions), (TabAccounts, Tab.Accounts), (TabCategories, Tab.Categories) })
        {
            button.DataContext = new ChipState(tab == _tab);
        }
        // A visão geral tem seus próprios cartões: sem o resumo de cima e sem a moldura da lista.
        var overview = _tab == Tab.Overview;
        SummaryGrid.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        ListCard.Background = overview ? Brushes.Transparent : FinanceUi.Res("SurfaceBrush");
        ListCard.BorderThickness = new Thickness(overview ? 0 : 1);
        ListPanel.Margin = overview ? new Thickness(0, 0, 4, 8) : new Thickness(16, 8, 16, 16);
        AllTimeChip.DataContext = new ChipState(_allTime);
        PrivacyButton.Content = FinanceUi.Privacy ? "" : "";
        MonthText.Text = FinanceUi.MonthTitle(_year, _month);
        var today = _finance.Today;
        TodayButton.Visibility = today.Year == _year && today.Month == _month ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.Visibility = _tab == Tab.Transactions ? Visibility.Visible : Visibility.Collapsed;
        RenderSummary();

        ListPanel.Children.Clear();
        FooterText.Text = "";
        switch (_tab)
        {
            case Tab.Accounts:
                RenderAccounts();
                break;
            case Tab.Categories:
                RenderCategories();
                break;
            case Tab.Overview:
                RenderOverview();
                break;
            default:
                RenderTransactions();
                break;
        }
    }

    private bool _narrow;

    private void ListScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 760;
        if (narrow != _narrow)
        {
            _narrow = narrow;
            if (_ready && _tab == Tab.Overview)
            {
                Render();
            }
        }
    }

    // ---------- Visão geral ----------

    private static Border Panel(string title, UIElement content, string? help = null, UIElement? headerRight = null)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            head.Children.Add(headerRight);
        }
        head.Children.Add(AgendaUi.Text(title, "SubtitleText", 14));
        stack.Children.Add(head);
        stack.Children.Add(content);
        var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 12, 12), Child = stack };
        if (help is not null)
        {
            card.ToolTip = help;
            AutomationProperties.SetHelpText(card, help);
        }
        AutomationProperties.SetName(card, title);
        return card;
    }

    /// <summary>Linha de cartões lado a lado (pesos), ou empilhados em larguras pequenas.</summary>
    private Grid Columns(params (UIElement Element, double Weight)[] items)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, -12, 0) };
        for (var i = 0; i < items.Length; i++)
        {
            if (_narrow)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(items[i].Element, i);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(items[i].Weight, GridUnitType.Star) });
                Grid.SetColumn(items[i].Element, i);
            }
            grid.Children.Add(items[i].Element);
        }
        return grid;
    }

    private Button TablesToggle()
    {
        var chip = Chip(s_tables ? "Ver gráficos" : "Ver como tabela", (_, _) =>
        {
            s_tables = !s_tables;
            Render();
        });
        AutomationProperties.SetName(chip, s_tables ? "Mostrar gráficos" : "Mostrar gráficos como tabelas");
        return chip;
    }

    private void RenderOverview()
    {
        if (_finance.Accounts.Count == 0)
        {
            ListCard.Background = FinanceUi.Res("SurfaceBrush");
            ListPanel.Margin = new Thickness(16, 8, 16, 16);
            RenderTransactions(); // estado vazio: convite para cadastrar a primeira conta
            return;
        }
        var currency = _finance.BaseCurrency;
        var o = _finance.Overview(_year, _month);
        var (start, end) = FinanceService.MonthRange(_year, _month);
        var monthName = FinanceUi.MonthTitle(_year, _month).Split(' ')[0].ToLower(FinanceUi.PtBr);
        var previousName = new DateTime(_year, _month, 1).AddMonths(-1).ToString("MMMM", FinanceUi.PtBr);
        var isPast = o.Reference == end && end < _finance.Today;

        // 1. Saldo previsto com a decomposição (a ideia central da referência).
        var hero = new StackPanel();
        hero.Children.Add(AgendaUi.Text(isPast ? $"Saldo no fim de {monthName}" : $"no fim do mês · {FinanceUi.Date(end)}", "CaptionText"));
        var big = AgendaUi.Text(FinanceUi.Amount(o.Forecast, currency), "TitleText", 32, o.Forecast < 0 ? FinanceUi.Res("WarningBrush") : null);
        big.Margin = new Thickness(0, 2, 0, 0);
        hero.Children.Add(big);
        hero.Children.Add(o.HasPreviousMonth ? DeltaText(new Change(o.Forecast, o.PreviousForecast), $"vs. fim de {previousName}", higherIsGood: true) : AgendaUi.Text($"sem dados de {previousName} para comparar", "CaptionText", 11.5));
        var accent = (Brush)FindResource("AccentBrush");
        var receive = FinanceUi.Res("SuccessBrush");
        var pay = FinanceUi.Res("DangerBrush");
        var other = FinanceUi.Res("TextTertiaryBrush");
        var todayLabel = isPast ? "Saldo confirmado" : $"Saldo hoje ({FinanceUi.Date(o.Reference)})";
        hero.Children.Add(FinanceCharts.SegmentBar(
        [
            (Math.Max(0, o.Forecast), accent, $"Previsto {FinanceUi.Amount(o.Forecast, currency)}"),
            (o.ToPay, pay, $"A pagar {FinanceUi.Amount(o.ToPay, currency)}"),
            (Math.Abs(o.OtherChanges), other, $"Outros {FinanceUi.Amount(o.OtherChanges, currency, showPlus: true)}"),
        ]));
        var equation = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Term(string label, string value, Brush brush)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 20, 4) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            head.Children.Add(AgendaUi.Text(label, "CaptionText", 11.5));
            item.Children.Add(head);
            item.Children.Add(AgendaUi.Text(value, "BodyText", 13.5));
            AutomationProperties.SetName(item, $"{label}: {value}");
            equation.Children.Add(item);
        }
        Term(todayLabel, FinanceUi.Amount(o.AccountsBalance, currency), FinanceUi.Res("TextSecondaryBrush"));
        Term("+ a receber", FinanceUi.Amount(o.ToReceive, currency), receive);
        Term("− a pagar", FinanceUi.Amount(o.ToPay, currency), pay);
        if (o.OtherChanges != 0)
        {
            Term("± transferências e ajustes", FinanceUi.Amount(o.OtherChanges, currency, showPlus: true), other);
        }
        Term("= previsto", FinanceUi.Amount(o.Forecast, currency), accent);
        hero.Children.Add(equation);
        if (o.OverduePending > 0)
        {
            var warn = AgendaUi.Text($"⚠ {o.OverduePending} {(o.OverduePending == 1 ? "lançamento pendente atrasado" : "lançamentos pendentes atrasados")}: confirme ou corrija a data (já estão no previsto).", "CaptionText", 12, FinanceUi.Res("WarningBrush"));
            warn.TextWrapping = TextWrapping.Wrap;
            warn.TextTrimming = TextTrimming.None;
            warn.Margin = new Thickness(0, 6, 0, 0);
            hero.Children.Add(warn);
        }
        var heroCard = Panel("Saldo previsto", hero,
            "Saldo previsto = saldo confirmado das contas do disponível + receitas ainda por entrar − despesas ainda por sair (pendentes ou com data futura) " +
            "± transferências com contas fora do disponível e ajustes, até o fim do mês. Investimentos, contas arquivadas e outras moedas não entram.");

        // Pendentes até o fim do mês (atrasados primeiro).
        var pending = _finance.PendingUntil(end).Where(t => t.Date >= start || t.Date < _finance.Today).Take(8).ToList();
        var pendingPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (pending.Count == 0)
        {
            pendingPanel.Children.Add(Paragraph("Nenhum lançamento pendente até o fim do mês. Lançamentos marcados como \"pendente\" aparecem aqui."));
        }
        foreach (var t in pending)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var signed = FinanceUi.SignedFor(t, null);
            var value = AgendaUi.Text(t.Kind == TransactionKind.Transfer ? "⇄ " + FinanceUi.Amount(t.Amount, currency) : FinanceUi.Amount(signed, currency, showPlus: true), "BodyText", 13, FinanceUi.AmountBrush(signed));
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            var overdue = t.Date < _finance.Today;
            var date = AgendaUi.Text(t.Date.ToString("dd/MM", FinanceUi.PtBr), "CaptionText", 12, overdue ? FinanceUi.Res("WarningBrush") : null);
            date.Width = 44;
            row.Children.Add(date);
            row.Children.Add(AgendaUi.Text(t.Description + (overdue ? " (atrasado)" : ""), "BodyText", 13));
            row.Cursor = Cursors.Hand;
            row.Background = Brushes.Transparent;
            var item = t;
            row.MouseLeftButtonUp += (_, _) => OpenEditor(new TransactionEditor(_finance, FinanceService.DraftFrom(item), isNew: false));
            AutomationProperties.SetName(row, $"{t.Description}, {FinanceUi.Date(t.Date)}, {(overdue ? "atrasado, " : "")}{FinanceUi.Amount(signed, currency, showPlus: true)}");
            pendingPanel.Children.Add(row);
        }
        var pendingCard = Panel("Pendentes até o fim do mês", pendingPanel, "Lançamentos marcados como pendentes com data até o fim do mês, incluindo os atrasados.");
        ListPanel.Children.Add(Columns((heroCard, 3), (pendingCard, 2)));

        // 2. Indicadores com comparação ao mês anterior.
        var indicators = new UniformGrid { Columns = _narrow ? 2 : 4, Margin = new Thickness(0, 0, -12, 0) };
        indicators.Children.Add(Indicator("Receitas recebidas", o.Totals.IncomeCleared, o.Totals.IncomePending > 0 ? $"+ {FinanceUi.Amount(o.Totals.IncomePending, currency)} previstas" : "nada previsto",
            o.HasPreviousMonth ? new Change(o.Totals.Income, o.PreviousTotals.Income) : null, previousName, true,
            "Receitas confirmadas no mês. Previstas = pendentes. A comparação usa receitas totais (com pendentes)."));
        indicators.Children.Add(Indicator("Despesas pagas", o.Totals.ExpenseCleared, o.Totals.ExpensePending > 0 ? $"+ {FinanceUi.Amount(o.Totals.ExpensePending, currency)} pendentes" : "nada pendente",
            o.HasPreviousMonth ? new Change(o.Totals.Expense, o.PreviousTotals.Expense) : null, previousName, false,
            "Despesas confirmadas no mês, menos estornos. A comparação usa despesas totais (com pendentes). Transferências e ajustes não contam."));
        indicators.Children.Add(Indicator("Resultado do mês", o.Totals.Net, "receitas − despesas, com pendentes",
            o.HasPreviousMonth ? new Change(o.Totals.Net, o.PreviousTotals.Net) : null, previousName, true, "Receitas menos despesas do mês, incluindo pendentes.", showPlus: true));
        indicators.Children.Add(Indicator("Patrimônio financeiro", o.NetWorth, isPast ? $"em {FinanceUi.Date(o.Reference)}" : "confirmado até hoje", null, previousName, true,
            "Soma dos saldos confirmados de todas as contas ativas na moeda base, incluindo investimentos. Cartões de crédito entram como dívida a partir da Fase 10."));
        ListPanel.Children.Add(indicators);

        // 3. Gráficos.
        var flows = _finance.MonthlyFlows(_year, _month, 6);
        var spending = _finance.SpendingByCategory(start, end);
        var days = _finance.DailyBalances(_year, _month);
        FrameworkElement flowContent = s_tables ? FinanceCharts.FlowTable(flows, currency) : FinanceCharts.FlowBars(flows, _year, _month, currency);
        FrameworkElement spendContent = spending.Count == 0
            ? Paragraph($"Nenhuma despesa em {monthName}.")
            : s_tables ? FinanceCharts.CategoryTable(spending, currency) : FinanceCharts.CategoryBars(spending, currency);
        ListPanel.Children.Add(Columns(
            (Panel("Receitas × despesas · 6 meses", flowContent, "Receitas e despesas de cada mês, incluindo pendentes. O mês exibido fica destacado.", TablesToggle()), 1),
            (Panel($"Gastos por categoria · {monthName}", spendContent, "Despesas menos estornos, somando as subcategorias em cada categoria principal. Inclui pendentes."), 1)));

        FrameworkElement balanceContent = s_tables ? FinanceCharts.BalanceTable(days, currency) : new BalanceChart(days, currency) { Margin = new Thickness(0, 10, 0, 0) };
        var balancePanel = new StackPanel();
        balancePanel.Children.Add(balanceContent);
        if (!s_tables && days.Count > 0)
        {
            // O gráfico não tem eixo de valores: os pontos que importam vão em texto.
            var lowest = days.MinBy(d => d.Balance)!;
            var todayPoint = days.LastOrDefault(d => !d.IsForecast);
            var keyPoints = $"Dia {days[0].Date.Day}: {FinanceUi.Amount(days[0].Balance, currency)}" +
                (todayPoint is not null && todayPoint != days[^1] ? $" · hoje: {FinanceUi.Amount(todayPoint.Balance, currency)}" : "") +
                $" · dia {days[^1].Date.Day}{(days[^1].IsForecast ? " (previsto)" : "")}: {FinanceUi.Amount(days[^1].Balance, currency)}" +
                $" · menor: {FinanceUi.Amount(lowest.Balance, currency)} em {lowest.Date.ToString("dd/MM", FinanceUi.PtBr)}";
            var keyText = AgendaUi.Text(keyPoints, "CaptionText", 12, lowest.Balance < 0 ? FinanceUi.Res("WarningBrush") : null);
            keyText.TextWrapping = TextWrapping.Wrap;
            keyText.TextTrimming = TextTrimming.None;
            keyText.Margin = new Thickness(0, 6, 0, 0);
            balancePanel.Children.Add(keyText);
            var forecastSwatch = accent.Clone();
            forecastSwatch.Opacity = 0.45;
            balancePanel.Children.Add(FinanceCharts.Legend(("Confirmado", accent), ("Previsto (tracejado)", forecastSwatch)));
        }
        ListPanel.Children.Add(Columns((Panel($"Saldo disponível ao longo de {monthName}", balancePanel,
            "Saldo das contas do disponível ao fim de cada dia: confirmado até hoje; depois, previsto com os pendentes."), 1)));
        FooterText.Text = "Todos os valores são calculados dos seus lançamentos. Passe o mouse sobre um cartão para ver a definição.";
    }

    private Border Indicator(string title, long value, string detail, Change? change, string previousName, bool higherIsGood, string help, bool showPlus = false)
    {
        var panel = new StackPanel();
        panel.Children.Add(AgendaUi.Text(title, "CaptionText"));
        var big = AgendaUi.Text(FinanceUi.Amount(value, _finance.BaseCurrency, showPlus), "TitleText", 20);
        big.Margin = new Thickness(0, 4, 0, 2);
        panel.Children.Add(big);
        panel.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5));
        if (change is not null)
        {
            panel.Children.Add(DeltaText(change, $"vs. {previousName}", higherIsGood));
        }
        var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 12, 12), Child = panel, ToolTip = help };
        AutomationProperties.SetName(card, $"{title}: {FinanceUi.Amount(value, _finance.BaseCurrency, showPlus)}, {detail}");
        AutomationProperties.SetHelpText(card, help);
        return card;
    }

    /// <summary>"▲ 12% (+R$ 300,00) vs. outubro": seta e sinal sempre; a cor só reforça (bom/ruim depende do indicador).</summary>
    private TextBlock DeltaText(Change change, string suffix, bool higherIsGood)
    {
        string text;
        Brush? brush = null;
        if (change.Delta == 0)
        {
            text = $"= igual {suffix}";
        }
        else
        {
            var up = change.Delta > 0;
            var pct = change.Percent is { } p ? $"{Math.Abs(p).ToString("0", FinanceUi.PtBr)}% " : "";
            text = $"{(up ? "▲" : "▼")} {pct}({FinanceUi.Amount(change.Delta, _finance.BaseCurrency, showPlus: true)}) {suffix}";
            brush = up == higherIsGood ? FinanceUi.Res("SuccessBrush") : FinanceUi.Res("WarningBrush");
        }
        var block = AgendaUi.Text(text, "CaptionText", 11.5, brush);
        block.Margin = new Thickness(0, 4, 0, 0);
        return block;
    }

    private void RenderSummary()
    {
        SummaryGrid.Children.Clear();
        var (from, to) = FinanceService.MonthRange(_year, _month);
        var totals = _finance.Totals(from, to);
        var month = FinanceUi.MonthTitle(_year, _month).Split(' ')[0].ToLower(FinanceUi.PtBr);
        SummaryGrid.Children.Add(SummaryCard("Saldo em contas", FinanceUi.Amount(_finance.AccountsBalance()),
            $"patrimônio {FinanceUi.Amount(_finance.NetWorth())}",
            "Soma dos saldos confirmados até hoje das contas que entram no disponível (sem investimentos, contas arquivadas e outras moedas). Patrimônio inclui investimentos."));
        SummaryGrid.Children.Add(SummaryCard($"Receitas · {month}", FinanceUi.Amount(totals.IncomeCleared),
            totals.IncomePending > 0 ? $"+ {FinanceUi.Amount(totals.IncomePending)} a receber" : "nada pendente",
            "Receitas confirmadas no mês. Pendentes aparecem à parte. Transferências e ajustes não contam."));
        SummaryGrid.Children.Add(SummaryCard($"Despesas · {month}", FinanceUi.Amount(totals.ExpenseCleared),
            totals.ExpensePending > 0 ? $"+ {FinanceUi.Amount(totals.ExpensePending)} a pagar" : "nada pendente",
            "Despesas confirmadas no mês, já descontados os estornos. Pendentes aparecem à parte. Transferências e ajustes não contam."));
        SummaryGrid.Children.Add(SummaryCard("Resultado do mês", FinanceUi.Amount(totals.Net, showPlus: true),
            "receitas − despesas, com pendentes",
            "Receitas menos despesas do mês, incluindo as pendentes.", totals.Net >= 0 ? null : FinanceUi.Res("WarningBrush")));
    }

    private static Border SummaryCard(string title, string value, string detail, string definition, Brush? valueBrush = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(AgendaUi.Text(title, "CaptionText"));
        var big = AgendaUi.Text(value, "TitleText", 21, valueBrush);
        big.Margin = new Thickness(0, 4, 0, 2);
        panel.Children.Add(big);
        panel.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5));
        var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10), Child = panel, ToolTip = definition };
        AutomationProperties.SetName(card, $"{title}: {value}, {detail}");
        AutomationProperties.SetHelpText(card, definition);
        return card;
    }

    private void Summary_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SummaryGrid.Columns = e.NewSize.Width < 760 ? 2 : 4;

    private static TextBlock Paragraph(string text, string style = "CaptionText")
    {
        var block = AgendaUi.Text(text, style);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        block.Margin = new Thickness(0, 8, 0, 0);
        return block;
    }

    private Grid SectionHeader(string title, params Button[] actions)
    {
        var grid = new Grid { Margin = new Thickness(0, ListPanel.Children.Count == 0 ? 8 : 20, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AgendaUi.Text(title, "SubtitleText", 14));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in actions)
        {
            buttons.Children.Add(b);
        }
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private static Button Chip(string text, RoutedEventHandler click, bool selected = false)
    {
        var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = text, DataContext = new ChipState(selected) };
        chip.Click += click;
        return chip;
    }

    /// <summary>Linha com ações que aparecem ao passar o mouse ou com foco do teclado (continuam acessíveis por Tab).</summary>
    private static Grid ActionRow(UIElement icon, UIElement body, UIElement? right, StackPanel actions, string name, Action open)
    {
        var row = new Grid { Margin = new Thickness(-6, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        if (right is not null)
        {
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
        }
        actions.Orientation = Orientation.Horizontal;
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions.Opacity = 0;
        actions.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(actions, 3);
        row.Children.Add(actions);
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = row.IsKeyboardFocusWithin ? 1 : 0;
        row.IsKeyboardFocusWithinChanged += (_, e) => actions.Opacity = (bool)e.NewValue || row.IsMouseOver ? 1 : 0;
        row.MouseLeftButtonUp += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, row); node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase)
                {
                    return;
                }
            }
            open();
        };
        AutomationProperties.SetName(row, name);
        return row;
    }

    private static Border IconCircle(string glyph, string? color)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 13,
            Foreground = AgendaUi.ColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = AgendaUi.ColorBrush(color, 40),
            Margin = new Thickness(6, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
        };
    }

    // ---------- Lançamentos ----------

    private TransactionFilter CurrentFilter()
    {
        var (from, to) = FinanceService.MonthRange(_year, _month);
        long? min = Money.TryParse(MinBox.Text, out var mn) ? Math.Abs(mn) : null;
        long? max = Money.TryParse(MaxBox.Text, out var mx) ? Math.Abs(mx) : null;
        return new TransactionFilter
        {
            From = _allTime ? null : from,
            To = _allTime ? null : to,
            AccountId = (AccountFilter.SelectedItem as Choice<Guid?>)?.Value,
            CategoryId = (CategoryFilter.SelectedItem as Choice<Guid?>)?.Value,
            Kind = (KindFilter.SelectedItem as Choice<TransactionKind?>)?.Value,
            Status = (StatusFilter.SelectedItem as Choice<TransactionStatus?>)?.Value,
            MinAmount = min,
            MaxAmount = max,
            Text = SearchBox.Text,
        };
    }

    private void RenderTransactions()
    {
        if (_finance.Accounts.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Comece cadastrando uma conta", "SubtitleText"));
            ListPanel.Children.Add(Paragraph("Conta corrente, poupança, carteira digital ou dinheiro. O saldo de cada uma é calculado a partir do saldo inicial e dos lançamentos."));
            var add = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Cadastrar conta", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) => OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
            ListPanel.Children.Add(add);
            return;
        }

        var filter = CurrentFilter();
        var items = _finance.Query(filter);
        var today = _finance.Today;
        long inflow = 0, outflow = 0;
        foreach (var t in items)
        {
            var signed = FinanceUi.SignedFor(t, filter.AccountId);
            if (signed > 0)
            {
                inflow += signed;
            }
            else
            {
                outflow -= signed;
            }
        }
        FooterText.Text = items.Count == 0 ? "" :
            $"{items.Count} {(items.Count == 1 ? "lançamento" : "lançamentos")} · entradas {FinanceUi.Amount(inflow)} · saídas {FinanceUi.Amount(outflow)}" +
            (filter.AccountId is null ? " · transferências entre contas não entram nos totais" : "");

        if (items.Count == 0)
        {
            var filtered = filter.AccountId is not null || filter.CategoryId is not null || filter.Kind is not null || filter.Status is not null || !string.IsNullOrWhiteSpace(filter.Text) || filter.MinAmount is not null || filter.MaxAmount is not null;
            ListPanel.Children.Add(Paragraph(filtered ? "Nenhum lançamento com esses filtros." : $"Nenhum lançamento em {FinanceUi.MonthTitle(_year, _month).ToLower(FinanceUi.PtBr)}. Use \"+ Lançamento\" para registrar."));
            return;
        }

        foreach (var day in items.Take(_limit).GroupBy(t => t.Date))
        {
            var header = new DockPanel { Margin = new Thickness(0, ListPanel.Children.Count == 0 ? 6 : 16, 0, 4) };
            var net = day.Sum(t => FinanceUi.SignedFor(t, filter.AccountId));
            var netText = AgendaUi.Text(net == 0 ? "" : FinanceUi.Amount(net, showPlus: true), "CaptionText");
            DockPanel.SetDock(netText, Dock.Right);
            header.Children.Add(netText);
            header.Children.Add(AgendaUi.Text(FinanceUi.DayTitle(day.Key, today), "SubtitleText", 13));
            ListPanel.Children.Add(header);
            foreach (var t in day)
            {
                ListPanel.Children.Add(TransactionRow(t, filter.AccountId));
            }
        }
        if (items.Count > _limit)
        {
            var more = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = $"Mostrar mais ({items.Count - _limit} restantes)", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            more.Click += (_, _) =>
            {
                _limit += PageSize;
                Render();
            };
            ListPanel.Children.Add(more);
        }
    }

    private Grid TransactionRow(FinanceTransaction t, Guid? accountFilter)
    {
        var category = _finance.Category(t.CategoryId);
        var icon = IconCircle(FinanceUi.Glyph(t.Kind), t.Kind == TransactionKind.Transfer ? "#8A8A8A" : category?.Color ?? "#8A8A8A");

        var body = new StackPanel { Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(AgendaUi.Text(t.Description, "BodyText"));
        var meta = new WrapPanel();
        void Meta(string value, Brush? brush = null)
        {
            var block = AgendaUi.Text(value, "CaptionText", 11.5, brush);
            block.Margin = new Thickness(0, 1, 12, 0);
            meta.Children.Add(block);
        }
        var account = _finance.Account(t.AccountId);
        if (t.Kind == TransactionKind.Transfer)
        {
            Meta($"{account?.Name} → {_finance.Account(t.ToAccountId)?.Name}");
        }
        else
        {
            Meta(t.Kind is TransactionKind.Adjustment ? "Ajuste de saldo" : _finance.CategoryPath(t.CategoryId));
            Meta(account?.Name ?? "?");
        }
        if (t.Kind == TransactionKind.Refund)
        {
            Meta("estorno");
        }
        if (t.Status == TransactionStatus.Pending)
        {
            Meta("pendente", FinanceUi.Res("WarningBrush"));
        }
        if (t.PaymentMethod is not PaymentMethod.None)
        {
            Meta(FinanceUi.Method(t.PaymentMethod));
        }
        foreach (var tag in t.Tags)
        {
            Meta("#" + tag);
        }
        body.Children.Add(meta);

        var signed = FinanceUi.SignedFor(t, accountFilter);
        var currency = account?.Currency ?? _finance.BaseCurrency;
        var amountText = t.Kind == TransactionKind.Transfer && accountFilter is null
            ? "⇄ " + FinanceUi.Amount(t.Amount, currency)
            : FinanceUi.Amount(signed, currency, showPlus: true);
        var amount = AgendaUi.Text(amountText, "BodyText", 14, t.Kind == TransactionKind.Transfer && accountFilter is null ? FinanceUi.Res("TextSecondaryBrush") : FinanceUi.AmountBrush(signed));
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.FontWeight = FontWeights.SemiBold;
        if (t.Status == TransactionStatus.Pending)
        {
            amount.Opacity = 0.75;
        }

        var actions = new StackPanel();
        actions.Children.Add(AgendaUi.IconButton("", $"Duplicar {t.Description}", (_, _) => OpenEditor(new TransactionEditor(_finance, _finance.Duplicate(t.Id), isNew: true))));
        if (t.Kind == TransactionKind.Expense)
        {
            actions.Children.Add(AgendaUi.IconButton("", $"Registrar estorno de {t.Description}", (_, _) => OpenEditor(new TransactionEditor(_finance, new TransactionDraft
            {
                Kind = TransactionKind.Refund,
                Date = _finance.Today,
                Description = "Estorno: " + t.Description,
                AccountId = t.AccountId,
                CategoryId = t.CategoryId,
                RefundOfId = t.Id,
            }, isNew: true))));
        }
        actions.Children.Add(AgendaUi.IconButton("", $"Excluir {t.Description}", (_, _) => DeleteWithUndo(t.Id)));

        var name = $"{FinanceUi.Kind(t.Kind)}: {t.Description}, {amountText}{(t.Status == TransactionStatus.Pending ? ", pendente" : "")}";
        return ActionRow(icon, body, amount, actions, name, () => OpenEditor(new TransactionEditor(_finance, FinanceService.DraftFrom(t), isNew: false)));
    }

    private void DeleteWithUndo(Guid id)
    {
        try
        {
            ShowUndo(_finance.Delete(id));
        }
        catch (FinanceValidationException)
        {
            Render();
        }
    }

    private void ShowUndo(DeletedTransaction removed)
    {
        _undo = removed;
        UndoText.Text = $"\"{removed.Transaction.Description}\" excluído.";
        UndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        _undo = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo is { } value)
        {
            try
            {
                _finance.Restore(value);
            }
            catch (FinanceValidationException ex)
            {
                UndoText.Text = ex.Message;
                return;
            }
        }
        HideUndo();
    }

    // ---------- Contas ----------

    private void RenderAccounts()
    {
        var add = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Nova conta" };
        add.Click += (_, _) => OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
        var accounts = _finance.Accounts;
        var archivedCount = accounts.Count(a => a.IsArchived);
        var buttons = archivedCount > 0
            ? new[] { Chip($"Mostrar arquivadas ({archivedCount})", (_, _) => { _showArchived = !_showArchived; Render(); }, _showArchived), add }
            : [add];
        ListPanel.Children.Add(SectionHeader("Contas e carteiras", buttons));
        if (accounts.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Nenhuma conta ainda. Cadastre a conta corrente, a poupança, a carteira digital ou o dinheiro em espécie."));
            return;
        }
        var today = _finance.Today;
        var balances = _finance.CurrentBalances();
        foreach (var a in accounts.Where(a => _showArchived || !a.IsArchived))
        {
            var balance = balances[a.Id];
            var forecast = _finance.Balance(a.Id, today, includePending: true);
            var body = new StackPanel { Margin = new Thickness(0, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(AgendaUi.Text(a.Name + (a.IsArchived ? " (arquivada)" : ""), "BodyText", foreground: a.IsArchived ? FinanceUi.Res("TextTertiaryBrush") : null));
            var detail = FinanceUi.Kind(a.Kind) + (a.Institution is { } inst ? $" · {inst}" : "") + (a.Currency != _finance.BaseCurrency ? $" · {a.Currency}" : "") +
                         (a.IncludeInAvailable ? "" : " · fora do disponível");
            body.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5));

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var value = AgendaUi.Text(FinanceUi.Amount(balance, a.Currency), "BodyText", 15, balance < 0 ? FinanceUi.Res("WarningBrush") : null);
            value.FontWeight = FontWeights.SemiBold;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(value);
            if (forecast != balance)
            {
                var f = AgendaUi.Text($"com pendentes: {FinanceUi.Amount(forecast, a.Currency)}", "CaptionText", 11.5);
                f.HorizontalAlignment = HorizontalAlignment.Right;
                right.Children.Add(f);
            }

            var actions = new StackPanel();
            actions.Children.Add(AgendaUi.IconButton("", $"Ajustar saldo de {a.Name}", (_, _) => OpenEditor(new AdjustBalanceEditor(_finance, a))));
            actions.Children.Add(AgendaUi.IconButton("", $"Ver lançamentos de {a.Name}", (_, _) => ShowAccountTransactions(a.Id)));
            actions.Children.Add(AgendaUi.IconButton(a.IsArchived ? "" : "", a.IsArchived ? $"Reativar {a.Name}" : $"Arquivar {a.Name}", (_, _) => _finance.SetArchived(a.Id, !a.IsArchived)));
            if (_finance.CanDeleteAccount(a.Id))
            {
                actions.Children.Add(AgendaUi.IconButton("", $"Excluir {a.Name}", (_, _) => _finance.DeleteAccount(a.Id)));
            }
            ListPanel.Children.Add(ActionRow(IconCircle(FinanceUi.Glyph(a.Kind), a.Color), body, right, actions,
                $"{a.Name}: saldo {FinanceUi.Amount(balance, a.Currency)}", () => OpenEditor(new AccountEditor(_finance, a, isNew: false))));
        }
        FooterText.Text = $"Saldo em contas {FinanceUi.Amount(_finance.AccountsBalance())} · patrimônio {FinanceUi.Amount(_finance.NetWorth())} " +
                          "· saldos confirmados até hoje; contas em outra moeda não entram nos totais";
    }

    private void ShowAccountTransactions(Guid accountId)
    {
        _tab = Tab.Transactions;
        var accounts = (List<Choice<Guid?>>)AccountFilter.ItemsSource;
        _ready = false;
        AccountFilter.SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Value == accountId));
        _ready = true;
        Render();
    }

    // ---------- Categorias ----------

    private void RenderCategories()
    {
        var archivedCount = _finance.Categories.Count(c => c.IsArchived);
        var addExpense = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Categoria" };
        addExpense.Click += (_, _) => OpenEditor(new CategoryEditor(_finance, _finance.NewCategory(CategoryKind.Expense), isNew: true));
        var header = archivedCount > 0
            ? new[] { Chip($"Mostrar arquivadas ({archivedCount})", (_, _) => { _showArchived = !_showArchived; Render(); }, _showArchived), addExpense }
            : [addExpense];
        ListPanel.Children.Add(SectionHeader("Categorias de despesa", header));
        RenderCategoryKind(CategoryKind.Expense);
        ListPanel.Children.Add(SectionHeader("Categorias de receita"));
        RenderCategoryKind(CategoryKind.Income);
        FooterText.Text = "Categorias em uso não podem ser excluídas: arquive para escondê-las sem perder o histórico.";
    }

    private void RenderCategoryKind(CategoryKind kind)
    {
        var usage = _finance.Transactions.Where(t => t.CategoryId is not null).GroupBy(t => t.CategoryId!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in _finance.OrderedCategories(kind, includeArchived: _showArchived))
        {
            var body = new StackPanel { Margin = new Thickness(c.ParentId is null ? 0 : 26, 4, 8, 4), Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(FinanceUi.ColorDot(c.Color));
            var name = AgendaUi.Text(" " + c.Name + (c.IsArchived ? " (arquivada)" : ""), c.ParentId is null ? "BodyText" : "CaptionText", c.ParentId is null ? null : 13,
                c.IsArchived ? FinanceUi.Res("TextTertiaryBrush") : null);
            name.Margin = new Thickness(6, 0, 0, 0);
            body.Children.Add(name);
            var count = usage.GetValueOrDefault(c.Id);
            var right = AgendaUi.Text(count == 0 ? "" : $"{count} lanç.", "CaptionText", 11.5);
            right.VerticalAlignment = VerticalAlignment.Center;

            var actions = new StackPanel();
            if (c.ParentId is null && !c.IsArchived)
            {
                actions.Children.Add(AgendaUi.IconButton("", $"Nova subcategoria em {c.Name}", (_, _) => OpenEditor(new CategoryEditor(_finance, _finance.NewCategory(kind, c.Id), isNew: true))));
            }
            actions.Children.Add(AgendaUi.IconButton(c.IsArchived ? "" : "", c.IsArchived ? $"Reativar {c.Name}" : $"Arquivar {c.Name}", (_, _) => _finance.SetArchived(c, !c.IsArchived)));
            if (_finance.CanDeleteCategory(c.Id))
            {
                actions.Children.Add(AgendaUi.IconButton("", $"Excluir {c.Name}", (_, _) => _finance.DeleteCategory(c.Id)));
            }
            var spacer = new Border { Width = 0 };
            ListPanel.Children.Add(ActionRow(spacer, body, right, actions, _finance.CategoryPath(c.Id), () => OpenEditor(new CategoryEditor(_finance, c, isNew: false))));
        }
    }

    // ---------- Ações ----------

    private void OpenEditor(FinanceForm editor)
    {
        editor.Done += CloseEditor;
        if (editor is TransactionEditor te)
        {
            te.Deleted += ShowUndo;
        }
        EditorHost.Content = editor;
        EditorHost.Visibility = Visibility.Visible;
    }

    private void CloseEditor()
    {
        EditorHost.Content = null;
        EditorHost.Visibility = Visibility.Collapsed;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_finance.Accounts.All(a => a.IsArchived))
        {
            OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
            return;
        }
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = PlacementMode.Bottom };
        foreach (var kind in new[] { TransactionKind.Expense, TransactionKind.Income, TransactionKind.Transfer })
        {
            var item = new MenuItem { Header = FinanceUi.Kind(kind) };
            item.Click += (_, _) => OpenEditor(new TransactionEditor(_finance, NewDraft(kind), isNew: true));
            menu.Items.Add(item);
        }
        var account = new MenuItem { Header = "Conta…" };
        account.Click += (_, _) => OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
        menu.Items.Add(account);
        menu.IsOpen = true;
    }

    /// <summary>Novo lançamento já na conta filtrada e, se o mês exibido não é o atual, no dia 1 dele.</summary>
    private TransactionDraft NewDraft(TransactionKind kind)
    {
        var draft = _finance.NewDraft(kind);
        if ((AccountFilter.SelectedItem as Choice<Guid?>)?.Value is { } account && _finance.Account(account) is { IsArchived: false })
        {
            draft = draft with { AccountId = account };
        }
        var today = _finance.Today;
        if (today.Year != _year || today.Month != _month)
        {
            draft = draft with { Date = new DateOnly(_year, _month, 1) };
        }
        return draft;
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Tab>(tag, out var tab))
        {
            _tab = tab;
            _showArchived = false;
            Render();
        }
    }

    private void MoveMonth(int delta)
    {
        var d = new DateOnly(_year, _month, 1).AddMonths(delta);
        (_year, _month) = (d.Year, d.Month);
        _limit = PageSize;
        Render();
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e) => MoveMonth(-1);

    private void NextMonth_Click(object sender, RoutedEventArgs e) => MoveMonth(1);

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        var today = _finance.Today;
        (_year, _month) = (today.Year, today.Month);
        Render();
    }

    // Checked/Unchecked (e não Click): também cobre teclado e leitores de tela (padrão Toggle da UI Automation).
    private void Privacy_Changed(object sender, RoutedEventArgs e) => FinanceUi.Privacy = PrivacyButton.IsChecked == true;

    private void AllTime_Click(object sender, RoutedEventArgs e)
    {
        _allTime = !_allTime;
        _limit = PageSize;
        Render();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        SearchBox.Clear();
        MinBox.Clear();
        MaxBox.Clear();
        AccountFilter.SelectedIndex = 0;
        CategoryFilter.SelectedIndex = 0;
        KindFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;
        _allTime = false;
        _ready = true;
        Render();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && IsLoaded)
        {
            _limit = PageSize;
            Render();
        }
    }

    public void Dispose()
    {
        _undoTimer.Stop();
        FinanceUi.PrivacyChanged -= OnPrivacyChanged;
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
