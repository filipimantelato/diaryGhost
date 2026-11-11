using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Finance;

/// <summary>Criar/editar lançamento: despesa, receita, transferência, estorno (e ajuste já existente).</summary>
internal sealed class TransactionEditor : FinanceForm
{
    private static readonly TransactionKind[] EditableKinds = [TransactionKind.Expense, TransactionKind.Income, TransactionKind.Transfer, TransactionKind.Refund];

    private readonly FinanceService _finance;
    private readonly TransactionDraft _original;
    private readonly bool _isNew;
    private TransactionKind _kind;
    private readonly List<(Button Button, TransactionKind Kind)> _kindChips = [];
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly TextBox _description;
    private readonly TextBlock _accountLabel;
    private readonly ComboBox _account;
    private readonly TextBlock _toLabel;
    private readonly ComboBox _to;
    private readonly TextBlock _categoryLabel;
    private readonly ComboBox _category;
    private readonly TextBlock _refundLabel;
    private readonly ComboBox _refundOf;
    private readonly TextBlock _methodLabel;
    private readonly ComboBox _method;
    private readonly ComboBox _status;
    private readonly TextBox _tags;
    private readonly TextBox _notes;
    private readonly TextBlock _amountHint;

    public TransactionEditor(FinanceService finance, TransactionDraft draft, bool isNew)
        : base(isNew ? "Novo lançamento" : draft.Kind == TransactionKind.Adjustment ? "Ajuste de saldo" : "Editar lançamento")
    {
        _finance = finance;
        _original = draft;
        _isNew = isNew;
        _kind = draft.Kind;

        if (draft.Kind != TransactionKind.Adjustment)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetName(chips, "Tipo de lançamento");
            foreach (var kind in EditableKinds)
            {
                // Transferência não troca de tipo depois de criada (e vice-versa): as partidas são diferentes.
                if (!isNew && (kind == TransactionKind.Transfer) != (draft.Kind == TransactionKind.Transfer))
                {
                    continue;
                }
                var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = FinanceUi.Kind(kind), Margin = new Thickness(0, 0, 6, 6) };
                chip.Click += (_, _) =>
                {
                    _kind = kind;
                    Refresh();
                };
                _kindChips.Add((chip, kind));
                chips.Children.Add(chip);
            }
            Add(chips);
        }

        _amount = TextField(draft.Kind == TransactionKind.Adjustment ? "Diferença aplicada ao saldo" : "Valor", draft.Amount == 0 ? "" : Money.Format(draft.Amount, symbol: false), "Valor", 24);
        _amountHint = Hint("");
        _date = TextField("Data", FinanceUi.Date(draft.Date), "Data (dd/mm/aaaa)", 20);
        var dateChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, offset) in new[] { ("Hoje", 0), ("Ontem", -1) })
        {
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = label, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => _date.Text = FinanceUi.Date(_finance.Today.AddDays(offset));
            dateChips.Children.Add(chip);
        }
        Add(dateChips);
        _description = TextField("Descrição", draft.Description, "Descrição", FinanceService.MaxDescriptionLength);

        _accountLabel = Label("Conta");
        _account = new ComboBox { ItemsSource = FinanceUi.AccountChoices(finance, draft.AccountId) };
        AutomationProperties.SetName(_account, "Conta");
        Add(_account);
        Select(_account, draft.AccountId);
        _toLabel = Label("Para a conta");
        _to = new ComboBox { ItemsSource = FinanceUi.AccountChoices(finance, draft.ToAccountId) };
        AutomationProperties.SetName(_to, "Conta de destino");
        Add(_to);
        if (draft.ToAccountId is { } to)
        {
            Select(_to, to);
        }

        _categoryLabel = Label("Categoria");
        _category = new ComboBox();
        AutomationProperties.SetName(_category, "Categoria");
        Add(_category);

        _refundLabel = Label("Estorno de (opcional)");
        _refundOf = new ComboBox();
        AutomationProperties.SetName(_refundOf, "Despesa estornada");
        Add(_refundOf);

        _methodLabel = Label("Forma de pagamento");
        _method = new ComboBox { ItemsSource = Enum.GetValues<PaymentMethod>().Select(m => new Choice<PaymentMethod>(FinanceUi.Method(m), m)).ToList() };
        _method.SelectedIndex = (int)draft.PaymentMethod;
        AutomationProperties.SetName(_method, "Forma de pagamento");
        Add(_method);

        _status = Combo("Situação",
            [new("Confirmado (já entrou ou saiu da conta)", TransactionStatus.Cleared), new("Pendente (agendado, ainda não compensou)", TransactionStatus.Pending)],
            draft.Status, "Situação");
        _tags = TextField("Etiquetas (separadas por vírgula)", string.Join(", ", draft.Tags), "Etiquetas", 400);
        _notes = TextField(draft.Kind == TransactionKind.Adjustment ? "Motivo do ajuste" : "Observações", draft.Notes ?? "", "Observações", FinanceService.MaxNotesLength, multiline: true);

        Button("Salvar", "AccentButton", (_, _) => Save(again: false), isDefault: true);
        if (isNew)
        {
            Button("Salvar e novo", "SecondaryButton", (_, _) => Save(again: true));
        }
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && draft.Id is { } id)
        {
            DeleteButton("Excluir lançamento", (_, _) =>
            {
                Deleted?.Invoke(_finance.Delete(id));
                Finish();
            });
        }
        Loaded += (_, _) => (_amount.IsEnabled ? _amount : _description).Focus();
        Refresh();
    }

    /// <summary>Lançamento excluído pelo editor (a página oferece "desfazer").</summary>
    public event Action<DeletedTransaction>? Deleted;

    private static void Select(ComboBox combo, Guid id)
    {
        if (combo.ItemsSource is List<Choice<Guid>> items)
        {
            combo.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Value == id));
        }
    }

    private void Refresh()
    {
        foreach (var (button, kind) in _kindChips)
        {
            button.DataContext = new ChipState(kind == _kind);
        }
        var transfer = _kind == TransactionKind.Transfer;
        var adjustment = _kind == TransactionKind.Adjustment;
        _accountLabel.Text = transfer ? "Da conta" : "Conta";
        foreach (var e in new UIElement[] { _toLabel, _to })
        {
            e.Visibility = transfer ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var e in new UIElement[] { _categoryLabel, _category })
        {
            e.Visibility = transfer || adjustment ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var e in new UIElement[] { _methodLabel, _method })
        {
            e.Visibility = transfer || adjustment ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var e in new UIElement[] { _refundLabel, _refundOf })
        {
            e.Visibility = _kind == TransactionKind.Refund ? Visibility.Visible : Visibility.Collapsed;
        }

        var currentCategory = Selected<Guid?>(_category) ?? _original.CategoryId;
        var categoryKind = _kind == TransactionKind.Income ? CategoryKind.Income : CategoryKind.Expense;
        var categories = FinanceUi.CategoryChoices(_finance, categoryKind, _kind == TransactionKind.Refund ? "Mesma da despesa / sem categoria" : "Sem categoria", _original.CategoryId);
        _category.ItemsSource = categories;
        _category.SelectedIndex = Math.Max(0, categories.FindIndex(c => c.Value == currentCategory));

        if (_kind == TransactionKind.Refund && _refundOf.ItemsSource is null)
        {
            // Despesas dos últimos 120 dias (mais a já escolhida), da mais recente para a mais antiga.
            var since = _finance.Today.AddDays(-120);
            var expenses = _finance.Transactions
                .Where(t => t.Kind == TransactionKind.Expense && (t.Date >= since || t.Id == _original.RefundOfId))
                .OrderByDescending(t => t.Date).Take(200)
                .Select(t => new Choice<Guid?>($"{FinanceUi.Date(t.Date)} · {t.Description} · {FinanceUi.Amount(t.Amount)}", t.Id));
            List<Choice<Guid?>> items = [new("Nenhuma (reembolso avulso)", null), .. expenses];
            _refundOf.ItemsSource = items;
            _refundOf.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Value == _original.RefundOfId));
        }

        _amountHint.Text = _kind switch
        {
            TransactionKind.Transfer => "Transferências entre suas contas não contam como receita nem despesa.",
            TransactionKind.Refund => "O estorno entra na conta e reduz o gasto da categoria (não é receita).",
            TransactionKind.Adjustment => "Use sinal: \"-10,00\" diminui o saldo. Ajustes não contam como receita nem despesa.",
            _ => "Digite só números: 12,50 ou 1.234,56.",
        };
    }

    private TransactionDraft? Build(out string? error)
    {
        error = null;
        if (!Money.TryParse(_amount.Text, out var amount, Selected<Guid>(_account) is var acc && _finance.Account(acc) is { } a ? a.Currency : _finance.BaseCurrency))
        {
            error = "Valor inválido. Exemplos: 12,50 · 1.234,56.";
            return null;
        }
        if (_kind != TransactionKind.Adjustment && amount < 0)
        {
            error = "Informe o valor sem sinal; o tipo (despesa, receita…) define se entra ou sai.";
            return null;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            error = "Data inválida. Use dd/mm/aaaa.";
            return null;
        }
        var draft = _original with
        {
            Kind = _kind,
            Amount = amount,
            Date = date,
            Description = _description.Text,
            AccountId = Selected<Guid>(_account),
            ToAccountId = _kind == TransactionKind.Transfer ? Selected<Guid>(_to) is var to && to != Guid.Empty ? to : null : null,
            CategoryId = _kind is TransactionKind.Transfer or TransactionKind.Adjustment ? null : Selected<Guid?>(_category),
            RefundOfId = _kind == TransactionKind.Refund ? Selected<Guid?>(_refundOf) : null,
            PaymentMethod = Selected<PaymentMethod>(_method),
            Status = Selected<TransactionStatus>(_status),
            Tags = _tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Notes = _notes.Text,
        };
        error = _finance.Validate(draft);
        return error is null ? draft : null;
    }

    private void Save(bool again)
    {
        if (Build(out var error) is not { } draft)
        {
            ShowError(error);
            return;
        }
        try
        {
            _finance.Save(draft);
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
            return;
        }
        if (again)
        {
            // Mantém tipo, data, conta e situação para lançar vários seguidos.
            ShowError(null);
            _amount.Clear();
            _description.Clear();
            _tags.Clear();
            _notes.Clear();
            _amount.Focus();
            return;
        }
        Finish();
    }

    public bool IsNew => _isNew;
}

/// <summary>Criar/editar conta ou carteira.</summary>
internal sealed class AccountEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly FinancialAccount _original;
    private readonly TextBox _name;
    private readonly ComboBox _kind;
    private readonly TextBox _institution;
    private readonly ComboBox _currency;
    private readonly TextBox _opening;
    private readonly TextBox _openingDate;
    private readonly CheckBox _available;
    private readonly Func<string> _color;

    public AccountEditor(FinanceService finance, FinancialAccount account, bool isNew)
        : base(isNew ? "Nova conta" : "Editar conta")
    {
        _finance = finance;
        _original = account;
        _name = TextField("Nome", account.Name, "Nome da conta", FinanceService.MaxNameLength);
        _kind = Combo("Tipo", [.. Enum.GetValues<AccountKind>().Select(k => new Choice<AccountKind>(FinanceUi.Kind(k), k))], account.Kind, "Tipo de conta");
        _kind.SelectionChanged += (_, _) =>
        {
            if (isNew)
            {
                _available!.IsChecked = Selected<AccountKind>(_kind) != AccountKind.Investment;
            }
        };
        _institution = TextField("Instituição (opcional)", account.Institution ?? "", "Instituição", FinanceService.MaxNameLength);
        _currency = Combo("Moeda", [.. Money.KnownCurrencies.Select(c => new Choice<string>($"{c} ({Money.Symbol(c)})", c))], account.Currency, "Moeda");
        _opening = TextField("Saldo inicial", Money.Format(account.OpeningBalance, symbol: false), "Saldo inicial", 24);
        _openingDate = TextField("Saldo inicial em", FinanceUi.Date(account.OpeningDate), "Data do saldo inicial", 20);
        Hint("O saldo é calculado a partir deste valor e dos lançamentos. Lançamentos não podem ser anteriores a esta data.");
        Label("Cor");
        var (picker, selected) = FinanceUi.ColorPicker(account.Color);
        Add(picker);
        _color = selected;
        _available = Add(new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            Content = "Entra no saldo disponível",
            IsChecked = account.IncludeInAvailable,
            Margin = new Thickness(0, 14, 0, 0),
        });
        Hint("Desligue para reservas e investimentos que você não usa no dia a dia (continuam no patrimônio).");

        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && finance.CanDeleteAccount(account.Id))
        {
            DeleteButton("Excluir conta", (_, _) =>
            {
                finance.DeleteAccount(account.Id);
                Finish();
            });
        }
        Loaded += (_, _) => _name.Focus();
    }

    private void Save()
    {
        var currency = Selected<string>(_currency) ?? _finance.BaseCurrency;
        if (!Money.TryParse(_opening.Text, out var opening, currency))
        {
            ShowError("Saldo inicial inválido. Exemplos: 0 · 1.234,56 · -50,00.");
            return;
        }
        if (!FinanceUi.TryParseDate(_openingDate.Text, _finance.Today, out var date))
        {
            ShowError("Data inválida. Use dd/mm/aaaa.");
            return;
        }
        var value = _original with
        {
            Name = _name.Text,
            Kind = Selected<AccountKind>(_kind),
            Institution = _institution.Text,
            Currency = currency,
            OpeningBalance = opening,
            OpeningDate = date,
            Color = _color(),
            IncludeInAvailable = _available.IsChecked == true,
        };
        if (_finance.Validate(value) is { } error)
        {
            ShowError(error);
            return;
        }
        _finance.SaveAccount(value);
        Finish();
    }
}

/// <summary>Conferir o saldo com o extrato: gera um lançamento de ajuste com a diferença.</summary>
internal sealed class AdjustBalanceEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly FinancialAccount _account;
    private readonly TextBox _target;
    private readonly TextBox _date;
    private readonly TextBox _notes;
    private readonly TextBlock _preview;

    public AdjustBalanceEditor(FinanceService finance, FinancialAccount account)
        : base($"Ajustar saldo · {account.Name}")
    {
        _finance = finance;
        _account = account;
        var today = finance.Today;
        Hint($"Saldo calculado hoje: {Money.Format(finance.Balance(account.Id, today), account.Currency)} (só confirmados).");
        _target = TextField("Saldo real (do extrato)", "", "Saldo real", 24);
        _date = TextField("Em", FinanceUi.Date(today), "Data do saldo", 20);
        _preview = Hint("");
        _notes = TextField("Motivo", "Conferido com o extrato", "Motivo do ajuste", FinanceService.MaxNotesLength, multiline: true);
        _target.TextChanged += (_, _) => Preview();
        _date.TextChanged += (_, _) => Preview();
        Button("Ajustar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => _target.Focus();
    }

    private void Preview()
    {
        if (Money.TryParse(_target.Text, out var target, _account.Currency) && FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date) && date >= _account.OpeningDate)
        {
            var diff = target - _finance.Balance(_account.Id, date);
            _preview.Text = diff == 0 ? "O saldo já está certo; nada a ajustar." : $"Será lançado um ajuste de {Money.Format(diff, _account.Currency, showPlus: true)} (não conta como receita nem despesa).";
        }
        else
        {
            _preview.Text = "";
        }
    }

    private void Save()
    {
        if (!Money.TryParse(_target.Text, out var target, _account.Currency))
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
            _finance.AdjustBalanceTo(_account.Id, date, target, _notes.Text);
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
            return;
        }
        Finish();
    }
}

/// <summary>Criar/editar categoria ou subcategoria.</summary>
internal sealed class CategoryEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly TransactionCategory _original;
    private readonly TextBox _name;
    private readonly ComboBox _kind;
    private readonly ComboBox _parent;
    private readonly Func<string> _color;

    public CategoryEditor(FinanceService finance, TransactionCategory category, bool isNew)
        : base(isNew ? (category.ParentId is null ? "Nova categoria" : "Nova subcategoria") : "Editar categoria")
    {
        _finance = finance;
        _original = category;
        _name = TextField("Nome", category.Name, "Nome da categoria", FinanceService.MaxNameLength);
        _kind = Combo("Tipo", [new("Despesa", CategoryKind.Expense), new("Receita", CategoryKind.Income)], category.Kind, "Tipo de categoria");
        _parent = Combo("Dentro de", Parents(category.Kind), category.ParentId, "Categoria principal");
        _kind.SelectionChanged += (_, _) =>
        {
            _parent.ItemsSource = Parents(Selected<CategoryKind>(_kind));
            _parent.SelectedIndex = 0;
        };
        Label("Cor");
        var (picker, selected) = FinanceUi.ColorPicker(category.Color);
        Add(picker);
        _color = selected;
        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && finance.CanDeleteCategory(category.Id))
        {
            DeleteButton("Excluir categoria", (_, _) =>
            {
                finance.DeleteCategory(category.Id);
                Finish();
            });
        }
        Loaded += (_, _) => _name.Focus();
    }

    private List<Choice<Guid?>> Parents(CategoryKind kind) =>
        [new("Nenhuma (categoria principal)", null), .. _finance.OrderedCategories(kind).Where(c => c.ParentId is null && c.Id != _original.Id).Select(c => new Choice<Guid?>(c.Name, c.Id))];

    private void Save()
    {
        var value = _original with { Name = _name.Text, Kind = Selected<CategoryKind>(_kind), ParentId = Selected<Guid?>(_parent), Color = _color() };
        if (_finance.Validate(value) is { } error)
        {
            ShowError(error);
            return;
        }
        _finance.SaveCategory(value);
        Finish();
    }
}
