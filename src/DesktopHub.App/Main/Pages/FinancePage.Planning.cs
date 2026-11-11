using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>Orçamento por categoria, metas e projeções com simulador.</summary>
public partial class FinancePage
{
    private static int s_horizonMonths;
    private static bool s_variable;
    private static readonly List<SimulationItem> s_simulations = [];
    private static SavedScenario? s_scenario;

    private string Currency => _finance.BaseCurrency;

    private static TextBlock Wrap(TextBlock block)
    {
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    // ---------- Orçamento ----------

    private void RenderBudget()
    {
        var month = _finance.BudgetMonth(_year, _month);
        var monthName = FinanceUi.MonthTitle(_year, _month).Split(' ')[0].ToLower(FinanceUi.PtBr);
        var previous = new DateOnly(_year, _month, 1).AddMonths(-1);
        var withBudget = month.Lines.Select(l => l.Category.Id).ToHashSet();
        var free = _finance.OrderedCategories(CategoryKind.Expense).Where(c => c.ParentId is null && !withBudget.Contains(c.Id)).ToList();

        var add = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Orçamento", IsEnabled = free.Count > 0 };
        AutomationProperties.SetName(add, "Adicionar orçamento para uma categoria");
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var c in free)
            {
                var item = new MenuItem { Header = c.Name };
                item.Click += (_, _) => OpenEditor(new BudgetEditor(_finance, c.Id, _year, _month));
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        var copy = Chip($"Copiar de {previous.ToString("MMMM", FinanceUi.PtBr)}", (_, _) =>
        {
            var count = _finance.CopyBudgetsFromPrevious(_year, _month);
            FooterText.Text = count == 0 ? $"{previous.ToString("MMMM", FinanceUi.PtBr)} não tinha orçamentos." : $"{count} limite(s) copiado(s) para {monthName}.";
        });
        AutomationProperties.SetName(copy, $"Copiar os limites de {previous.ToString("MMMM", FinanceUi.PtBr)} para {monthName}");
        copy.Margin = new Thickness(0, 0, 8, 0);

        var body = new StackPanel();
        if (month.Lines.Count == 0)
        {
            body.Children.Add(Paragraph("Nenhum orçamento ainda. Escolha uma categoria em \"+ Orçamento\" e defina um limite por mês; ele vale para todo mês até você mudar."));
        }
        else
        {
            var total = month.TotalLimit;
            var head = AgendaUi.Text($"{FinanceUi.Amount(month.TotalSpent, Currency)} de {FinanceUi.Amount(total, Currency)} · restam {FinanceUi.Amount(month.TotalRemaining, Currency)}", "TitleText", 20,
                month.TotalRemaining < 0 ? FinanceUi.Res("WarningBrush") : null);
            body.Children.Add(head);
            body.Children.Add(Meter.Build(month.TotalSpent, month.TotalPlanned, total, (Brush)FindResource("AccentBrush"),
                $"Total: {Percent(month.TotalSpent, total)} do orçamento gasto, mais {FinanceUi.Amount(month.TotalPlanned, Currency)} previstos", 12));
            if (month.TotalPlanned > 0)
            {
                body.Children.Add(AgendaUi.Text($"+ {FinanceUi.Amount(month.TotalPlanned, Currency)} em contas e assinaturas previstas (barra clara)", "CaptionText", 11.5));
            }
            foreach (var line in month.Lines)
            {
                body.Children.Add(BudgetRow(line));
            }
        }
        if (month.UnbudgetedSpent != 0)
        {
            body.Children.Add(Wrap(Paragraph($"Fora do orçamento: {FinanceUi.Amount(month.UnbudgetedSpent, Currency)} gastos em categorias sem limite.")));
        }
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal };
        headerRight.Children.Add(copy);
        headerRight.Children.Add(add);
        ListPanel.Children.Add(Columns((Panel($"Orçamento de {monthName}", body,
            "Gasto = despesas confirmadas e pendentes do mês, menos estornos, somando as subcategorias. Parcelas de cartão contam na data de cada parcela. " +
            "Transferências, pagamentos de fatura e ajustes nunca entram. Faixa de atenção (padrão 80%) e estouro (100%).", headerRight), 1)));
        FooterText.Text = "Clique numa categoria para mudar o limite deste mês ou de todos os meses.";
    }

    private static string Percent(long part, long whole) =>
        Money.Percent(part, whole) is { } p ? $"{p.ToString("0", FinanceUi.PtBr)}%" : "—";

    private Grid BudgetRow(BudgetLine line)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6, 8, 6) };
        var title = new DockPanel();
        var stateText = line.State switch
        {
            BudgetState.Over => $"✕ estourou {FinanceUi.Amount(-line.Remaining, Currency)}",
            BudgetState.Warning => $"⚠ {Percent(line.Spent, line.Limit)}",
            _ => Percent(line.Spent, line.Limit),
        };
        var state = AgendaUi.Text(stateText, "CaptionText", 12, line.State == BudgetState.Ok ? null : FinanceUi.Res(line.State == BudgetState.Over ? "DangerBrush" : "WarningBrush"));
        DockPanel.SetDock(state, Dock.Right);
        title.Children.Add(state);
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(FinanceUi.ColorDot(line.Category.Color));
        var label = AgendaUi.Text(" " + line.Category.Name + (line.IsDefault ? "" : " · só este mês"), "BodyText", 13.5);
        label.Margin = new Thickness(6, 0, 0, 0);
        name.Children.Add(label);
        title.Children.Add(name);
        body.Children.Add(title);
        var brush = line.State switch
        {
            BudgetState.Over => FinanceUi.Res("DangerBrush"),
            BudgetState.Warning => FinanceUi.Res("WarningBrush"),
            _ => AgendaUi.ColorBrush(line.Category.Color),
        };
        var summary = $"{line.Category.Name}: {FinanceUi.Amount(line.Spent, Currency)} de {FinanceUi.Amount(line.Limit, Currency)}, {stateText}";
        body.Children.Add(Meter.Build(line.Spent, line.Planned, line.Limit, brush, summary));
        var detail = $"{FinanceUi.Amount(line.Spent, Currency)} de {FinanceUi.Amount(line.Limit, Currency)} · " +
            (line.Remaining >= 0 ? $"restam {FinanceUi.Amount(line.Remaining, Currency)}" : $"passou {FinanceUi.Amount(-line.Remaining, Currency)}") +
            (line.Planned > 0 ? $" · + {FinanceUi.Amount(line.Planned, Currency)} previstos{(line.WillExceed && line.State != BudgetState.Over ? " (vai passar do limite)" : "")}" : "");
        body.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5, line.WillExceed && line.State != BudgetState.Over ? FinanceUi.Res("WarningBrush") : null));
        var actions = new StackPanel();
        var category = line.Category;
        actions.Children.Add(AgendaUi.IconButton("\uE8FD", $"Ver lançamentos de {category.Name} no mês", (_, _) =>
        {
            _tab = Tab.Transactions;
            _ready = false;
            CategoryFilter.SelectedIndex = Math.Max(0, ((List<Choice<Guid?>>)CategoryFilter.ItemsSource).FindIndex(c => c.Value == category.Id));
            _ready = true;
            Render();
        }));
        return ActionRow(new Border { Width = 0 }, body, null, actions, summary, () => OpenEditor(new BudgetEditor(_finance, category.Id, _year, _month)));
    }

    // ---------- Metas ----------

    private void RenderGoals()
    {
        var add = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Meta" };
        add.Click += (_, _) => OpenEditor(new GoalEditor(_finance, _finance.NewGoal(), isNew: true));
        var archived = _finance.Goals.Count(g => g.IsArchived);
        var header = archived > 0
            ? new[] { Chip($"Mostrar arquivadas ({archived})", (_, _) => { _showArchived = !_showArchived; Render(); }, _showArchived), add }
            : [add];
        var reserved = _finance.Reserved(_finance.Today);
        var balance = _finance.AccountsBalance();
        ListPanel.Children.Add(SectionHeader("Metas", header));
        ListPanel.Children.Add(Wrap(AgendaUi.Text($"Reservado em metas: {FinanceUi.Amount(reserved, Currency)} · disponível {FinanceUi.Amount(balance - reserved, Currency)} (saldo em contas {FinanceUi.Amount(balance, Currency)} − reservado)", "CaptionText", 12)));
        var goals = _finance.Goals.Where(g => _showArchived || !g.IsArchived).ToList();
        if (goals.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Nenhuma meta. Crie uma (ex.: reserva de emergência, viagem) e registre o que já guardou e o que pretende guardar."));
        }
        var cards = goals.Select(GoalCard).ToList();
        for (var i = 0; i < cards.Count; i += 2)
        {
            ListPanel.Children.Add(i + 1 < cards.Count ? Columns((cards[i], 1), (cards[i + 1], 1)) : Columns((cards[i], 1), (new Border(), 1)));
        }
        FooterText.Text = "Guardado de fato entra no \"Reservado\" e reduz o disponível; planejado só aparece nas projeções.";
    }

    private Border GoalCard(FinancialGoal goal)
    {
        var p = _finance.Progress(goal);
        var body = new StackPanel();
        body.Children.Add(AgendaUi.Text($"{FinanceUi.Amount(p.Real, Currency)} de {FinanceUi.Amount(goal.Target, Currency)}", "TitleText", 20));
        var color = AgendaUi.ColorBrush(goal.Color);
        body.Children.Add(Meter.Build(p.Real, p.PlannedAhead, goal.Target, color,
            $"{goal.Name}: {Percent(p.Real, goal.Target)} guardado, {Percent(p.Real + p.PlannedAhead, goal.Target)} com o planejado", 12));
        var line = $"{Percent(p.Real, goal.Target)} guardado" + (p.PlannedAhead > 0 ? $" · {Percent(p.Real + p.PlannedAhead, goal.Target)} com {FinanceUi.Amount(p.PlannedAhead, Currency)} planejados" : "");
        body.Children.Add(AgendaUi.Text(line, "CaptionText", 11.5));
        string plan;
        Brush? planBrush = null;
        if (p.Reached)
        {
            plan = "✓ Meta atingida.";
            planBrush = FinanceUi.Res("SuccessBrush");
        }
        else if (p.Late)
        {
            plan = $"⚠ Prazo ({FinanceUi.Date(goal.Deadline!.Value)}) passou: faltam {FinanceUi.Amount(p.Remaining, Currency)}.";
            planBrush = FinanceUi.Res("WarningBrush");
        }
        else if (p.MonthlyNeeded is { } monthly)
        {
            plan = $"Faltam {FinanceUi.Amount(p.Remaining, Currency)} até {FinanceUi.Date(goal.Deadline!.Value)}: guarde {FinanceUi.Amount(monthly, Currency)} por mês ({p.MonthsLeft} {(p.MonthsLeft == 1 ? "mês" : "meses")}).";
        }
        else
        {
            plan = $"Faltam {FinanceUi.Amount(p.Remaining, Currency)} (sem prazo).";
        }
        var planText = Wrap(AgendaUi.Text(plan, "CaptionText", 12, planBrush));
        planText.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(planText);
        var where = goal.AccountId is { } a ? $"Conta da meta: {_finance.Account(a)?.Name}" : "Reserva dentro das contas";
        body.Children.Add(AgendaUi.Text(where + (goal.IsArchived ? " · arquivada" : ""), "CaptionText", 11.5));

        foreach (var c in _finance.Contributions(goal.Id).Take(5))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var contribution = c;
            if (c.Kind == ContributionKind.Planned && !goal.IsArchived)
            {
                var realize = Chip("Realizar", (_, _) => Try(() => _finance.Realize(contribution.Id, contribution.AccountId ?? _finance.Accounts.FirstOrDefault(x => !x.IsArchived && x.Id != goal.AccountId)?.Id)));
                AutomationProperties.SetName(realize, $"Realizar hoje a contribuição planejada de {FinanceUi.Amount(c.Amount, Currency)}");
                actions.Children.Add(realize);
            }
            actions.Children.Add(AgendaUi.IconButton("\uE74D", $"Excluir contribuição de {FinanceUi.Date(c.Date)}" + (c.TransactionId is null ? "" : " (e a transferência)"), (_, _) => Try(() => _finance.DeleteContribution(contribution.Id))));
            row.Children.Add(actions);
            var text = $"{c.Date.ToString("dd/MM/yy", FinanceUi.PtBr)} · {(c.Kind == ContributionKind.Planned ? "planejado" : c.Amount < 0 ? "retirado" : "guardado")} {FinanceUi.Amount(Math.Abs(c.Amount), Currency)}";
            row.Children.Add(AgendaUi.Text(text, "CaptionText", 12, c.Kind == ContributionKind.Planned ? FinanceUi.Res("TextTertiaryBrush") : null));
            AutomationProperties.SetName(row, text);
            body.Children.Add(row);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (!goal.IsArchived)
        {
            var contribute = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Guardar / planejar", Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(contribute, $"Guardar, retirar ou planejar dinheiro para {goal.Name}");
            contribute.Click += (_, _) => OpenEditor(new ContributionEditor(_finance, goal));
            buttons.Children.Add(contribute);
        }
        var edit = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Editar" };
        AutomationProperties.SetName(edit, $"Editar meta {goal.Name}");
        edit.Click += (_, _) => OpenEditor(new GoalEditor(_finance, goal, isNew: false));
        buttons.Children.Add(edit);
        body.Children.Add(buttons);
        return Panel(goal.Name, body, "Real = dinheiro guardado de fato (barra cheia). Planejado = intenção futura (barra clara): nunca entra no Reservado.");
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (FinanceValidationException ex)
        {
            FooterText.Text = ex.Message;
        }
    }

    // ---------- Projeções ----------

    private DateOnly HorizonEnd()
    {
        var today = _finance.Today;
        var last = new DateOnly(today.Year, today.Month, 1).AddMonths(s_horizonMonths + 1).AddDays(-1);
        var max = today.AddDays(FinanceService.MaxProjectionDays);
        return last > max ? max : last;
    }

    private void RenderProjections()
    {
        if (_finance.Accounts.Count == 0)
        {
            ListPanel.Children.Add(Paragraph("Cadastre uma conta para ver projeções."));
            return;
        }
        // Hipóteses que deixaram de valer (data passou, cartão arquivado) saem sem alarde.
        s_simulations.RemoveAll(s => _finance.Validate(s) is not null);
        var horizon = HorizonEnd();
        Projection projection;
        try
        {
            projection = _finance.Project(horizon, s_simulations, s_variable, FinanceUi.SafetyMargin);
        }
        catch (FinanceValidationException ex)
        {
            ListPanel.Children.Add(Paragraph(ex.Message));
            return;
        }

        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(controls, "Período da projeção");
        foreach (var (label, months) in new[] { ("Fim do mês", 0), ("3 meses", 2), ("6 meses", 5), ("12 meses", 11) })
        {
            var chip = Chip(label, (_, _) =>
            {
                s_horizonMonths = months;
                Render();
            }, s_horizonMonths == months);
            chip.Margin = new Thickness(0, 0, 6, 0);
            controls.Children.Add(chip);
        }
        var variable = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            Content = "Incluir média de gastos variáveis",
            IsChecked = s_variable,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(variable, "Incluir a média diária de gastos variáveis dos 3 meses anteriores");
        variable.Checked += (_, _) => { s_variable = true; Render(); };
        variable.Unchecked += (_, _) => { s_variable = false; Render(); };
        controls.Children.Add(variable);
        ListPanel.Children.Add(controls);

        // Gráfico e pontos-chave.
        var chartPanel = new StackPanel();
        chartPanel.Children.Add(new ProjectionChart(projection, Currency) { Margin = new Thickness(0, 10, 0, 0) });
        var accent = (Brush)FindResource("AccentBrush");
        chartPanel.Children.Add(projection.HasSimulation
            ? FinanceCharts.Legend(("Projeção", accent), ("Com a simulação (tracejada)", FinanceUi.Res("WarningBrush")))
            : FinanceCharts.Legend(("Projeção", accent)));
        var low = projection.Lowest;
        var key = $"Hoje: {FinanceUi.Amount(projection.Points[0].Baseline, Currency)} · em {FinanceUi.Date(projection.To)}: {FinanceUi.Amount(projection.Points[^1].Baseline, Currency)} · " +
            $"menor: {FinanceUi.Amount(low.Baseline, Currency)} em {FinanceUi.Date(low.Date)}";
        chartPanel.Children.Add(Wrap(AgendaUi.Text(key, "CaptionText", 12, low.Baseline < 0 ? FinanceUi.Res("WarningBrush") : null)));
        if (projection.HasSimulation)
        {
            var sl = projection.LowestSimulated;
            var tight = projection.TightestMonth;
            var after = $"Com a simulação: em {FinanceUi.Date(projection.To)} {FinanceUi.Amount(projection.Points[^1].Simulated, Currency)} " +
                $"({FinanceUi.Amount(projection.Points[^1].Simulated - projection.Points[^1].Baseline, Currency, showPlus: true)}) · menor {FinanceUi.Amount(sl.Simulated, Currency)} em {FinanceUi.Date(sl.Date)} · " +
                $"mês mais apertado: {FinanceUi.MonthTitle(tight.Year, tight.Month).ToLower(FinanceUi.PtBr)}";
            chartPanel.Children.Add(Wrap(AgendaUi.Text(after, "CaptionText", 12, sl.Simulated < 0 ? FinanceUi.Res("DangerBrush") : FinanceUi.Res("WarningBrush"))));
        }
        var chartCard = Panel($"Disponível projetado até {FinanceUi.Date(projection.To)}", chartPanel,
            "Disponível livre = saldo das contas do disponível (com pendentes e lançamentos futuros) − faturas de cartão no vencimento ± contas, assinaturas e receitas previstas − reservado em metas − contribuições planejadas.");

        // Quanto posso reservar.
        var reservePanel = new StackPanel();
        var canReserve = AgendaUi.Text(FinanceUi.Amount(projection.CanReserve, Currency), "TitleText", 26);
        canReserve.Margin = new Thickness(0, 4, 0, 4);
        reservePanel.Children.Add(canReserve);
        reservePanel.Children.Add(Wrap(AgendaUi.Text($"= menor disponível até {FinanceUi.Date(projection.To)} ({FinanceUi.Amount(projection.LowestSimulated.Simulated, Currency)}{(projection.HasSimulation ? ", com a simulação" : "")}) − margem de segurança ({FinanceUi.Amount(projection.SafetyMargin, Currency)}).", "CaptionText", 12)));
        var marginRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        marginRow.Children.Add(AgendaUi.Text("Margem", "CaptionText", 12));
        var marginBox = new TextBox { Width = 100, Margin = new Thickness(8, 0, 8, 0), Text = Money.Format(FinanceUi.SafetyMargin, Currency, symbol: false) };
        AutomationProperties.SetName(marginBox, "Margem de segurança");
        marginRow.Children.Add(marginBox);
        marginRow.Children.Add(Chip("Aplicar", (_, _) =>
        {
            if (Money.TryParse(marginBox.Text, out var margin) && margin >= 0)
            {
                FinanceUi.SafetyMargin = margin;
            }
            else
            {
                FooterText.Text = "Margem inválida. Exemplo: 500,00.";
            }
        }));
        reservePanel.Children.Add(marginRow);
        var reserveCard = Panel("Quanto posso reservar hoje", reservePanel, "Reservar hoje diminui todos os dias seguintes; por isso o limite é o menor ponto do período, não o saldo de hoje.");
        ListPanel.Children.Add(Columns((chartCard, 2), (reserveCard, 1)));

        // Simulador e hipóteses.
        var simPanel = new StackPanel();
        if (s_scenario is not null)
        {
            simPanel.Children.Add(AgendaUi.Text($"Cenário: {s_scenario.Name} (simulado)", "BodyText", 13));
        }
        if (s_simulations.Count == 0)
        {
            simPanel.Children.Add(Paragraph("Adicione hipóteses como \"compra de R$ 300 hoje\", \"notebook em 12×\" ou \"guardar R$ 500 dia 10\" para ver o antes e o depois. Nada vira lançamento."));
        }
        for (var i = 0; i < s_simulations.Count; i++)
        {
            var sim = s_simulations[i];
            var index = i;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = AgendaUi.IconButton("\uE74D", $"Remover hipótese {i + 1}", (_, _) =>
            {
                s_simulations.RemoveAt(index);
                Render();
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var label = $"{PlanningText.Simulation(sim.Kind)}{(sim.Description.Length > 0 ? $" · {sim.Description}" : "")} · {FinanceUi.Amount(sim.Amount, Currency)}" +
                (sim.Kind == SimulationKind.CardPurchase ? $" em {sim.Installments}× no {_finance.Card(sim.CardId)?.Name}" : "") + $" · {FinanceUi.Date(sim.Date)}";
            row.Children.Add(Wrap(AgendaUi.Text(label, "CaptionText", 12.5)));
            AutomationProperties.SetName(row, label);
            simPanel.Children.Add(row);
        }
        var simButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var addSim = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Hipótese", Margin = new Thickness(0, 0, 8, 6), IsEnabled = s_simulations.Count < FinanceService.MaxSimulationItems };
        addSim.Click += (_, _) => OpenEditor(new SimulationEditor(_finance, item =>
        {
            s_simulations.Add(item);
            Render();
        }));
        simButtons.Children.Add(addSim);
        if (s_simulations.Count > 0)
        {
            var save = Chip("Salvar cenário", (_, _) => OpenEditor(new ScenarioEditor(_finance, [.. s_simulations], s_scenario, saved => s_scenario = saved)));
            save.Margin = new Thickness(0, 0, 6, 6);
            simButtons.Children.Add(save);
            var clear = Chip("Limpar", (_, _) =>
            {
                s_simulations.Clear();
                s_scenario = null;
                Render();
            });
            clear.Margin = new Thickness(0, 0, 6, 6);
            simButtons.Children.Add(clear);
        }
        simPanel.Children.Add(simButtons);
        if (_finance.Scenarios.Count > 0)
        {
            simPanel.Children.Add(AgendaUi.Text("Cenários salvos", "CaptionText", 12));
            var saved = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var scenario in _finance.Scenarios)
            {
                var chip = Chip(scenario.Name, (_, _) =>
                {
                    s_simulations.Clear();
                    s_simulations.AddRange(scenario.Items);
                    s_scenario = scenario;
                    Render();
                }, s_scenario?.Id == scenario.Id);
                AutomationProperties.SetName(chip, $"Carregar cenário {scenario.Name}");
                chip.Margin = new Thickness(0, 0, 4, 6);
                saved.Children.Add(chip);
                var delete = AgendaUi.IconButton("\uE74D", $"Excluir cenário {scenario.Name}", (_, _) =>
                {
                    if (s_scenario?.Id == scenario.Id)
                    {
                        s_scenario = null;
                    }
                    _finance.DeleteScenario(scenario.Id);
                });
                delete.Margin = new Thickness(0, 0, 10, 6);
                saved.Children.Add(delete);
            }
            simPanel.Children.Add(saved);
        }
        if (s_simulations.Count < s_scenario?.Items.Count)
        {
            simPanel.Children.Add(AgendaUi.Text("Algumas hipóteses do cenário já passaram e foram ignoradas.", "CaptionText", 11.5));
        }
        var simCard = Panel("Simulador", simPanel, "Hipóteses temporárias. Não são salvas como lançamentos; um cenário salvo guarda só as hipóteses, sempre marcado como simulado.");

        var a = projection.Assumptions;
        var considers = new List<string>();
        if (a.ScheduledTransactions > 0)
        {
            considers.Add($"{a.ScheduledTransactions} lançamento(s) pendente(s) ou com data futura");
        }
        if (a.PlannedIncome > 0)
        {
            considers.Add($"{a.PlannedIncome} receita(s) prevista(s)");
        }
        if (a.PlannedExpenses > 0)
        {
            considers.Add($"{a.PlannedExpenses} conta(s) e assinatura(s) prevista(s)" + (a.Estimated > 0 ? $" ({a.Estimated} com valor estimado pela média)" : ""));
        }
        if (a.Statements > 0)
        {
            considers.Add($"{a.Statements} fatura(s) de cartão com parcelas e compras já lançadas");
        }
        if (a.Reserved > 0)
        {
            considers.Add($"{FinanceUi.Amount(a.Reserved, Currency)} reservados em metas");
        }
        if (a.PlannedContributions > 0)
        {
            considers.Add($"{a.PlannedContributions} contribuição(ões) planejada(s) para metas");
        }
        if (a.VariableDaily is { } daily)
        {
            considers.Add($"gastos variáveis de {FinanceUi.Amount(daily, Currency)} por dia (média dos 3 meses anteriores, sem recorrências)");
        }
        if (a.Simulations > 0)
        {
            considers.Add($"{a.Simulations} hipótese(s) simulada(s), só na linha tracejada");
        }
        var assumptions = new StackPanel();
        assumptions.Children.Add(Wrap(AgendaUi.Text("Considera: " + (considers.Count == 0 ? "só o saldo atual das contas do disponível." : string.Join("; ", considers) + "."), "BodyText", 13)));
        assumptions.Children.Add(Wrap(Paragraph("Não considera: " + (a.VariableDaily is null ? "gastos do dia a dia ainda não lançados; " : "") +
            "investimentos, contas arquivadas e outras moedas; juros e encargos de cartão.")));
        var assumptionsCard = Panel("Hipóteses da projeção", assumptions, "Lista exata do que entrou no cálculo.");
        ListPanel.Children.Add(Columns((simCard, 1), (assumptionsCard, 1)));

        // Mês a mês.
        if (projection.Months.Count > 1)
        {
            var headers = projection.HasSimulation ? new[] { "Mês", "Menor (com simulação)", "Fim do mês", "Fim com simulação" } : ["Mês", "Menor", "Fim do mês"];
            var rows = projection.Months.Select(m =>
            {
                var name = FinanceUi.MonthTitle(m.Year, m.Month);
                var lowest = $"{FinanceUi.Amount(m.Lowest.Simulated, Currency)} em {m.Lowest.Date.ToString("dd/MM", FinanceUi.PtBr)}";
                return projection.HasSimulation
                    ? new[] { name, lowest, FinanceUi.Amount(m.End.Baseline, Currency), FinanceUi.Amount(m.End.Simulated, Currency) }
                    : [name, lowest, FinanceUi.Amount(m.End.Baseline, Currency)];
            });
            ListPanel.Children.Add(Columns((Panel("Mês a mês", FinanceCharts.Table(headers, rows), "Menor disponível de cada mês e o valor no último dia."), 1)));
        }
        FooterText.Text = "Projeções usam só seus dados locais. Simulações nunca alteram lançamentos nem saldos.";
    }
}
