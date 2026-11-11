using System.Windows;
using System.Windows.Controls;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Finance;

/// <summary>Criar/editar cartão de crédito. Mudar fechamento/vencimento vale a partir do próximo ciclo.</summary>
internal sealed class CardEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly CreditCard _original;
    private readonly bool _isNew;
    private readonly TextBox _name;
    private readonly TextBox _institution;
    private readonly TextBox _network;
    private readonly TextBox _digits;
    private readonly TextBox _limit;
    private readonly TextBox _closing;
    private readonly TextBox _due;
    private readonly ComboBox _account;
    private readonly CheckBox _goesNext;
    private readonly Func<string> _color;

    public CardEditor(FinanceService finance, CreditCard card, bool isNew)
        : base(isNew ? "Novo cartão" : "Editar cartão")
    {
        _finance = finance;
        _original = card;
        _isNew = isNew;
        _name = TextField("Nome", card.Name, "Nome do cartão", FinanceService.MaxNameLength);
        _institution = TextField("Instituição (opcional)", card.Institution ?? "", "Instituição", FinanceService.MaxNameLength);
        _network = TextField("Bandeira (opcional)", card.Network ?? "", "Bandeira", 40);
        _digits = TextField("Últimos 4 dígitos (opcional)", card.LastDigits ?? "", "Últimos 4 dígitos", 4);
        Hint("Só para reconhecer o cartão. Nunca digite o número completo nem o código de segurança.");
        _limit = TextField("Limite", Money.Format(card.Limit, card.Currency, symbol: false), "Limite", 24);
        var rule = card.CurrentRule;
        _closing = TextField("Dia de fechamento", rule.ClosingDay.ToString(FinanceUi.PtBr), "Dia de fechamento", 2);
        _due = TextField("Dia de vencimento", rule.DueDay.ToString(FinanceUi.PtBr), "Dia de vencimento", 2);
        Hint(isNew
            ? "Dias 29 a 31 viram o último dia nos meses mais curtos. O vencimento não é adiado por feriados; corrija numa fatura específica se o banco prorrogar."
            : "Mudar os dias vale a partir do próximo mês: a fatura aberta e as fechadas não mudam, e as parcelas futuras são remanejadas.");
        _goesNext = Add(new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            Content = "Compras no dia do fechamento vão para a fatura seguinte",
            IsChecked = card.ClosingDayGoesNext,
            Margin = new Thickness(0, 12, 0, 0),
        });
        List<Choice<Guid?>> accounts = [.. finance.Accounts.Where(a => !a.IsArchived || a.Id == card.PaymentAccountId)
            .Select(a => new Choice<Guid?>(a.Name + (a.Institution is { Length: > 0 } bank ? $" · {bank}" : ""), a.Id))];
        _account = Combo("Conta do banco deste cartão", accounts, card.PaymentAccountId, "Conta do banco a que o cartão pertence");
        Hint("O cartão aparece junto dessa conta, e a fatura é paga por ela.");
        Label("Cor");
        var (picker, selected) = FinanceUi.ColorPicker(card.Color);
        Add(picker);
        _color = selected;

        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && finance.CanDeleteCard(card.Id))
        {
            DeleteButton("Excluir cartão", (_, _) =>
            {
                finance.DeleteCard(card.Id);
                Finish();
            });
        }
        Loaded += (_, _) => _name.Focus();
    }

    private void Save()
    {
        if (!Money.TryParse(_limit.Text, out var limit, _original.Currency) || limit < 0)
        {
            ShowError("Limite inválido. Exemplo: 5.000,00.");
            return;
        }
        if (!int.TryParse(_closing.Text, out var closing) || !int.TryParse(_due.Text, out var due))
        {
            ShowError("Informe os dias de fechamento e vencimento (1 a 31).");
            return;
        }
        var value = _original with
        {
            Name = _name.Text,
            Institution = _institution.Text,
            Network = _network.Text,
            LastDigits = _digits.Text.Trim(),
            Limit = limit,
            ClosingDayGoesNext = _goesNext.IsChecked == true,
            PaymentAccountId = Selected<Guid?>(_account),
            Color = _color(),
            Rules = _isNew ? [new CardCycleRule(DateOnly.MinValue, closing, due)] : _original.Rules,
        };
        // Os dias novos são conferidos à parte (num cartão existente, eles viram uma regra nova no próximo ciclo).
        var error = _finance.Validate(value) ?? (_isNew ? null : _finance.Validate(value with { Rules = [new CardCycleRule(DateOnly.MinValue, closing, due)] }));
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        try
        {
            _finance.SaveCard(value);
            if (!_isNew)
            {
                _finance.ChangeCycle(value.Id, closing, due);
            }
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
            return;
        }
        Finish();
    }
}

/// <summary>Compra no cartão, à vista ou parcelada, com a prévia das parcelas e faturas.</summary>
internal sealed class PurchaseEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly PurchaseDraft _original;
    private readonly bool _locked;
    private readonly ComboBox _card;
    private readonly TextBox _total;
    private readonly TextBox _installments;
    private readonly TextBox _date;
    private readonly TextBox _description;
    private readonly ComboBox _category;
    private readonly TextBox _tags;
    private readonly TextBox _notes;
    private readonly TextBlock _preview;

    public PurchaseEditor(FinanceService finance, PurchaseDraft draft, bool isNew)
        : base(isNew ? "Compra no cartão" : "Editar compra no cartão")
    {
        _finance = finance;
        _original = draft;
        _locked = !isNew && draft.Id is { } id && finance.IsLocked(id);
        List<Choice<Guid>> cards = [.. finance.Cards.Where(c => !c.IsArchived || c.Id == draft.CardId).Select(c => new Choice<Guid>(c.Name + (c.LastDigits is { } d ? $" ·{d}" : ""), c.Id))];
        _card = Combo("Cartão", cards, draft.CardId, "Cartão");
        var currency = finance.Card(draft.CardId)?.Currency ?? finance.BaseCurrency;
        _total = TextField("Valor total", draft.Total == 0 ? "" : Money.Format(draft.Total, currency, symbol: false), "Valor total da compra", 24);
        _installments = TextField("Parcelas", draft.Installments.ToString(FinanceUi.PtBr), "Número de parcelas", 2);
        _date = TextField("Data da compra", FinanceUi.Date(draft.Date), "Data da compra (dd/mm/aaaa)", 20);
        _preview = Hint("");
        _description = TextField("Descrição", draft.Description, "Descrição", FinanceService.MaxDescriptionLength);
        var categories = FinanceUi.CategoryChoices(finance, CategoryKind.Expense, "Sem categoria", draft.CategoryId);
        _category = Combo("Categoria", categories, draft.CategoryId, "Categoria");
        _tags = TextField("Etiquetas (separadas por vírgula)", string.Join(", ", draft.Tags), "Etiquetas", 400);
        _notes = TextField("Observações", draft.Notes ?? "", "Observações", FinanceService.MaxNotesLength, multiline: true);
        if (_locked)
        {
            foreach (var e in new Control[] { _card, _total, _installments, _date })
            {
                e.IsEnabled = false;
            }
            Hint("Uma parcela já está em fatura fechada: valor, data, cartão e parcelas não mudam mais. Para corrigir valores, registre um estorno.");
        }
        foreach (var box in new[] { _total, _installments, _date })
        {
            box.TextChanged += (_, _) => Preview();
        }
        _card.SelectionChanged += (_, _) => Preview();

        Button("Salvar", "AccentButton", (_, _) => Save(again: false), isDefault: true);
        if (isNew)
        {
            Button("Salvar e nova", "SecondaryButton", (_, _) => Save(again: true));
        }
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && draft.Id is { } purchaseId && !_locked)
        {
            DeleteButton("Excluir compra e todas as parcelas", (_, _) =>
            {
                try
                {
                    finance.DeletePurchase(purchaseId);
                    Finish();
                }
                catch (FinanceValidationException ex)
                {
                    ShowError(ex.Message);
                }
            });
        }
        Loaded += (_, _) => (_locked ? _description : _total).Focus();
        Preview();
    }

    private PurchaseDraft? Build(out string? error)
    {
        error = null;
        var cardId = Selected<Guid>(_card);
        var currency = _finance.Card(cardId)?.Currency ?? _finance.BaseCurrency;
        if (!Money.TryParse(_total.Text, out var total, currency) || total <= 0)
        {
            error = "Valor inválido. Exemplos: 129,90 · 2.400,00.";
            return null;
        }
        if (!int.TryParse(_installments.Text, out var installments))
        {
            error = $"Parcelas: de 1 a {FinanceService.MaxInstallments}.";
            return null;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            error = "Data inválida. Use dd/mm/aaaa.";
            return null;
        }
        var draft = _original with
        {
            CardId = cardId,
            Total = total,
            Installments = installments,
            Date = date,
            Description = _description.Text,
            CategoryId = Selected<Guid?>(_category),
            Tags = _tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Notes = _notes.Text,
        };
        error = _finance.Validate(draft);
        return error is null ? draft : null;
    }

    private void Preview()
    {
        var cardId = Selected<Guid>(_card);
        var currency = _finance.Card(cardId)?.Currency ?? _finance.BaseCurrency;
        if (_finance.Card(cardId) is null || !Money.TryParse(_total.Text, out var total) || total <= 0 ||
            !int.TryParse(_installments.Text, out var n) || n is < 1 or > FinanceService.MaxInstallments || !FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            _preview.Text = "Digite o valor total; as parcelas e as faturas aparecem aqui.";
            return;
        }
        var plan = _finance.Plan(new PurchaseDraft { CardId = cardId, Total = total, Installments = n, Date = date });
        if (plan.Count == 0)
        {
            _preview.Text = "";
            return;
        }
        var first = plan[0];
        var text = n == 1
            ? $"À vista: entra na fatura que fecha em {FinanceUi.Date(first.Closing)} e vence em {FinanceUi.Date(first.Due)}."
            : $"{n}× de {FinanceUi.Amount(plan[^1].Amount, currency)}" + (first.Amount != plan[^1].Amount ? $" (a 1ª de {FinanceUi.Amount(first.Amount, currency)})" : "") +
              $". 1ª parcela na fatura que vence em {FinanceUi.Date(first.Due)}; última em {FinanceUi.Date(plan[^1].Due)}. O limite usado é o total.";
        _preview.Text = text;
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
            _finance.SavePurchase(draft);
        }
        catch (FinanceValidationException ex)
        {
            ShowError(ex.Message);
            return;
        }
        if (again)
        {
            ShowError(null);
            _total.Clear();
            _installments.Text = "1";
            _description.Clear();
            _tags.Clear();
            _notes.Clear();
            _total.Focus();
            return;
        }
        Finish();
    }
}

/// <summary>Pagamento de fatura (total, restante ou outro valor). Não é despesa: só tira o dinheiro da conta.</summary>
internal sealed class PaymentEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly TransactionDraft _original;
    private readonly bool _isNew;
    private readonly ComboBox _card;
    private readonly ComboBox _statement;
    private readonly TextBox _amount;
    private readonly ComboBox _account;
    private readonly TextBox _date;
    private readonly ComboBox _status;
    private readonly WrapPanel _quick = new() { Margin = new Thickness(0, 6, 0, 0) };

    public PaymentEditor(FinanceService finance, TransactionDraft draft, bool isNew)
        : base(isNew ? "Pagar fatura" : "Editar pagamento de fatura")
    {
        _finance = finance;
        _original = draft;
        _isNew = isNew;
        List<Choice<Guid>> cards = [.. finance.Cards.Where(c => !c.IsArchived || c.Id == draft.CardId).Select(c => new Choice<Guid>(c.Name, c.Id))];
        _card = Combo("Cartão", cards, draft.CardId ?? Guid.Empty, "Cartão");
        Label("Fatura");
        _statement = new ComboBox();
        System.Windows.Automation.AutomationProperties.SetName(_statement, "Fatura");
        Add(_statement);
        _amount = TextField("Valor pago", draft.Amount == 0 ? "" : Money.Format(draft.Amount, symbol: false), "Valor pago", 24);
        Add(_quick);
        Hint("Pagamento parcial: o restante passa para a próxima fatura (o app não calcula juros; lance os encargos quando vierem). Pagando a mais, a diferença vira crédito.");
        _account = Combo("Pago com a conta", FinanceUi.AccountChoices(finance, draft.AccountId), draft.AccountId, "Conta");
        _date = TextField("Data do pagamento", FinanceUi.Date(draft.Date), "Data do pagamento (dd/mm/aaaa)", 20);
        _status = Combo("Situação", [new("Confirmado", TransactionStatus.Cleared), new("Agendado (pendente)", TransactionStatus.Pending)], draft.Status, "Situação");
        _card.SelectionChanged += (_, _) => FillStatements(null);
        _statement.SelectionChanged += (_, _) => FillQuick();
        FillStatements(draft.StatementClosing);

        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && draft.Id is { } id)
        {
            DeleteButton("Excluir pagamento", (_, _) =>
            {
                Deleted?.Invoke(finance.Delete(id));
                Finish();
            });
        }
        Loaded += (_, _) => _amount.Focus();
    }

    public event Action<DeletedTransaction>? Deleted;

    private void FillStatements(DateOnly? selected)
    {
        var cardId = Selected<Guid>(_card);
        if (_finance.Card(cardId) is not { } card)
        {
            _statement.ItemsSource = null;
            return;
        }
        var statements = _finance.Statements(cardId).Where(s => s.Status != StatementStatus.Future).Reverse().Take(12).ToList();
        var items = statements.Select(s => new Choice<DateOnly>(
            $"Vence {FinanceUi.Date(s.Due)} · {FinanceUi.StatementStatus(s.Status)} · resta {FinanceUi.Amount(Math.Max(0, s.Remaining), card.Currency)}", s.Closing)).ToList();
        _statement.ItemsSource = items;
        var target = selected ?? _finance.StatementToPay(cardId)?.Closing;
        _statement.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Value == target));
        if (_isNew && card.PaymentAccountId is { } account && _account.ItemsSource is List<Choice<Guid>> accounts)
        {
            _account.SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Value == account));
        }
        FillQuick();
    }

    private void FillQuick()
    {
        _quick.Children.Clear();
        var cardId = Selected<Guid>(_card);
        var closing = Selected<DateOnly>(_statement);
        var statement = _finance.Card(cardId) is null ? null : _finance.Statements(cardId).FirstOrDefault(s => s.Closing == closing);
        if (statement is null)
        {
            return;
        }
        var currency = _finance.Card(cardId)!.Currency;
        void Chip(string label, long value)
        {
            if (value <= 0)
            {
                return;
            }
            var chip = new Button { Style = (Style)Application.Current.FindResource("Chip"), Content = $"{label} {Money.Format(value, currency)}", Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => _amount.Text = Money.Format(value, currency, symbol: false);
            _quick.Children.Add(chip);
        }
        Chip("Restante", statement.Remaining);
        if (statement.AmountDue != statement.Remaining)
        {
            Chip("Total", statement.AmountDue);
        }
    }

    private void Save()
    {
        var cardId = Selected<Guid>(_card);
        var currency = _finance.Card(cardId)?.Currency ?? _finance.BaseCurrency;
        if (!Money.TryParse(_amount.Text, out var amount, currency) || amount <= 0)
        {
            ShowError("Valor inválido.");
            return;
        }
        if (!FinanceUi.TryParseDate(_date.Text, _finance.Today, out var date))
        {
            ShowError("Data inválida. Use dd/mm/aaaa.");
            return;
        }
        var card = _finance.Card(cardId);
        var draft = _original with
        {
            Kind = TransactionKind.Transfer,
            CardId = cardId,
            StatementClosing = Selected<DateOnly>(_statement),
            Amount = amount,
            AccountId = Selected<Guid>(_account),
            Date = date,
            Status = Selected<TransactionStatus>(_status),
            Description = string.IsNullOrWhiteSpace(_original.Description) || _isNew ? $"Pagamento da fatura {card?.Name}" : _original.Description,
        };
        if (_finance.Validate(draft) is { } error)
        {
            ShowError(error);
            return;
        }
        _finance.Save(draft);
        Finish();
    }
}

/// <summary>Estorno no cartão (reduz a dívida e o gasto da categoria), ligado a uma compra quando houver.</summary>
internal sealed class CardRefundEditor : FinanceForm
{
    private readonly FinanceService _finance;
    private readonly TransactionDraft _original;
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly TextBox _description;

    public CardRefundEditor(FinanceService finance, TransactionDraft draft, bool isNew)
        : base(isNew ? "Estorno no cartão" : "Editar estorno no cartão")
    {
        _finance = finance;
        _original = draft;
        var card = finance.Card(draft.CardId);
        Hint($"Cartão {card?.Name}. O estorno entra na fatura do dia em que aparece e reduz o gasto da categoria (não é receita).");
        if (draft.RefundOfId is { } of && finance.Find(of) is { } original)
        {
            var purchase = finance.Purchase(original.PurchaseId);
            Hint($"Estorno de: {original.Description} ({FinanceUi.Amount(purchase?.Total ?? original.Amount, card?.Currency ?? finance.BaseCurrency)}).");
        }
        _amount = TextField("Valor estornado", draft.Amount == 0 ? "" : Money.Format(draft.Amount, symbol: false), "Valor estornado", 24);
        _date = TextField("Data", FinanceUi.Date(draft.Date), "Data (dd/mm/aaaa)", 20);
        _description = TextField("Descrição", draft.Description, "Descrição", FinanceService.MaxDescriptionLength);
        Button("Salvar", "AccentButton", (_, _) => Save(), isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        if (!isNew && draft.Id is { } id)
        {
            DeleteButton("Excluir estorno", (_, _) =>
            {
                Deleted?.Invoke(finance.Delete(id));
                Finish();
            });
        }
        Loaded += (_, _) => _amount.Focus();
    }

    public event Action<DeletedTransaction>? Deleted;

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
        var draft = _original with { Kind = TransactionKind.Refund, Amount = amount, Date = date, Description = _description.Text };
        if (_finance.Validate(draft) is { } error)
        {
            ShowError(error);
            return;
        }
        _finance.Save(draft);
        Finish();
    }
}

/// <summary>Corrigir o vencimento de uma fatura (ex.: o banco prorrogou por feriado).</summary>
internal sealed class StatementDueEditor : FinanceForm
{
    public StatementDueEditor(FinanceService finance, CreditCard card, CardStatement statement)
        : base("Corrigir vencimento")
    {
        Hint($"Fatura de {card.Name} que fecha em {FinanceUi.Date(statement.Closing)}. Vale só para esta fatura.");
        var due = TextField("Novo vencimento", FinanceUi.Date(statement.Due), "Novo vencimento (dd/mm/aaaa)", 20);
        Button("Salvar", "AccentButton", (_, _) =>
        {
            if (!FinanceUi.TryParseDate(due.Text, finance.Today, out var date))
            {
                ShowError("Data inválida. Use dd/mm/aaaa.");
                return;
            }
            try
            {
                finance.SetStatementDue(card.Id, statement.Closing, date);
                Finish();
            }
            catch (FinanceValidationException ex)
            {
                ShowError(ex.Message);
            }
        }, isDefault: true);
        Button("Cancelar", "SecondaryButton", (_, _) => Finish(), isCancel: true);
        Loaded += (_, _) => due.Focus();
    }
}
