using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Finanças no modo simples, organizado como a planilha do usuário: totais do mês no topo, "restante para gastar" e
/// quatro listas (entradas, gastos, assinaturas e reservas). Contas agrupadas por banco com os cartões de cada uma;
/// assinaturas numa aba própria. As telas avançadas ficam no menu "Mais".
/// </summary>
public partial class FinancePage
{
    // ---------- Peças: tabela no estilo da planilha ----------

    /// <summary>Tabela simples: cabeçalho em maiúsculas e linhas separadas por um fio, como na planilha.</summary>
    private static Grid SheetTable(string[] headers, double[] widths, IEnumerable<(UIElement[] Cells, string Name, Action Open)> rows, string empty)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var w in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w <= 0 ? GridLength.Auto : new GridLength(w, GridUnitType.Star) });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
        {
            var h = AgendaUi.Text(headers[c].ToUpper(FinanceUi.PtBr), "CaptionText", 10.5);
            h.FontWeight = FontWeights.SemiBold;
            h.Margin = new Thickness(c == 0 ? 0 : 8, 0, 0, 6);
            if (c > 0 && c == headers.Length - 1)
            {
                h.HorizontalAlignment = HorizontalAlignment.Right;
            }
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        var r = 0;
        foreach (var (cells, name, open) in rows)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Background = Brushes.Transparent,
                Cursor = open is null ? null : System.Windows.Input.Cursors.Hand,
                ToolTip = open is null ? null : "Clique para abrir",
            };
            line.SetResourceReference(Border.BorderBrushProperty, "BorderTokenBrush");
            Grid.SetRow(line, r);
            Grid.SetColumnSpan(line, headers.Length);
            if (open is not null)
            {
                line.MouseLeftButtonUp += (_, _) => open();
            }
            AutomationProperties.SetName(line, name);
            grid.Children.Add(line);
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                if (cell is FrameworkElement fe)
                {
                    fe.Margin = new Thickness(c == 0 ? 0 : 8, 7, 0, 7);
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    if (c > 0 && c == cells.Length - 1)
                    {
                        fe.HorizontalAlignment = HorizontalAlignment.Right;
                    }
                    if (open is not null && cell is TextBlock)
                    {
                        fe.IsHitTestVisible = false; // o clique vai para a linha
                    }
                }
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        if (r == 0)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var e = Wrap(AgendaUi.Text(empty, "CaptionText", 12));
            e.Margin = new Thickness(0, 8, 0, 4);
            Grid.SetRow(e, 1);
            Grid.SetColumnSpan(e, headers.Length);
            grid.Children.Add(e);
        }
        return grid;
    }

    private static TextBlock Cell(string text, Brush? brush = null, bool bold = false)
    {
        var t = AgendaUi.Text(text, "BodyText", 13, brush);
        if (bold)
        {
            t.FontWeight = FontWeights.SemiBold;
        }
        return t;
    }

    /// <summary>Cartão de total como na planilha: título, valor e "mês anterior".</summary>
    private Border TotalCard(string title, long value, long? previous, string glyph, string color, string help, string? note = null)
    {
        var currency = _finance.BaseCurrency;
        var panel = new StackPanel();
        var head = new DockPanel();
        var badge = Badge(glyph, color, 30);
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(AgendaUi.Text(title, "CaptionText", 12.5));
        panel.Children.Add(head);
        var big = AgendaUi.Text(FinanceUi.Amount(value, currency), "TitleText", 22);
        big.Margin = new Thickness(0, 2, 0, 6);
        panel.Children.Add(big);
        var label = note ?? (previous is { } p ? $"mês anterior: {FinanceUi.Amount(p, currency)}" : null);
        if (label is not null)
        {
            var prev = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
            prev.SetResourceReference(Border.BackgroundProperty, "SurfaceElevatedBrush");
            prev.Child = AgendaUi.Text(label, "CaptionText", 11.5);
            panel.Children.Add(prev);
        }
        var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 12, 12), Child = panel, ToolTip = help };
        AutomationProperties.SetName(card, $"{title}: {FinanceUi.Amount(value, currency)}" + (label is null ? "" : $", {label}"));
        AutomationProperties.SetHelpText(card, help);
        return card;
    }

    /// <summary>Números do mês usados nos totais (moeda base).</summary>
    private (long Income, long Expense, long Debit, long Credit, long Reserved) MonthNumbers(int year, int month)
    {
        var (start, end) = FinanceService.MonthRange(year, month);
        var totals = _finance.Totals(start, end);
        long credit = 0;
        foreach (var t in _finance.Transactions.Where(t => t.CardId is not null && t.Date >= start && t.Date <= end))
        {
            credit += t.Kind switch
            {
                TransactionKind.Expense => t.Amount,
                TransactionKind.Refund => -t.Amount,
                _ => 0,
            };
        }
        var reserved = _finance.Goals.SelectMany(g => _finance.Contributions(g.Id))
            .Where(c => c.Kind == ContributionKind.Real && c.Date >= start && c.Date <= end).Sum(c => c.Amount);
        return (totals.Income, totals.Expense, totals.Expense - credit, credit, reserved);
    }

    // ---------- Mês ----------

    private void RenderMonth()
    {
        if (_finance.Accounts.Count == 0)
        {
            ListCard.Background = FinanceUi.Res("SurfaceBrush");
            ListPanel.Margin = new Thickness(16, 8, 16, 16);
            RenderTransactions(); // estado vazio: convite para cadastrar a primeira conta
            return;
        }
        var currency = _finance.BaseCurrency;
        var (start, end) = FinanceService.MonthRange(_year, _month);
        var monthName = FinanceUi.MonthTitle(_year, _month).Split(' ')[0];
        var previous = new DateOnly(_year, _month, 1).AddMonths(-1);
        var now = MonthNumbers(_year, _month);
        var before = MonthNumbers(previous.Year, previous.Month);

        // 1. Totais do mês.
        var totals = new UniformGrid { Columns = _narrow ? 2 : 5, Margin = new Thickness(0, 0, -12, 0) };
        totals.Children.Add(TotalCard("Entradas", now.Income, before.Income, "", FinanceColors.Green, "Tudo que entrou (ou vai entrar) no mês: salário, pix recebidos, rendimentos."));
        totals.Children.Add(TotalCard("Saídas", now.Expense, before.Expense, "", FinanceColors.Red, "Todos os gastos do mês (no débito e no cartão), menos estornos."));
        totals.Children.Add(TotalCard("Reservas", now.Reserved, before.Reserved, "", FinanceColors.Teal, "Dinheiro guardado nas metas neste mês."));
        totals.Children.Add(TotalCard("Uso do débito", now.Debit, before.Debit, "", FinanceColors.Blue, "Gastos pagos direto da conta (pix, débito, boleto)."));
        totals.Children.Add(TotalCard("Uso do crédito", now.Credit, before.Credit, "", FinanceColors.Purple, "Gastos no cartão de crédito neste mês (parcelas na data de cada parcela)."));
        ListPanel.Children.Add(totals);

        // 2. Restante para gastar + para onde foi o dinheiro.
        var subscriptions = _finance.Occurrences(start, end).Where(o => !o.Item.IsIncome).ToList();
        var toPay = subscriptions.Where(o => o.IsOpen).Sum(o => o.Amount);
        var remaining = now.Income - now.Expense - now.Reserved - toPay;
        var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 18) };
        var big = BigMoney(remaining, 44, remaining < 0 ? FinanceUi.Res("DangerBrush") : null, $"Restante para gastar: {FinanceUi.Amount(remaining, currency)}");
        big.HorizontalAlignment = HorizontalAlignment.Center;
        left.Children.Add(big);
        var formula = Wrap(AgendaUi.Text($"entradas {FinanceUi.Amount(now.Income, currency)} − saídas {FinanceUi.Amount(now.Expense, currency)} − reservas {FinanceUi.Amount(now.Reserved, currency)}" +
            (toPay > 0 ? $" − assinaturas a pagar {FinanceUi.Amount(toPay, currency)}" : ""), "CaptionText", 12));
        formula.TextAlignment = TextAlignment.Center;
        formula.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(formula);
        var restCard = Panel("Restante para gastar", left, "O que sobra do mês depois dos gastos, das reservas e das assinaturas que ainda vão ser pagas.", null, ("", FinanceColors.Green));

        var spending = _finance.SpendingByCategory(start, end);
        FrameworkElement where = spending.Count == 0 ? Paragraph($"Nenhum gasto em {monthName.ToLower(FinanceUi.PtBr)}.") : FinanceCharts.CategoryBars(spending, currency, 6);
        var whereCard = Panel("Para onde foi o dinheiro", where, "Gastos do mês por categoria (somando as subcategorias), menos estornos.", null, ("", FinanceColors.Pink));
        ListPanel.Children.Add(Columns((restCard, 1), (whereCard, 1)));

        // 3. As quatro listas do mês.
        var incomes = _finance.Transactions.Where(t => t.Kind == TransactionKind.Income && t.Date >= start && t.Date <= end).OrderBy(t => t.Date).ToList();
        var incomeTotal = Math.Max(1, incomes.Sum(t => t.Amount));
        var incomeTable = SheetTable(["Nome", "Valor", "%"], [3, 2, 0],
            incomes.Select(t => (new UIElement[]
            {
                Cell(t.Description + (t.Status == TransactionStatus.Pending ? " (a receber)" : "")),
                Cell(FinanceUi.Amount(t.Amount, currency), FinanceUi.Res("SuccessBrush")),
                Cell(Percent(t.Amount, incomeTotal)),
            }, $"{t.Description}, {FinanceUi.Amount(t.Amount, currency)}", (Action)(() => OpenEditor(EditorFor(t))))),
            "Nenhuma entrada ainda. Use \"+ Lançamento › Entrada\".");
        var addIncome = Chip("+ Entrada", (_, _) => OpenEditor(new TransactionEditor(_finance, NewDraft(TransactionKind.Income), isNew: true)));
        var incomeCard = Panel("Entradas", incomeTable, "Receitas do mês.", addIncome, ("", FinanceColors.Green));

        var expenses = _finance.Transactions.Where(t => t.Kind == TransactionKind.Expense && t.Date >= start && t.Date <= end && t.RecurringId is null)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ToList();
        const int maxRows = 12;
        var expenseTable = SheetTable(["Categoria", "Descrição", "Pago com", "Data", "Valor"], [2, 3, 2, 0, 0],
            expenses.Take(maxRows).Select(t => (new UIElement[]
            {
                Cell(_finance.Category(t.CategoryId)?.Name ?? "—"),
                Cell(t.Description + (t.InstallmentNumber is { } k && _finance.Purchase(t.PurchaseId) is { } p ? $" ({k}/{p.Installments})" : "")),
                Cell(t.CardId is { } card ? "Cartão " + _finance.Card(card)?.Name : _finance.Account(t.AccountId)?.Name ?? "?"),
                Cell(t.Date.ToString("dd/MM", FinanceUi.PtBr)),
                Cell(FinanceUi.Amount(t.Amount, currency), bold: true),
            }, $"{t.Description}, {FinanceUi.Amount(t.Amount, currency)}, {FinanceUi.Date(t.Date)}", (Action)(() => OpenEditor(EditorFor(t))))),
            "Nenhum gasto ainda. Use \"+ Lançamento › Gasto\".");
        var expensePanel = new StackPanel();
        expensePanel.Children.Add(expenseTable);
        if (expenses.Count > maxRows)
        {
            var all = Chip($"Ver todos os {expenses.Count} gastos", (_, _) =>
            {
                _tab = Tab.Transactions;
                Render();
            });
            all.Margin = new Thickness(0, 8, 0, 0);
            all.HorizontalAlignment = HorizontalAlignment.Left;
            expensePanel.Children.Add(all);
        }
        var addExpense = Chip("+ Gasto", (_, _) => OpenEditor(new TransactionEditor(_finance, NewDraft(TransactionKind.Expense), isNew: true)));
        var expenseCard = Panel("Gastos", expensePanel, "Gastos do mês, do mais recente para o mais antigo (assinaturas ficam na lista ao lado).", addExpense, ("", FinanceColors.Red));
        ListPanel.Children.Add(Columns((incomeCard, 2), (expenseCard, 3)));

        var paid = subscriptions.Count(o => o.State == OccurrenceState.Confirmed);
        var subsTable = SheetTable(["", "Nome", "Planejado", "Valor", "Pago em"], [0, 3, 2, 2, 0],
            subscriptions.Where(o => o.State != OccurrenceState.Skipped).Select(o => (new UIElement[]
            {
                PaidBox(o),
                Cell(o.Item.Description + (o.State == OccurrenceState.Overdue ? " · atrasada" : ""), o.State == OccurrenceState.Overdue ? FinanceUi.Res("WarningBrush") : null),
                Cell(FinanceUi.Amount(_finance.ExpectedAmount(o.Item), currency), FinanceUi.Res("TextSecondaryBrush")),
                Cell(o.State == OccurrenceState.Confirmed ? FinanceUi.Amount(o.Amount, currency) : "—", bold: true),
                Cell(o.State == OccurrenceState.Confirmed && o.TransactionId is { } id && _finance.Find(id) is { } t ? t.Date.ToString("dd/MM", FinanceUi.PtBr) : $"dia {o.Date.Day}"),
            }, $"{o.Item.Description}, {(o.State == OccurrenceState.Confirmed ? "paga" : "a pagar")}, {FinanceUi.Amount(o.Amount, currency)}", (Action)(() => OpenEditor(new SubscriptionEditor(_finance, o.Item))))),
            "Nenhuma assinatura. Cadastre os pagamentos certos de todo mês na aba Assinaturas.");
        var subsPanel = new StackPanel();
        subsPanel.Children.Add(AgendaUi.Text($"{paid} de {subscriptions.Count(o => o.State != OccurrenceState.Skipped)} pagas · total {FinanceUi.Amount(subscriptions.Where(o => o.State != OccurrenceState.Skipped).Sum(o => o.Amount), currency)}", "CaptionText", 12));
        subsPanel.Children.Add(subsTable);
        var addSub = Chip("+ Assinatura", (_, _) => OpenEditor(new SubscriptionEditor(_finance, null)));
        var subsCard = Panel("Assinaturas do mês", subsPanel, "Pagamentos certos de todo mês. Marque o quadradinho quando pagar.", addSub, ("", FinanceColors.Orange));

        var goals = _finance.Goals.Where(g => !g.IsArchived).ToList();
        var goalsTable = SheetTable(["Meta", "Neste mês", "Guardado", "Falta"], [3, 2, 2, 0],
            goals.Select(g =>
            {
                var p = _finance.Progress(g);
                var month = _finance.Contributions(g.Id).Where(c => c.Kind == ContributionKind.Real && c.Date >= start && c.Date <= end).Sum(c => c.Amount);
                return (new UIElement[]
                {
                    Cell(g.Name),
                    Cell(FinanceUi.Amount(month, currency), FinanceUi.Res("SuccessBrush")),
                    Cell(FinanceUi.Amount(p.Real, currency)),
                    Cell(p.Reached ? "✓ atingida" : FinanceUi.Amount(p.Remaining, currency), bold: true),
                }, $"{g.Name}: guardado {FinanceUi.Amount(p.Real, currency)}, falta {FinanceUi.Amount(p.Remaining, currency)}", (Action)(() => OpenEditor(new ContributionEditor(_finance, g))));
            }),
            "Nenhuma meta. Crie uma (ex.: reserva de emergência) na aba Metas.");
        var addGoal = Chip("+ Meta", (_, _) => OpenEditor(new GoalEditor(_finance, _finance.NewGoal(), isNew: true)));
        var goalsCard = Panel("Reservas e metas", goalsTable, "Quanto você guardou em cada meta. Clique numa meta para guardar dinheiro.", addGoal, ("", FinanceColors.Teal));
        ListPanel.Children.Add(Columns((subsCard, 3), (goalsCard, 2)));
        FooterText.Text = $"Organização de {monthName.ToLower(FinanceUi.PtBr)}. Clique numa linha para editar.";
    }

    /// <summary>Quadradinho de "paga": marcar confirma a ocorrência com o valor previsto; desmarcar desfaz.</summary>
    private CheckBox PaidBox(RecurringOccurrence o)
    {
        var box = new CheckBox { IsChecked = o.State == OccurrenceState.Confirmed, Cursor = System.Windows.Input.Cursors.Hand };
        AutomationProperties.SetName(box, $"{o.Item.Description} paga em {FinanceUi.MonthTitle(o.Date.Year, o.Date.Month)}");
        // Checked/Unchecked (e não Click): também funciona pelo teclado e por leitores de tela.
        box.Checked += (_, _) => Try(() =>
        {
            if (o.State != OccurrenceState.Confirmed)
            {
                var today = _finance.Today;
                _finance.Confirm(o.Item.Id, o.Date, paidOn: o.Date <= today ? o.Date : today);
            }
        });
        box.Unchecked += (_, _) => Try(() =>
        {
            if (o.TransactionId is { } id)
            {
                var t = _finance.Find(id);
                if (t?.PurchaseId is { } purchase)
                {
                    _finance.DeletePurchase(purchase);
                }
                else
                {
                    _finance.Delete(id);
                }
            }
        });
        return box;
    }

    // ---------- Contas e cartões ----------

    private void RenderAccountsAndCards()
    {
        var currency = _finance.BaseCurrency;
        var addAccount = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "+ Conta", Margin = new Thickness(0, 0, 8, 0) };
        addAccount.Click += (_, _) => OpenEditor(new AccountEditor(_finance, _finance.NewAccount(), isNew: true));
        var addCard = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Cartão" };
        addCard.Click += (_, _) => OpenEditor(new CardEditor(_finance, _finance.NewCard(), isNew: true));
        addCard.IsEnabled = _finance.Accounts.Any(a => !a.IsArchived);
        ListPanel.Children.Add(SectionHeader("Seus bancos", addAccount, addCard));
        var accounts = _finance.Accounts.Where(a => !a.IsArchived).ToList();
        if (accounts.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Comece cadastrando a conta de cada banco (corrente, poupança, carteira digital). Depois, os cartões de cada um."));
            return;
        }
        var balances = _finance.CurrentBalances();
        var cards = _finance.Cards.Where(c => !c.IsArchived).ToList();
        var tiles = new List<UIElement>();
        foreach (var bank in accounts.GroupBy(a => string.IsNullOrWhiteSpace(a.Institution) ? "Sem banco informado" : a.Institution!.Trim()))
        {
            var body = new StackPanel();
            foreach (var a in bank)
            {
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 2), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
                var value = AgendaUi.Text(FinanceUi.Amount(balances[a.Id], a.Currency), "BodyText", 16, balances[a.Id] < 0 ? FinanceUi.Res("WarningBrush") : null);
                value.FontWeight = FontWeights.SemiBold;
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(value);
                var icon = IconCircle(FinanceUi.Glyph(a.Kind), a.Color);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(AgendaUi.Text(a.Name, "BodyText", 14));
                text.Children.Add(AgendaUi.Text(FinanceUi.Kind(a.Kind) + (a.IncludeInAvailable ? "" : " · fora do disponível"), "CaptionText", 11.5));
                row.Children.Add(text);
                var account = a;
                row.MouseLeftButtonUp += (_, _) => OpenEditor(new AccountEditor(_finance, account, isNew: false));
                AutomationProperties.SetName(row, $"{a.Name}: saldo {FinanceUi.Amount(balances[a.Id], a.Currency)}");
                body.Children.Add(row);

                foreach (var card in cards.Where(c => c.PaymentAccountId == a.Id))
                {
                    body.Children.Add(CardRow(card));
                }
                var actions = new WrapPanel { Margin = new Thickness(44, 2, 0, 6) };
                var newCard = Chip("+ Cartão desta conta", (_, _) => OpenEditor(new CardEditor(_finance, _finance.NewCard() with { PaymentAccountId = account.Id }, isNew: true)));
                newCard.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(newCard);
                var statement = Chip("Extrato", (_, _) => ShowAccountTransactions(account.Id));
                AutomationProperties.SetName(statement, $"Ver lançamentos de {a.Name}");
                statement.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(statement);
                var adjust = Chip("Ajustar saldo", (_, _) => OpenEditor(new AdjustBalanceEditor(_finance, account)));
                AutomationProperties.SetName(adjust, $"Ajustar saldo de {a.Name}");
                actions.Children.Add(adjust);
                body.Children.Add(actions);
            }
            var total = bank.Where(a => a.Currency == currency).Sum(a => balances[a.Id]);
            tiles.Add(Panel(bank.Key, body, "Contas deste banco e os cartões ligados a cada uma.", AgendaUi.Text(FinanceUi.Amount(total, currency), "BodyText", 14), ("", FinanceColors.Blue)));
        }
        var orphans = cards.Where(c => c.PaymentAccountId is null || accounts.All(a => a.Id != c.PaymentAccountId)).ToList();
        if (orphans.Count > 0)
        {
            var body = new StackPanel();
            body.Children.Add(Wrap(AgendaUi.Text("Estes cartões ainda não estão ligados a uma conta. Abra o cartão e escolha a conta do banco.", "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
            foreach (var card in orphans)
            {
                body.Children.Add(CardRow(card));
            }
            tiles.Add(Panel("Cartões sem conta", body, "Ligue cada cartão à conta do banco dele.", null, ("", FinanceColors.Orange)));
        }
        for (var i = 0; i < tiles.Count; i += 2)
        {
            ListPanel.Children.Add(i + 1 < tiles.Count ? Columns((tiles[i], 1), (tiles[i + 1], 1)) : Columns((tiles[i], 1), (new Border(), 1)));
        }
        FooterText.Text = $"Saldo em contas {FinanceUi.Amount(_finance.AccountsBalance())} · patrimônio {FinanceUi.Amount(_finance.NetWorth())} (contas − dívida dos cartões). Clique numa conta ou cartão para editar.";
    }

    private Border CardRow(CreditCard card)
    {
        var currency = card.Currency;
        var summary = _finance.Summary(card.Id);
        var toPay = _finance.StatementToPay(card.Id);
        var row = new DockPanel();
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var invoice = AgendaUi.Text(toPay is null ? "sem fatura" : $"fatura {FinanceUi.Amount(Math.Max(0, toPay.Remaining), currency)}", "BodyText", 13.5);
        invoice.FontWeight = FontWeights.SemiBold;
        invoice.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(invoice);
        var due = AgendaUi.Text(toPay is null ? "" : $"vence {toPay.Due.ToString("dd/MM", FinanceUi.PtBr)} · livre {FinanceUi.Amount(summary.Available, currency)}", "CaptionText", 11.5);
        due.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(due);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var icon = IconCircle("", card.Color);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(AgendaUi.Text("Cartão " + card.Name + (card.LastDigits is { } d ? $" ••{d}" : ""), "BodyText", 13.5));
        text.Children.Add(AgendaUi.Text($"fecha dia {card.CurrentRule.ClosingDay} · vence dia {card.CurrentRule.DueDay}", "CaptionText", 11.5));
        row.Children.Add(text);
        var border = new Border { Margin = new Thickness(28, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(10), Child = row, Cursor = System.Windows.Input.Cursors.Hand };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceElevatedBrush");
        border.MouseLeftButtonUp += (_, _) =>
        {
            _tab = Tab.Cards;
            _selectedCard = card.Id;
            _selectedStatement = toPay?.Closing;
            Render();
        };
        border.ToolTip = "Clique para ver as faturas";
        AutomationProperties.SetName(border, $"Cartão {card.Name}: {(toPay is null ? "sem fatura" : $"fatura {FinanceUi.Amount(Math.Max(0, toPay.Remaining), currency)}, vence {FinanceUi.Date(toPay.Due)}")}");
        return border;
    }

    // ---------- Assinaturas ----------

    private void RenderSubscriptions()
    {
        var currency = _finance.BaseCurrency;
        var add = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "+ Assinatura" };
        add.Click += (_, _) => OpenEditor(new SubscriptionEditor(_finance, null));
        add.IsEnabled = _finance.Accounts.Any(a => !a.IsArchived);
        var items = _finance.Recurring.Where(r => !r.IsIncome).ToList();
        var active = items.Where(r => !r.IsArchived).ToList();
        var monthly = active.Where(r => _finance.CurrencyOfRecurring(r) == currency).Sum(r => RecurringSchedule.MonthlyCost(r, _finance.ExpectedAmount(r)));
        ListPanel.Children.Add(SectionHeader("Assinaturas e contas fixas", add));

        var totals = new UniformGrid { Columns = _narrow ? 2 : 3, Margin = new Thickness(0, 0, -12, 0) };
        var (start, end) = FinanceService.MonthRange(_year, _month);
        var month = _finance.Occurrences(start, end).Where(o => !o.Item.IsIncome && o.State != OccurrenceState.Skipped).ToList();
        totals.Children.Add(TotalCard("Por mês", monthly, null, "", FinanceColors.Orange, "Soma das assinaturas ativas (valor de um mês)."));
        totals.Children.Add(TotalCard("Por ano", monthly * 12, null, "", FinanceColors.Purple, "O mesmo valor em um ano."));
        totals.Children.Add(TotalCard($"Pagas em {FinanceUi.MonthTitle(_year, _month).Split(' ')[0].ToLower(FinanceUi.PtBr)}",
            month.Where(o => o.State == OccurrenceState.Confirmed).Sum(o => o.Amount), null, "\uE73E", FinanceColors.Green,
            "Quanto já foi pago das assinaturas deste mês.", $"de {FinanceUi.Amount(month.Sum(o => o.Amount), currency)} no mês"));
        ListPanel.Children.Add(totals);

        var byOccurrence = month.ToDictionary(o => o.Item.Id);
        var table = SheetTable(["Pago", "Nome", "Dia", "Pago com", "Categoria", "Valor"], [0, 3, 0, 2, 2, 0],
            items.OrderBy(r => r.IsArchived).ThenBy(r => r.StartDate.Day).Select(r =>
            {
                UIElement status = byOccurrence.TryGetValue(r.Id, out var o) ? PaidBox(o) : Cell(r.IsArchived ? "parada" : "—", FinanceUi.Res("TextTertiaryBrush"));
                var where = r.CardId is { } c ? "Cartão " + _finance.Card(c)?.Name : _finance.Account(r.AccountId)?.Name ?? "?";
                return (new UIElement[]
                {
                    status,
                    Cell(r.Description + (r.IsArchived ? " (parada)" : ""), r.IsArchived ? FinanceUi.Res("TextTertiaryBrush") : null),
                    Cell(r.StartDate.Day.ToString("00", FinanceUi.PtBr)),
                    Cell(where),
                    Cell(_finance.Category(r.CategoryId)?.Name ?? "—"),
                    Cell(FinanceUi.Amount(_finance.ExpectedAmount(r), currency), bold: true),
                }, $"{r.Description}, dia {r.StartDate.Day}, {FinanceUi.Amount(_finance.ExpectedAmount(r), currency)}, {where}", (Action)(() => OpenEditor(new SubscriptionEditor(_finance, r))));
            }),
            "Nenhuma assinatura ainda. Cadastre o que você paga todo mês com certeza (streaming, academia, aluguel, internet…).");
        ListPanel.Children.Add(Columns((Panel($"Lista · {FinanceUi.MonthTitle(_year, _month).ToLower(FinanceUi.PtBr)}", table,
            "Marque \"Pago\" quando pagar no mês exibido; troque o mês no topo para ver outros meses.", null, ("", FinanceColors.Orange)), 1)));
        FooterText.Text = "Assinaturas entram no saldo previsto do mês até você marcar como pagas. Clique numa linha para editar, parar ou excluir.";
    }

    // ---------- Menu "Mais" ----------

    private static readonly (string Label, Tab Tab)[] MoreTabs =
    [
        ("Faturas dos cartões", Tab.Cards),
        ("Orçamento por categoria", Tab.Planning),
        ("Projeções e simulador", Tab.Projections),
        ("Relatórios e backup", Tab.Reports),
        ("Importar extrato", Tab.Import),
        ("Categorias e regras", Tab.Categories),
    ];

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabMore, Placement = PlacementMode.Bottom };
        foreach (var (label, tab) in MoreTabs)
        {
            var item = new MenuItem { Header = label, IsChecked = _tab == tab };
            item.Click += (_, _) =>
            {
                _tab = tab;
                _showArchived = false;
                Render();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
