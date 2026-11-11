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
        Cards,
        Planning,
        Projections,
        Import,
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
        foreach (var b in new UIElement[] { PrevMonth, NextMonth, TodayButton, TabOverview, TabTransactions, TabAccounts, TabCards, TabPlanning, TabProjections, TabImport, TabCategories })
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
        List<Choice<Guid?>> accounts =
        [
            new("Todas as contas e cartões", null),
            .. _finance.Accounts.Select(a => new Choice<Guid?>(a.Name + (a.IsArchived ? " (arquivada)" : ""), a.Id)),
            .. _finance.Cards.Select(c => new Choice<Guid?>("Cartão " + c.Name + (c.IsArchived ? " (arquivado)" : ""), c.Id)),
        ];
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
        foreach (var (button, tab) in new[] { (TabOverview, Tab.Overview), (TabTransactions, Tab.Transactions), (TabAccounts, Tab.Accounts), (TabCards, Tab.Cards), (TabPlanning, Tab.Planning), (TabProjections, Tab.Projections), (TabImport, Tab.Import), (TabCategories, Tab.Categories) })
        {
            button.DataContext = new ChipState(tab == _tab);
        }
        // A visão geral tem seus próprios cartões: sem o resumo de cima e sem a moldura da lista.
        var overview = _tab is Tab.Overview or Tab.Planning or Tab.Projections or Tab.Import;
        foreach (var e in new UIElement[] { PrevMonth, NextMonth, MonthText })
        {
            e.Visibility = _tab is Tab.Projections or Tab.Import ? Visibility.Collapsed : Visibility.Visible;
        }
        SummaryGrid.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        ListCard.Background = overview ? Brushes.Transparent : FinanceUi.Res("SurfaceBrush");
        ListCard.BorderThickness = new Thickness(overview ? 0 : 1);
        ListPanel.Margin = overview ? new Thickness(0, 0, 4, 8) : new Thickness(16, 8, 16, 16);
        AllTimeChip.DataContext = new ChipState(_allTime);
        PrivacyButton.Content = FinanceUi.Privacy ? "" : "";
        MonthText.Text = FinanceUi.MonthTitle(_year, _month);
        var today = _finance.Today;
        TodayButton.Visibility = today.Year == _year && today.Month == _month || _tab is Tab.Projections or Tab.Import ? Visibility.Collapsed : Visibility.Visible;
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
            case Tab.Cards:
                RenderCards();
                break;
            case Tab.Overview:
                RenderOverview();
                break;
            case Tab.Planning:
                RenderPlanning();
                break;
            case Tab.Projections:
                RenderProjections();
                break;
            case Tab.Import:
                RenderImport();
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
            if (_ready && _tab is Tab.Overview or Tab.Cards or Tab.Planning or Tab.Projections)
            {
                Render();
            }
        }
    }

    // ---------- Visão geral ----------

    private static Border Panel(string title, UIElement content, string? help = null, UIElement? headerRight = null, (string Glyph, string Color)? icon = null)
    {
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            head.Children.Add(headerRight);
        }
        if (icon is { } i)
        {
            var badge = Badge(i.Glyph, i.Color, 32);
            badge.Margin = new Thickness(0, 0, 10, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
        }
        var titleText = AgendaUi.Text(title, "SubtitleText", 14);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(titleText);
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

    private Border Indicator(string title, long value, string detail, Change? change, string previousName, bool higherIsGood, string help, bool showPlus = false,
        string glyph = "\uE9D9", string color = FinanceColors.Blue)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        var badge = Badge(glyph, color, 30);
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(AgendaUi.Text(title, "CaptionText"));
        panel.Children.Add(head);
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
            "Soma dos saldos confirmados até hoje das contas que entram no disponível (sem investimentos, contas arquivadas e outras moedas). Patrimônio inclui investimentos.",
            glyph: "", color: FinanceColors.Blue));
        SummaryGrid.Children.Add(SummaryCard($"Receitas · {month}", FinanceUi.Amount(totals.IncomeCleared),
            totals.IncomePending > 0 ? $"+ {FinanceUi.Amount(totals.IncomePending)} a receber" : "nada pendente",
            "Receitas confirmadas no mês. Pendentes aparecem à parte. Transferências e ajustes não contam.",
            glyph: "", color: FinanceColors.Green));
        SummaryGrid.Children.Add(SummaryCard($"Despesas · {month}", FinanceUi.Amount(totals.ExpenseCleared),
            totals.ExpensePending > 0 ? $"+ {FinanceUi.Amount(totals.ExpensePending)} a pagar" : "nada pendente",
            "Despesas confirmadas no mês, já descontados os estornos. Pendentes aparecem à parte. Transferências e ajustes não contam.",
            glyph: "", color: FinanceColors.Red));
        SummaryGrid.Children.Add(SummaryCard("Resultado do mês", FinanceUi.Amount(totals.Net, showPlus: true),
            "receitas − despesas, com pendentes",
            "Receitas menos despesas do mês, incluindo as pendentes.", totals.Net >= 0 ? null : FinanceUi.Res("WarningBrush"),
            glyph: "", color: FinanceColors.Purple));
    }

    private static Border SummaryCard(string title, string value, string detail, string definition, Brush? valueBrush = null, string glyph = "", string color = FinanceColors.Blue)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        var badge = Badge(glyph, color, 30);
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(AgendaUi.Text(title, "CaptionText"));
        panel.Children.Add(head);
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
        var card = _finance.Card(t.CardId);
        if (t.IsCardPayment)
        {
            Meta($"{account?.Name} → fatura {card?.Name} (vence {FinanceUi.Date(_finance.DueDate(card!, t.StatementClosing!.Value))})");
        }
        else if (t.Kind == TransactionKind.Transfer)
        {
            Meta($"{account?.Name} → {_finance.Account(t.ToAccountId)?.Name}");
        }
        else
        {
            Meta(t.Kind is TransactionKind.Adjustment ? "Ajuste de saldo" : _finance.CategoryPath(t.CategoryId));
            Meta(card is not null ? $"cartão {card.Name}" : account?.Name ?? "?");
        }
        if (t.PurchaseId is { } purchaseId && _finance.Purchase(purchaseId) is { Installments: > 1 } purchase)
        {
            Meta($"parcela {t.InstallmentNumber}/{purchase.Installments}");
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
        var currency = card?.Currency ?? account?.Currency ?? _finance.BaseCurrency;
        var neutral = t.Kind == TransactionKind.Transfer && accountFilter is null;
        var amountText = neutral ? "⇄ " + FinanceUi.Amount(t.Amount, currency) : FinanceUi.Amount(signed, currency, showPlus: true);
        var amount = AgendaUi.Text(amountText, "BodyText", 14, neutral ? FinanceUi.Res("TextSecondaryBrush") : FinanceUi.AmountBrush(signed));
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.FontWeight = FontWeights.SemiBold;
        if (t.Status == TransactionStatus.Pending)
        {
            amount.Opacity = 0.75;
        }

        var actions = new StackPanel();
        if (t.PurchaseId is null && !t.IsCardPayment)
        {
            actions.Children.Add(AgendaUi.IconButton("", $"Duplicar {t.Description}", (_, _) =>
                OpenEditor(t.CardId is null ? new TransactionEditor(_finance, _finance.Duplicate(t.Id), isNew: true) : new CardRefundEditor(_finance, _finance.Duplicate(t.Id), isNew: true))));
        }
        if (t.Kind == TransactionKind.Expense)
        {
            actions.Children.Add(AgendaUi.IconButton("", $"Registrar estorno de {t.Description}", (_, _) => OpenEditor(RefundEditorFor(t))));
        }
        if (t.PurchaseId is null)
        {
            actions.Children.Add(AgendaUi.IconButton("", $"Excluir {t.Description}", (_, _) => DeleteWithUndo(t.Id)));
        }

        var name = $"{FinanceUi.Kind(t)}: {t.Description}, {amountText}{(t.Status == TransactionStatus.Pending ? ", pendente" : "")}";
        return ActionRow(icon, body, amount, actions, name, () => OpenEditor(EditorFor(t)));
    }

    /// <summary>O editor certo para cada lançamento: compra no cartão, pagamento de fatura, estorno no cartão ou comum.</summary>
    private FinanceForm EditorFor(FinanceTransaction t)
    {
        if (t.PurchaseId is { } purchaseId && _finance.Purchase(purchaseId) is { } purchase)
        {
            return new PurchaseEditor(_finance, FinanceService.DraftFrom(purchase), isNew: false);
        }
        if (t.IsCardPayment)
        {
            return new PaymentEditor(_finance, FinanceService.DraftFrom(t), isNew: false);
        }
        if (t.CardId is not null)
        {
            return new CardRefundEditor(_finance, FinanceService.DraftFrom(t), isNew: false);
        }
        return new TransactionEditor(_finance, FinanceService.DraftFrom(t), isNew: false);
    }

    private FinanceForm RefundEditorFor(FinanceTransaction expense)
    {
        var draft = new TransactionDraft
        {
            Kind = TransactionKind.Refund,
            Date = _finance.Today,
            Description = "Estorno: " + expense.Description,
            AccountId = expense.AccountId,
            CardId = expense.CardId,
            CategoryId = expense.CategoryId,
            RefundOfId = expense.Id,
        };
        return expense.CardId is null ? new TransactionEditor(_finance, draft, isNew: true) : new CardRefundEditor(_finance, draft, isNew: true);
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

    // ---------- Cartões ----------

    private Guid? _selectedCard;
    private DateOnly? _selectedStatement;

    private PaymentEditor NewPayment(Guid cardId)
    {
        var card = _finance.Card(cardId)!;
        var statement = _finance.StatementToPay(cardId);
        var account = card.PaymentAccountId ?? _finance.Accounts.FirstOrDefault(a => !a.IsArchived)?.Id ?? Guid.Empty;
        return new PaymentEditor(_finance, new TransactionDraft
        {
            Kind = TransactionKind.Transfer,
            Date = _finance.Today,
            Description = $"Pagamento da fatura {card.Name}",
            CardId = cardId,
            StatementClosing = statement?.Closing,
            AccountId = account,
            Amount = Math.Max(0, statement?.Remaining ?? 0),
        }, isNew: true);
    }

    private void RenderCards()
    {
        var add = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Novo cartão" };
        add.Click += (_, _) => OpenEditor(new CardEditor(_finance, _finance.NewCard(), isNew: true));
        var cards = _finance.Cards.Where(c => _showArchived || !c.IsArchived).ToList();
        var archivedCount = _finance.Cards.Count(c => c.IsArchived);
        var buttons = archivedCount > 0
            ? new[] { Chip($"Mostrar arquivados ({archivedCount})", (_, _) => { _showArchived = !_showArchived; Render(); }, _showArchived), add }
            : [add];
        ListPanel.Children.Add(SectionHeader("Cartões de crédito", buttons));
        if (cards.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Nenhum cartão ainda. Cadastre com o dia de fechamento e de vencimento: as faturas, parcelas e o limite são calculados a partir das compras."));
            return;
        }
        if (_selectedCard is null || cards.All(c => c.Id != _selectedCard))
        {
            _selectedCard = cards[0].Id;
            _selectedStatement = null;
        }

        // Seletor de cartões.
        var selector = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(selector, "Cartões");
        foreach (var c in cards)
        {
            var chip = Chip(c.Name + (c.IsArchived ? " (arquivado)" : ""), (_, _) =>
            {
                _selectedCard = c.Id;
                _selectedStatement = null;
                Render();
            }, c.Id == _selectedCard);
            chip.Margin = new Thickness(0, 0, 6, 6);
            selector.Children.Add(chip);
        }
        ListPanel.Children.Add(selector);

        var card = _finance.Card(_selectedCard)!;
        var summary = _finance.Summary(card.Id);
        var statements = _finance.Statements(card.Id);
        var currency = card.Currency;

        // Resumo: limite usado, disponível, fatura aberta, próximo vencimento.
        var head = new DockPanel();
        // Em larguras pequenas (editor aberto) os botões vão para baixo do nome, sem esticar.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = _narrow ? new Thickness(0, 10, 0, 0) : new Thickness(0) };
        DockPanel.SetDock(tools, _narrow ? Dock.Bottom : Dock.Right);
        if (!card.IsArchived)
        {
            var buy = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Nova compra", Margin = new Thickness(0, 0, 8, 0) };
            buy.Click += (_, _) => OpenEditor(new PurchaseEditor(_finance, _finance.NewPurchase(card.Id), isNew: true));
            var pay = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Pagar fatura", Margin = new Thickness(0, 0, 4, 0) };
            pay.Click += (_, _) => OpenEditor(NewPayment(card.Id));
            tools.Children.Add(buy);
            tools.Children.Add(pay);
        }
        tools.Children.Add(AgendaUi.IconButton("", $"Editar {card.Name}", (_, _) => OpenEditor(new CardEditor(_finance, card, isNew: false)), size: 32));
        tools.Children.Add(AgendaUi.IconButton(card.IsArchived ? "" : "", card.IsArchived ? $"Reativar {card.Name}" : $"Arquivar {card.Name}", (_, _) => _finance.SetArchived(card, !card.IsArchived), size: 32));
        head.Children.Add(tools);
        var title = new StackPanel();
        title.Children.Add(AgendaUi.Text(card.Name, "SubtitleText", 16));
        var rule = card.CurrentRule;
        title.Children.Add(AgendaUi.Text(string.Join(" · ", new[] { card.Institution, card.Network, card.LastDigits is { } d ? $"final {d}" : null, $"fecha dia {rule.ClosingDay}, vence dia {rule.DueDay}" }.Where(x => x is not null)), "CaptionText", 12));
        head.Children.Add(title);
        var top = new StackPanel();
        top.Children.Add(head);

        var used = Math.Max(0, summary.Debt);
        var ratio = card.Limit > 0 ? Math.Min(1, used / (double)card.Limit) : 0;
        var bar = new Grid { Height = 10, Margin = new Thickness(0, 14, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        bar.Children.Add(new Border { Background = FinanceUi.Res("SurfaceElevatedBrush"), CornerRadius = new CornerRadius(5), Margin = new Thickness(0) { }, });
        Grid.SetColumnSpan(bar.Children[0], 2);
        var fill = new Border { Background = ratio >= 0.9 ? FinanceUi.Res("WarningBrush") : (Brush)FindResource("AccentBrush"), CornerRadius = new CornerRadius(5) };
        bar.Children.Add(fill);
        AutomationProperties.SetName(bar, $"Limite usado: {Money.Percent(used, card.Limit)?.ToString("0", FinanceUi.PtBr) ?? "0"}%");
        top.Children.Add(bar);
        top.Children.Add(AgendaUi.Text(
            $"Usado {FinanceUi.Amount(used, currency)} de {FinanceUi.Amount(card.Limit, currency)} · disponível {FinanceUi.Amount(summary.Available, currency)}" +
            (summary.Debt < 0 ? $" · crédito de {FinanceUi.Amount(-summary.Debt, currency)}" : "") + " · o limite usado inclui parcelas futuras", "CaptionText", 12));
        var facts = new UniformGrid { Columns = _narrow ? 2 : 3, Margin = new Thickness(0, 12, 0, 0) };
        void Fact(string label, string value, string? detail = null, Brush? brush = null)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 12, 4) };
            p.Children.Add(AgendaUi.Text(label, "CaptionText", 11.5));
            p.Children.Add(AgendaUi.Text(value, "BodyText", 16, brush));
            if (detail is not null)
            {
                p.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5));
            }
            AutomationProperties.SetName(p, $"{label}: {value}{(detail is null ? "" : ", " + detail)}");
            facts.Children.Add(p);
        }
        if (summary.OpenStatement is { } open)
        {
            Fact("Fatura aberta", FinanceUi.Amount(open.AmountDue, currency),
                (open.Payments > 0 ? $"pago {FinanceUi.Amount(open.Payments, currency)} · " : "") + $"fecha {FinanceUi.Date(open.Closing)} · vence {FinanceUi.Date(open.Due)}");
        }
        if (summary.NextDue is { } next && next != summary.OpenStatement)
        {
            var late = next.Status is StatementStatus.Overdue or StatementStatus.PartiallyPaid;
            Fact(late ? "Fatura vencida" : "Próximo vencimento", FinanceUi.Amount(Math.Max(0, next.Remaining), currency),
                $"{(late ? "venceu" : "vence")} {FinanceUi.Date(next.Due)} · {FinanceUi.StatementStatus(next.Status)}", late ? FinanceUi.Res("WarningBrush") : null);
        }
        var futureTotal = statements.Where(s => s.Status == StatementStatus.Future).Sum(s => s.Charges);
        Fact("Parcelas futuras", FinanceUi.Amount(futureTotal, currency), "em faturas que ainda não abriram");
        top.Children.Add(facts);
        ListPanel.Children.Add(Panel("Resumo do cartão", top, "Valores calculados das compras, parcelas, estornos e pagamentos deste cartão."));

        // Linha do tempo de faturas: últimas fechadas, aberta e futuras.
        var openIndex = statements.ToList().FindLastIndex(s => s.Status != StatementStatus.Future);
        var window = statements.Skip(Math.Max(0, openIndex - 5)).Take(12).ToList();
        var defaultStatement = _finance.StatementToPay(card.Id)?.Closing ?? summary.OpenStatement?.Closing;
        _selectedStatement ??= defaultStatement;
        var selected = statements.FirstOrDefault(s => s.Closing == _selectedStatement) ?? window.LastOrDefault();
        var timeline = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(timeline, "Faturas");
        foreach (var s in window)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = s.Due.ToString("MMM yy", FinanceUi.PtBr), FontSize = 11.5 });
            label.Children.Add(new TextBlock { Text = FinanceUi.Amount(s.AmountDue, currency), FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = FinanceUi.StatementStatus(s.Status), FontSize = 11, Foreground = s.Status is StatementStatus.Overdue or StatementStatus.PartiallyPaid ? FinanceUi.Res("WarningBrush") : FinanceUi.Res("TextTertiaryBrush") });
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = label, Margin = new Thickness(0, 0, 6, 6), DataContext = new ChipState(s.Closing == selected?.Closing) };
            AutomationProperties.SetName(chip, $"Fatura que vence em {FinanceUi.Date(s.Due)}: {FinanceUi.Amount(s.AmountDue, currency)}, {FinanceUi.StatementStatus(s.Status)}");
            var closing = s.Closing;
            chip.Click += (_, _) =>
            {
                _selectedStatement = closing;
                Render();
            };
            timeline.Children.Add(chip);
        }
        if (selected is not null)
        {
            ListPanel.Children.Add(Panel("Faturas", StatementDetail(card, selected, timeline), "Escolha uma fatura para ver os lançamentos. Futuras = só parcelas que ainda vão entrar."));
        }

        // Parcelamentos em andamento.
        var running = _finance.Purchases.Where(p => p.CardId == card.Id && p.Installments > 1)
            .Select(p => (Purchase: p, Left: _finance.Installments(p.Id).Where(t => t.StatementClosing is { } c && !StatementCalendar.IsClosed(card, c, _finance.Today)).ToList()))
            .Where(x => x.Left.Count > 0).OrderByDescending(x => x.Left.Sum(t => t.Amount)).ToList();
        if (running.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (p, left) in running)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var value = AgendaUi.Text($"faltam {FinanceUi.Amount(left.Sum(t => t.Amount), currency)}", "BodyText", 13);
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(value);
                row.Children.Add(AgendaUi.Text($"{p.Description} · {p.Installments - left.Count}/{p.Installments} pagas na fatura · {left.Count}× de {FinanceUi.Amount(left[^1].Amount, currency)}", "BodyText", 13));
                var purchase = p;
                row.MouseLeftButtonUp += (_, _) => OpenEditor(new PurchaseEditor(_finance, FinanceService.DraftFrom(purchase), isNew: false));
                AutomationProperties.SetName(row, $"{p.Description}: faltam {left.Count} parcelas, {FinanceUi.Amount(left.Sum(t => t.Amount), currency)}");
                list.Children.Add(row);
            }
            ListPanel.Children.Add(Panel("Parcelamentos em andamento", list, "Compras parceladas com parcelas na fatura aberta ou em faturas futuras."));
        }
        FooterText.Text = "Compras no cartão contam como despesa na data de cada parcela; pagar a fatura só tira o dinheiro da conta (não é outra despesa).";
    }

    private StackPanel StatementDetail(CreditCard card, CardStatement s, UIElement timeline)
    {
        var currency = card.Currency;
        var panel = new StackPanel();
        panel.Children.Add(timeline);
        var head = new DockPanel { Margin = new Thickness(0, 8, 0, 6) };
        if (s.Status is not StatementStatus.Future)
        {
            var fixDue = Chip("Corrigir vencimento", (_, _) => OpenEditor(new StatementDueEditor(_finance, card, s)));
            DockPanel.SetDock(fixDue, Dock.Right);
            head.Children.Add(fixDue);
        }
        head.Children.Add(AgendaUi.Text($"{FinanceUi.Date(s.Opening)} a {FinanceUi.Date(s.Closing)} · vence {FinanceUi.Date(s.Due)} · {FinanceUi.StatementStatus(s.Status)}", "BodyText", 13.5));
        panel.Children.Add(head);
        var numbers = new WrapPanel();
        void N(string label, long value, bool showPlus = false)
        {
            var t = AgendaUi.Text($"{label} {FinanceUi.Amount(value, currency, showPlus)}", "CaptionText", 12.5, FinanceUi.Res("TextPrimaryBrush"));
            t.Margin = new Thickness(0, 0, 18, 4);
            numbers.Children.Add(t);
        }
        if (s.Previous != 0)
        {
            N(s.Previous > 0 ? "Saldo anterior" : "Crédito anterior", Math.Abs(s.Previous));
        }
        N("Compras e parcelas", s.Charges);
        N("Total devido", s.AmountDue);
        N("Pago", s.Payments);
        N(s.Remaining >= 0 ? "Restante" : "Crédito", Math.Abs(s.Remaining));
        panel.Children.Add(numbers);
        if (s.Status == StatementStatus.PartiallyPaid)
        {
            var note = AgendaUi.Text("O restante passou para a próxima fatura. Juros e encargos não são calculados pelo app: lance-os como compra (categoria Outros ou uma sua) quando aparecerem.", "CaptionText", 11.5, FinanceUi.Res("WarningBrush"));
            note.TextWrapping = TextWrapping.Wrap;
            note.TextTrimming = TextTrimming.None;
            panel.Children.Add(note);
        }

        var items = _finance.Transactions.Where(t => t.CardId == card.Id && t.StatementClosing == s.Closing)
            .OrderBy(t => t.IsCardPayment).ThenBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
        if (items.Count == 0)
        {
            panel.Children.Add(Paragraph("Nenhum lançamento nesta fatura."));
        }
        foreach (var t in items)
        {
            panel.Children.Add(TransactionRow(t, null));
        }
        return panel;
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
        switch (editor)
        {
            case TransactionEditor te:
                te.Deleted += ShowUndo;
                break;
            case PaymentEditor pe:
                pe.Deleted += ShowUndo;
                break;
            case CardRefundEditor re:
                re.Deleted += ShowUndo;
                break;
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
        if (_finance.Cards.Any(c => !c.IsArchived))
        {
            var purchase = new MenuItem { Header = "Compra no cartão" };
            purchase.Click += (_, _) => OpenEditor(new PurchaseEditor(_finance, _finance.NewPurchase(_selectedCard), isNew: true));
            menu.Items.Add(purchase);
            var payment = new MenuItem { Header = "Pagamento de fatura" };
            payment.Click += (_, _) => OpenEditor(NewPayment(_selectedCard ?? _finance.Cards.First(c => !c.IsArchived).Id));
            menu.Items.Add(payment);
        }
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var account = new MenuItem { Header = "Conta…" };
        account.Click += (_, _) => OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
        menu.Items.Add(account);
        var newCard = new MenuItem { Header = "Cartão de crédito…" };
        newCard.Click += (_, _) => OpenEditor(new CardEditor(_finance, _finance.NewCard(), isNew: true));
        menu.Items.Add(newCard);
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
