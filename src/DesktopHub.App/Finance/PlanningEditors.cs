using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Finance;

internal static class PlanningText
{
    public static string Kind(RecurringKind kind) => kind switch
    {
        RecurringKind.Subscription => "Assinatura",
        RecurringKind.Income => "Receita",
        _ => "Conta a pagar",
    };

    public static string Every(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var start = item.StartDate;
        return (item.Frequency, item.Interval) switch
        {
            (RecurringFrequency.Weekly, 1) => $"toda {start.ToString("dddd", FinanceUi.PtBr)}",
            (RecurringFrequency.Weekly, var n) => $"a cada {n} semanas ({start.ToString("dddd", FinanceUi.PtBr)})",
            (RecurringFrequency.Monthly, 1) => start.Day >= 29 ? $"todo dia {start.Day} (ou o último dia do mês)" : $"todo dia {start.Day}",
            (RecurringFrequency.Monthly, var n) => $"a cada {n} meses, dia {start.Day}",
            (RecurringFrequency.Yearly, 1) => $"todo ano em {start.ToString("dd 'de' MMMM", FinanceUi.PtBr)}",
            (_, var n) => $"a cada {n} anos em {start.ToString("dd 'de' MMMM", FinanceUi.PtBr)}",
        };
    }

    public static string State(OccurrenceState state) => state switch
    {
        OccurrenceState.Confirmed => "confirmada",
        OccurrenceState.Skipped => "pulada",
        OccurrenceState.Overdue => "atrasada",
        _ => "prevista",
    };

    public static string Simulation(SimulationKind kind) => kind switch
    {
        SimulationKind.Income => "Receita",
        SimulationKind.CardPurchase => "Compra no cartão",
        SimulationKind.Saving => "Guardar",
        _ => "Gasto",
    };
}

/// <summary>Conta a pagar, assinatura ou receita recorrente.</summary>
internal sealed class RecurringEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly RecurringItem _original;
    private RecurringKind _kind;
    private readonly List<(Button Button, RecurringKind Kind)> _kindChips = [];
    private readonly TextBox _description;
    private readonly TextBox _amount;
    private readonly CheckBox _estimated;
    private readonly TextBlock _payLabel;
    private readonly ComboBox _payWith;
    private readonly ComboBox _category;
    private readonly ComboBox _frequency;
    private readonly TextBox _interval;
    private readonly TextBox _start;
    private readonly TextBox _end;
    private readonly TextBox _remind;
    private readonly TextBox _notes;
    private readonly TextBlock _hint;

    public RecurringEditor(FinanceService finance, RecurringItem item, bool isNew)
        : base(isNew ? "Nova recorrência" : "Editar recorrência")
    {
        _finance = finance;
        _original = item;
        _kind = item.Kind;
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(chips, "Tipo de recorrência");
        foreach (var kind in Enum.GetValues<RecurringKind>())
        {
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = PlanningText.Kind(kind), Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                _kind = kind;
                Refresh();
            };
            _kindChips.Add((chip, kind));
            chips.Children.Add(chip);
        }
        Add(chips);
        _description = TextField("Descrição", item.Description, "Descrição", FinanceService.MaxDescriptionLength);
        _amount = TextField("Valor", item.Amount == 0 ? "" : Money.Format(item.Amount, symbol: false), "Valor", 24);
        _estimated = Add(new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            Content = "Valor varia (prever pela média das 3 últimas)",
            IsChecked = item.IsEstimated,
            Margin = new Thickness(0, 8, 0, 0),
        });
        AutomationProperties.SetName(_estimated, "Valor variável, estimado pela média das 3 últimas ocorrências");
        _hint = Hint("");
        _payLabel = Label("Paga com");
        _payWith = new ComboBox();
        AutomationProperties.SetName(_payWith, "Conta ou cartão");
        Add(_payWith);
        _category = new ComboBox();
        Label("Categoria");
        AutomationProperties.SetName(_category, "Categoria");
        Add(_category);
        _frequency = Combo("Repete", [new("Todo mês", RecurringFrequency.Monthly), new("Toda semana", RecurringFrequency.Weekly), new("Todo ano", RecurringFrequency.Yearly)], item.Frequency, "Frequência");
        _interval = TextField("A cada quantos períodos", item.Interval.ToString(CultureInfo.InvariantCulture), "Intervalo (1 = todo período)", 2);
        _start = TextField("Primeira ocorrência (define o dia)", FinanceUi.Date(item.StartDate), "Primeira ocorrência (dd/mm/aaaa)", 20);
        _end = TextField("Termina em (opcional)", item.EndDate is { } e ? FinanceUi.Date(e) : "", "Data final (opcional, dd/mm/aaaa)", 20);
        _remind = TextField("Avisar quantos dias antes", item.RemindDaysBefore.ToString(CultureInfo.InvariantCulture), "Dias de antecedência do aviso", 2);
        Hint("Os avisos de vencimento chegam numa próxima versão; o valor já fica guardado.");
        _notes = TextField("Observações", item.Notes ?? "", "Observações", FinanceService.MaxNotesLength, multiline: true);

        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew)
        {
            Button(item.IsArchived ? "Retomar" : "Pausar", "SecondaryButton", (_, _) =>
            {
                Run(() => _finance.SetArchived(_original, !_original.IsArchived));
            });
            DeleteButton("Excluir recorrência (lançamentos confirmados ficam)", (_, _) => Run(() => _finance.DeleteRecurring(_original.Id)));
        }
        Loaded += (_, _) => _description.Focus();
        Refresh();
    }

    private void Run(Action action)
    {
        try
        {
            action();
            Finish();
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Refresh()
    {
        foreach (var (button, kind) in _kindChips)
        {
            button.DataContext = new ChipState(kind == _kind);
        }
        var income = _kind == RecurringKind.Income;
        _payLabel.Text = income ? "Entra na conta" : "Paga com";
        var current = Selected<Guid>(_payWith) is var sel && sel != Guid.Empty ? sel : _original.CardId ?? _original.AccountId ?? Guid.Empty;
        var choices = FinanceUi.AccountChoices(_finance, _original.AccountId);
        if (!income)
        {
            choices.AddRange(_finance.Cards.Where(c => !c.IsArchived || c.Id == _original.CardId).Select(c => new Choice<Guid>("Cartão " + c.Name, c.Id)));
        }
        _payWith.ItemsSource = choices;
        _payWith.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Value == current));
        var category = Selected<Guid?>(_category) ?? _original.CategoryId;
        var categories = FinanceUi.CategoryChoices(_finance, income ? CategoryKind.Income : CategoryKind.Expense, "Sem categoria", _original.CategoryId);
        _category.ItemsSource = categories;
        _category.SelectedIndex = Math.Max(0, categories.FindIndex(c => c.Value == category));
        _hint.Text = "Nada é lançado sozinho: cada ocorrência fica prevista até você confirmar. " +
            (_kind == RecurringKind.Subscription ? "No cartão, confirmar cria uma compra à vista na fatura." : "Confirmar cria o lançamento na conta.");
    }

    private void Save()
    {
        var payWith = Selected<Guid>(_payWith);
        var isCard = _finance.Card(payWith) is not null;
        if (!Money.TryParse(_amount.Text, out var amount) || amount <= 0)
        {
            ShowError("Valor inválido. Exemplos: 12,50 · 1.234,56.");
            return;
        }
        if (!int.TryParse(_interval.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval))
        {
            ShowError("Intervalo: um número (1 = todo período).");
            return;
        }
        if (!FinanceUi.TryParseDate(_start.Text, _finance.Today, out var start))
        {
            ShowError("Primeira ocorrência: use dd/mm/aaaa.");
            return;
        }
        DateOnly? end = null;
        if (!string.IsNullOrWhiteSpace(_end.Text))
        {
            if (!FinanceUi.TryParseDate(_end.Text, _finance.Today, out var e))
            {
                ShowError("Data final: use dd/mm/aaaa (ou deixe em branco).");
                return;
            }
            end = e;
        }
        if (!int.TryParse(_remind.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var remind))
        {
            ShowError("Aviso: número de dias.");
            return;
        }
        var item = _original with
        {
            Kind = _kind,
            Description = _description.Text,
            Amount = amount,
            IsEstimated = _estimated.IsChecked == true,
            AccountId = isCard ? null : payWith,
            CardId = isCard ? payWith : null,
            CategoryId = Selected<Guid?>(_category),
            Frequency = Selected<RecurringFrequency>(_frequency),
            Interval = interval,
            StartDate = start,
            EndDate = end,
            RemindDaysBefore = remind,
            Notes = _notes.Text,
        };
        if (_finance.Validate(item) is { } error)
        {
            ShowError(error);
            return;
        }
        Run(() => _finance.SaveRecurring(item));
    }
}

/// <summary>Confirmar (ou pular) uma ocorrência: data e valor reais.</summary>
internal sealed class OccurrenceEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly RecurringOccurrence _occurrence;
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly ComboBox? _account;

    public OccurrenceEditor(FinanceService finance, RecurringOccurrence occurrence)
        : base(occurrence.Item.IsIncome ? "Confirmar recebimento" : "Confirmar pagamento")
    {
        _finance = finance;
        _occurrence = occurrence;
        var item = occurrence.Item;
        Hint($"{item.Description} · {PlanningText.Kind(item.Kind).ToLower(FinanceUi.PtBr)} · prevista para {FinanceUi.Date(occurrence.Date)}");
        _amount = TextField("Valor", Money.Format(occurrence.Amount, symbol: false), "Valor real", 24);
        if (occurrence.IsEstimated)
        {
            Hint("Valor estimado pela média: confira com a conta antes de confirmar.");
        }
        var today = finance.Today;
        _date = TextField(item.IsIncome ? "Recebido em" : "Pago em", FinanceUi.Date(occurrence.Date <= today ? occurrence.Date : today), "Data real (dd/mm/aaaa)", 20);
        if (item.AccountId is { } accountId)
        {
            _account = Combo(item.IsIncome ? "Na conta" : "Da conta", FinanceUi.AccountChoices(finance, accountId), accountId, "Conta");
        }
        else
        {
            Hint($"Entra como compra à vista no cartão {finance.Card(item.CardId)?.Name}.");
        }
        Button("Confirmar", "AccentButton", (_, _) => Confirm(), isDefault: true);
        Button("Pular esta", "SecondaryButton", (_, _) => Run(() => _finance.Skip(item.Id, occurrence.Date)));
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => _amount.Focus();
    }

    private void Run(Action action)
    {
        try
        {
            action();
            Finish();
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Confirm()
    {
        if (!Money.TryParse(_amount.Text, out var amount) || amount <= 0)
        {
            ShowError("Valor inválido.");
            return;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            ShowError("Data inválida. Use dd/mm/aaaa.");
            return;
        }
        Run(() => _finance.Confirm(_occurrence.Item.Id, _occurrence.Date, date, amount, _account is null ? null : Selected<Guid>(_account)));
    }
}

/// <summary>Limite de uma categoria: para todo mês ou só para o mês exibido.</summary>
internal sealed class BudgetEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly Guid _categoryId;
    private readonly DateOnly _month;
    private readonly TextBox _limit;
    private readonly TextBox _warn;
    private readonly CheckBox _onlyThisMonth;

    public BudgetEditor(FinanceService finance, Guid categoryId, int year, int month)
        : base("Orçamento")
    {
        _finance = finance;
        _categoryId = categoryId;
        _month = new DateOnly(year, month, 1);
        var monthName = _month.ToString("MMMM 'de' yyyy", FinanceUi.PtBr);
        var current = finance.BudgetFor(categoryId, year, month);
        var isDefault = current?.Month is null;
        if (finance.Category(categoryId) is { } category)
        {
            Hint($"{category.Name} (com as subcategorias). Conta despesas confirmadas e pendentes, menos estornos.");
        }
        _limit = TextField("Limite por mês", current is { Limit: > 0 } ? Money.Format(current.Limit, symbol: false) : "", "Limite", 24);
        _warn = TextField("Avisar ao atingir (%)", (current?.WarnPercent ?? 80).ToString(CultureInfo.InvariantCulture), "Faixa de atenção em porcentagem", 3);
        _onlyThisMonth = Add(new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            Content = $"Só em {monthName}",
            IsChecked = current is not null && !isDefault,
            Margin = new Thickness(0, 10, 0, 0),
        });
        AutomationProperties.SetName(_onlyThisMonth, $"Valer só em {monthName}");
        Hint("Desligado: vale para todo mês (os ajustes de um mês específico continuam valendo).");
        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (current is not null)
        {
            DeleteButton(isDefault ? "Remover o orçamento padrão desta categoria" : $"Remover o ajuste de {monthName} (volta ao padrão)", (_, _) =>
            {
                _finance.RemoveBudget(categoryId, current.Month);
                Finish();
            });
        }
        Loaded += (_, _) => _limit.Focus();
    }

    private void Save()
    {
        if (!Money.TryParse(_limit.Text, out var limit) || limit < 0)
        {
            ShowError("Limite inválido.");
            return;
        }
        if (!int.TryParse(_warn.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var warn))
        {
            ShowError("Faixa de atenção: um número de 1 a 100.");
            return;
        }
        try
        {
            _finance.SetBudget(_categoryId, _onlyThisMonth.IsChecked == true ? _month : null, limit, warn);
            Finish();
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
        }
    }
}

/// <summary>Meta: nome, alvo, prazo e conta própria (ou reserva dentro de uma conta).</summary>
internal sealed class GoalEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly FinancialGoal _original;
    private readonly TextBox _name;
    private readonly TextBox _target;
    private readonly TextBox _deadline;
    private readonly ComboBox _account;
    private readonly Func<string> _color;

    public GoalEditor(FinanceService finance, FinancialGoal goal, bool isNew)
        : base(isNew ? "Nova meta" : "Editar meta")
    {
        _finance = finance;
        _original = goal;
        _name = TextField("Nome", goal.Name, "Nome da meta", FinanceService.MaxNameLength);
        _target = TextField("Quanto quer juntar", goal.Target == 0 ? "" : Money.Format(goal.Target, symbol: false), "Valor alvo", 24);
        _deadline = TextField("Prazo (opcional)", goal.Deadline is { } d ? FinanceUi.Date(d) : "", "Prazo (dd/mm/aaaa, opcional)", 20);
        List<Choice<Guid?>> accounts = [new("Nenhuma: reservar dentro de uma conta", null), .. FinanceUi.AccountChoices(finance, goal.AccountId).Select(a => new Choice<Guid?>(a.Label, a.Value))];
        _account = Combo("Conta da meta", accounts, goal.AccountId, "Conta própria da meta");
        Hint("Com conta própria (ex.: poupança), guardar vira uma transferência para ela. Sem conta, o dinheiro fica reservado numa conta: o saldo não muda, o disponível sim.");
        Label("Cor");
        var (picker, selected) = FinanceUi.ColorPicker(goal.Color);
        Add(picker);
        _color = selected;
        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew)
        {
            Button(goal.IsArchived ? "Reativar" : "Concluir e arquivar", "SecondaryButton", (_, _) => Run(() => _finance.SetArchived(_original, !_original.IsArchived)));
            DeleteButton("Excluir meta (transferências já feitas ficam)", (_, _) => Run(() => _finance.DeleteGoal(_original.Id)));
        }
        Loaded += (_, _) => _name.Focus();
    }

    private void Run(Action action)
    {
        try
        {
            action();
            Finish();
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Save()
    {
        if (!Money.TryParse(_target.Text, out var target) || target <= 0)
        {
            ShowError("Valor alvo inválido.");
            return;
        }
        DateOnly? deadline = null;
        if (!string.IsNullOrWhiteSpace(_deadline.Text))
        {
            if (!FinanceUi.TryParseDate(_deadline.Text, _finance.Today, out var d))
            {
                ShowError("Prazo: use dd/mm/aaaa (ou deixe em branco).");
                return;
            }
            deadline = d;
        }
        var goal = _original with { Name = _name.Text, Target = target, Deadline = deadline, AccountId = Selected<Guid?>(_account), Color = _color() };
        if (_finance.Validate(goal) is { } error)
        {
            ShowError(error);
            return;
        }
        Run(() => _finance.SaveGoal(goal));
    }
}

/// <summary>Guardar ou retirar (real) ou planejar uma contribuição.</summary>
internal sealed class ContributionEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly FinancialGoal _goal;
    private ContributionKind _kind = ContributionKind.Real;
    private bool _withdraw;
    private readonly List<(Button Button, ContributionKind Kind)> _kindChips = [];
    private readonly List<(Button Button, bool Withdraw)> _directionChips = [];
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly TextBlock _accountLabel;
    private readonly ComboBox _account;
    private readonly TextBox _notes;
    private readonly TextBlock _hint;

    public ContributionEditor(FinanceService finance, FinancialGoal goal)
        : base($"Meta: {goal.Name}")
    {
        _finance = finance;
        _goal = goal;
        var kinds = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(kinds, "Tipo de contribuição");
        foreach (var (label, kind) in new[] { ("Guardado de fato", ContributionKind.Real), ("Planejado", ContributionKind.Planned) })
        {
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = label, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                _kind = kind;
                Refresh();
            };
            _kindChips.Add((chip, kind));
            kinds.Children.Add(chip);
        }
        Add(kinds);
        var directions = new WrapPanel();
        AutomationProperties.SetName(directions, "Guardar ou retirar");
        foreach (var (label, withdraw) in new[] { ("Guardar", false), ("Retirar", true) })
        {
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = label, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                _withdraw = withdraw;
                Refresh();
            };
            _directionChips.Add((chip, withdraw));
            directions.Children.Add(chip);
        }
        Add(directions);
        _amount = TextField("Valor", "", "Valor", 24);
        _date = TextField("Data", FinanceUi.Date(finance.Today), "Data (dd/mm/aaaa)", 20);
        _accountLabel = Label("Conta");
        var accounts = FinanceUi.AccountChoices(finance).Where(a => a.Value != goal.AccountId).ToList();
        _account = new ComboBox { ItemsSource = accounts };
        _account.SelectedIndex = accounts.Count > 0 ? 0 : -1;
        AutomationProperties.SetName(_account, "Conta");
        Add(_account);
        _hint = Hint("");
        _notes = TextField("Observações", "", "Observações", FinanceService.MaxNotesLength);
        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => _amount.Focus();
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (button, kind) in _kindChips)
        {
            button.DataContext = new ChipState(kind == _kind);
        }
        foreach (var (button, withdraw) in _directionChips)
        {
            button.DataContext = new ChipState(withdraw == _withdraw);
        }
        var own = _goal.AccountId is not null;
        var accountName = _finance.Account(_goal.AccountId)?.Name;
        _accountLabel.Text = _kind == ContributionKind.Planned ? "De qual conta (previsto)" : own ? (_withdraw ? "Volta para a conta" : "Sai da conta") : "Reservado na conta";
        _hint.Text = _kind == ContributionKind.Planned
            ? "Planejado é só intenção: entra nas projeções, nunca no \"Reservado\". Depois, use \"Realizar\"."
            : own
                ? $"Cria uma transferência {(_withdraw ? $"de {accountName} para a conta escolhida" : $"da conta escolhida para {accountName}")}. Não é despesa."
                : "Não move dinheiro: separa uma parte do saldo da conta. O saldo continua igual; o disponível diminui.";
    }

    private void Save()
    {
        if (!Money.TryParse(_amount.Text, out var amount) || amount <= 0)
        {
            ShowError("Valor inválido.");
            return;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            ShowError("Data inválida. Use dd/mm/aaaa.");
            return;
        }
        try
        {
            _finance.Contribute(_goal.Id, _kind, date, _withdraw ? -amount : amount, Selected<Guid>(_account) is var a && a != Guid.Empty ? a : null, _notes.Text);
            Finish();
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
        }
    }
}

/// <summary>Hipótese do simulador (nunca vira lançamento).</summary>
internal sealed class SimulationEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly Action<SimulationItem> _add;
    private SimulationKind _kind = SimulationKind.Expense;
    private readonly List<(Button Button, SimulationKind Kind)> _chips = [];
    private readonly TextBox _description;
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly TextBlock _cardLabel;
    private readonly ComboBox _card;
    private readonly TextBlock _installmentsLabel;
    private readonly TextBox _installments;

    public SimulationEditor(FinanceService finance, Action<SimulationItem> add)
        : base("Nova hipótese")
    {
        _finance = finance;
        _add = add;
        Hint("Simulações não viram lançamentos e não mudam nenhum saldo real.");
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(chips, "Tipo de hipótese");
        var kinds = finance.Cards.Any(c => !c.IsArchived) ? Enum.GetValues<SimulationKind>() : [SimulationKind.Expense, SimulationKind.Income, SimulationKind.Saving];
        foreach (var kind in kinds)
        {
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = PlanningText.Simulation(kind), Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                _kind = kind;
                Refresh();
            };
            _chips.Add((chip, kind));
            chips.Children.Add(chip);
        }
        Add(chips);
        _description = TextField("Descrição (opcional)", "", "Descrição", FinanceService.MaxDescriptionLength);
        _amount = TextField("Valor total", "", "Valor", 24);
        _date = TextField("Data", FinanceUi.Date(finance.Today), "Data (dd/mm/aaaa)", 20);
        _cardLabel = Label("Cartão");
        var cards = finance.Cards.Where(c => !c.IsArchived).Select(c => new Choice<Guid>(c.Name, c.Id)).ToList();
        _card = new ComboBox { ItemsSource = cards, SelectedIndex = cards.Count > 0 ? 0 : -1 };
        AutomationProperties.SetName(_card, "Cartão");
        Add(_card);
        _installmentsLabel = Label("Parcelas");
        _installments = new TextBox { Text = "1", MaxLength = 2 };
        AutomationProperties.SetName(_installments, "Número de parcelas");
        Add(_installments);
        Button("Adicionar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => _amount.Focus();
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (button, kind) in _chips)
        {
            button.DataContext = new ChipState(kind == _kind);
        }
        var card = _kind == SimulationKind.CardPurchase ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in new UIElement[] { _cardLabel, _card, _installmentsLabel, _installments })
        {
            e.Visibility = card;
        }
    }

    private void Save()
    {
        if (!Money.TryParse(_amount.Text, out var amount) || amount <= 0)
        {
            ShowError("Valor inválido.");
            return;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            ShowError("Data inválida. Use dd/mm/aaaa.");
            return;
        }
        var installments = 1;
        if (_kind == SimulationKind.CardPurchase && !int.TryParse(_installments.Text, NumberStyles.None, CultureInfo.InvariantCulture, out installments))
        {
            ShowError("Parcelas: um número de 1 a 72.");
            return;
        }
        var item = new SimulationItem
        {
            Kind = _kind,
            Description = _description.Text.Trim(),
            Amount = amount,
            Date = date,
            Installments = installments,
            CardId = _kind == SimulationKind.CardPurchase ? Selected<Guid>(_card) : null,
        };
        if (_finance.Validate(item) is { } error)
        {
            ShowError(error);
            return;
        }
        _add(item);
        Finish();
    }
}

/// <summary>Nome para salvar o cenário atual do simulador.</summary>
internal sealed class ScenarioEditor : FinanceForm
{
    public ScenarioEditor(FinanceService finance, IReadOnlyList<SimulationItem> items, SavedScenario? existing, Action<SavedScenario> saved)
        : base("Salvar cenário")
    {
        Hint("O cenário guarda só as hipóteses, sempre marcado como simulado. Nenhum lançamento é criado.");
        var name = TextField("Nome", existing?.Name ?? "", "Nome do cenário", FinanceService.MaxNameLength);
        Button("Salvar", "AccentButton", (_, _) =>
        {
            try
            {
                saved(finance.SaveScenario(existing?.Id, name.Text, items));
                Finish();
            }
            catch (FinanceValidationException ex)
            {
                ShowError(ex.Message);
            }
        }, isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => name.Focus();
    }
}
