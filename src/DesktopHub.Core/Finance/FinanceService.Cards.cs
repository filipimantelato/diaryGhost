namespace DesktopHub.Core.Finance;

/// <summary>O que o usuário preenche para uma compra no cartão.</summary>
public sealed record PurchaseDraft
{
    public Guid? Id { get; init; }
    public Guid CardId { get; init; }
    public required DateOnly Date { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Valor total da compra (positivo), dividido em <see cref="Installments"/> parcelas.</summary>
    public long Total { get; init; }
    public int Installments { get; init; } = 1;
    public Guid? CategoryId { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Confirmação de uma assinatura cobrada no cartão.</summary>
    public Guid? RecurringId { get; init; }
    public DateOnly? OccurrenceDate { get; init; }
}

/// <summary>Uma parcela planejada: número, valor, data de competência e fatura (fechamento e vencimento).</summary>
public sealed record InstallmentPlan(int Number, long Amount, DateOnly Date, DateOnly Closing, DateOnly Due);

/// <summary>
/// Cartões de crédito (ADR-030): compra = despesa (na data de cada parcela) + dívida no cartão; pagamento da fatura
/// = transferência da conta para o cartão, sem nova despesa. Faturas são calculadas, nunca digitadas.
/// </summary>
public sealed partial class FinanceService
{
    public const int MaxInstallments = 72;

    private List<CreditCard> _cards = [];
    private List<CardPurchase> _purchases = [];
    private List<StatementOverride> _overrides = [];

    public IReadOnlyList<CreditCard> Cards
    {
        get
        {
            lock (_gate)
            {
                return [.. _cards.OrderBy(c => c.IsArchived).ThenBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public IReadOnlyList<CardPurchase> Purchases
    {
        get
        {
            lock (_gate)
            {
                return [.. _purchases];
            }
        }
    }

    public CreditCard? Card(Guid? id)
    {
        if (id is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _cards.FirstOrDefault(c => c.Id == id);
        }
    }

    public CardPurchase? Purchase(Guid? id)
    {
        if (id is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _purchases.FirstOrDefault(p => p.Id == id);
        }
    }

    public IReadOnlyList<FinanceTransaction> Installments(Guid purchaseId) =>
        [.. Transactions.Where(t => t.PurchaseId == purchaseId).OrderBy(t => t.InstallmentNumber)];

    // ---------- Cadastro ----------

    public CreditCard NewCard() => new()
    {
        Id = Guid.CreateVersion7(Now),
        Name = "",
        Currency = BaseCurrency,
        Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)],
        PaymentAccountId = Accounts.FirstOrDefault(a => !a.IsArchived && a.Kind == AccountKind.Checking)?.Id ?? Accounts.FirstOrDefault(a => !a.IsArchived)?.Id,
        SortOrder = Cards.Count,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public string? Validate(CreditCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(card.Name))
        {
            return "Dê um nome ao cartão.";
        }
        if (card.Name.Trim().Length > MaxNameLength || (card.Institution?.Length ?? 0) > MaxNameLength || (card.Network?.Length ?? 0) > 40)
        {
            return $"Nome e instituição têm no máximo {MaxNameLength} caracteres.";
        }
        if (card.LastDigits is { Length: > 0 } digits && (digits.Length != 4 || !digits.All(char.IsAsciiDigit)))
        {
            return "Últimos dígitos: exatamente 4 números (ou deixe em branco). Nunca o número completo.";
        }
        if (card.Limit < 0 || card.Limit > Money.MaxAbsMinor)
        {
            return "Limite inválido.";
        }
        if (!Money.IsKnownCurrency(card.Currency))
        {
            return "Moeda não suportada.";
        }
        if (card.Rules.Count == 0 || card.Rules.Any(r => r.ClosingDay is < 1 or > 31 || r.DueDay is < 1 or > 31))
        {
            return "Dias de fechamento e vencimento vão de 1 a 31.";
        }
        if (card.Rules.Any(r => r.ClosingDay == r.DueDay))
        {
            return "O vencimento precisa ser num dia diferente do fechamento.";
        }
        if (card.PaymentAccountId is { } account && Account(account) is null)
        {
            return "Conta de pagamento não encontrada.";
        }
        if (card.PaymentAccountId is null && Accounts.Any(a => !a.IsArchived))
        {
            return "Escolha a conta do banco a que o cartão pertence.";
        }
        var existing = Card(card.Id);
        if (existing is not null && existing.Currency != card.Currency && Transactions.Any(t => t.CardId == card.Id))
        {
            return "A moeda de um cartão com lançamentos não pode mudar.";
        }
        return null;
    }

    public CreditCard SaveCard(CreditCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (Validate(card) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var value = card with
        {
            Name = card.Name.Trim(),
            Institution = string.IsNullOrWhiteSpace(card.Institution) ? null : card.Institution.Trim(),
            Network = string.IsNullOrWhiteSpace(card.Network) ? null : card.Network.Trim(),
            LastDigits = string.IsNullOrWhiteSpace(card.LastDigits) ? null : card.LastDigits.Trim(),
            UpdatedAt = Now,
        };
        _store.SaveCard(value);
        lock (_gate)
        {
            _cards.RemoveAll(c => c.Id == value.Id);
            _cards.Add(value);
        }
        Changed();
        return value;
    }

    /// <summary>
    /// Muda fechamento/vencimento a partir do mês seguinte ao da fatura aberta hoje (as fechadas e a aberta não
    /// mudam; o ciclo de transição fica mais curto ou mais longo). Lançamentos já atribuídos a faturas futuras são
    /// remanejados pelo novo ciclo. Devolve quantos foram movidos.
    /// </summary>
    public int ChangeCycle(Guid cardId, int closingDay, int dueDay)
    {
        var card = Card(cardId) ?? throw new FinanceValidationException("Cartão não encontrado.");
        var current = card.CurrentRule;
        if (current.ClosingDay == closingDay && current.DueDay == dueDay)
        {
            return 0;
        }
        var openClosing = StatementCalendar.ClosingFor(card, Today);
        var from = new DateOnly(openClosing.Year, openClosing.Month, 1).AddMonths(1);
        var updated = card with { Rules = [.. card.Rules.Where(r => r.EffectiveFrom < from), new CardCycleRule(from, closingDay, dueDay)] };
        if (Validate(updated) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var moved = new List<FinanceTransaction>();
        foreach (var t in Transactions.Where(t => t.CardId == cardId && t.StatementClosing > openClosing))
        {
            DateOnly closing;
            if (t.PurchaseId is { } pid && Purchase(pid) is { } purchase && t.InstallmentNumber is { } k)
            {
                closing = StatementCalendar.Advance(updated, StatementCalendar.ClosingFor(updated, purchase.Date), k - 1);
            }
            else
            {
                closing = StatementCalendar.ClosingFor(updated, t.Date);
            }
            if (closing <= openClosing)
            {
                closing = StatementCalendar.NextClosing(updated, openClosing);
            }
            if (closing != t.StatementClosing)
            {
                moved.Add(t with { Postings = [.. t.Postings.Select(p => p.IsCard ? p with { StatementClosing = closing } : p)], UpdatedAt = Now });
            }
        }
        if (moved.Count > 0)
        {
            _store.SaveTransactions(moved);
        }
        SaveCard(updated);
        lock (_gate)
        {
            foreach (var t in moved)
            {
                _transactions.RemoveAll(x => x.Id == t.Id);
                _transactions.Add(t);
            }
        }
        Changed();
        return moved.Count;
    }

    public void SetArchived(CreditCard card, bool archived)
    {
        ArgumentNullException.ThrowIfNull(card);
        SaveCard(card with { ArchivedAt = archived ? Now : null });
    }

    public bool CanDeleteCard(Guid id) => !Transactions.Any(t => t.CardId == id) && !Purchases.Any(p => p.CardId == id) && !Recurring.Any(r => r.CardId == id);

    public void DeleteCard(Guid id)
    {
        if (!CanDeleteCard(id))
        {
            throw new FinanceValidationException("O cartão tem lançamentos. Arquive-o para escondê-lo sem perder o histórico.");
        }
        _store.DeleteCard(id);
        lock (_gate)
        {
            _cards.RemoveAll(c => c.Id == id);
        }
        Changed();
    }

    /// <summary>Corrige o vencimento de uma fatura específica (ex.: prorrogado por feriado).</summary>
    public void SetStatementDue(Guid cardId, DateOnly closing, DateOnly due)
    {
        if (Card(cardId) is null)
        {
            throw new FinanceValidationException("Cartão não encontrado.");
        }
        if (due <= closing || due > closing.AddDays(60))
        {
            throw new FinanceValidationException("O vencimento precisa ser depois do fechamento (até 60 dias).");
        }
        var value = new StatementOverride(cardId, closing, due);
        _store.SaveStatementOverride(value);
        lock (_gate)
        {
            _overrides.RemoveAll(o => o.CardId == cardId && o.Closing == closing);
            _overrides.Add(value);
        }
        Changed();
    }

    public DateOnly DueDate(CreditCard card, DateOnly closing)
    {
        lock (_gate)
        {
            return StatementCalendar.Due(card, closing, _overrides);
        }
    }

    // ---------- Compras ----------

    public PurchaseDraft NewPurchase(Guid? cardId = null) => new()
    {
        CardId = cardId ?? Cards.FirstOrDefault(c => !c.IsArchived)?.Id ?? Guid.Empty,
        Date = Today,
    };

    public static PurchaseDraft DraftFrom(CardPurchase p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new()
        {
            Id = p.Id,
            CardId = p.CardId,
            Date = p.Date,
            Description = p.Description,
            Total = p.Total,
            Installments = p.Installments,
            CategoryId = p.CategoryId,
            Notes = p.Notes,
            Tags = p.Tags,
        };
    }

    /// <summary>
    /// Parcelas da compra: o resto da divisão vai na 1ª (soma sempre igual ao total); a parcela k tem competência
    /// k−1 meses depois da compra e cai k−1 faturas depois da primeira.
    /// </summary>
    public IReadOnlyList<InstallmentPlan> Plan(PurchaseDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var card = Card(draft.CardId) ?? throw new FinanceValidationException("Escolha o cartão.");
        if (draft.Installments is < 1 or > MaxInstallments || draft.Total <= 0)
        {
            return [];
        }
        var amounts = Money.Split(draft.Total, draft.Installments);
        var closing = StatementCalendar.ClosingFor(card, draft.Date);
        var plan = new List<InstallmentPlan>(draft.Installments);
        for (var k = 1; k <= draft.Installments; k++)
        {
            plan.Add(new InstallmentPlan(k, amounts[k - 1], draft.Date.AddMonths(k - 1), closing, DueDate(card, closing)));
            closing = StatementCalendar.NextClosing(card, closing);
        }
        return plan;
    }

    /// <summary>Alguma parcela já está numa fatura fechada? Então valores, datas e parcelas não mudam mais.</summary>
    public bool IsLocked(Guid purchaseId)
    {
        var purchase = Purchase(purchaseId);
        var card = Card(purchase?.CardId);
        if (purchase is null || card is null)
        {
            return false;
        }
        var today = Today;
        return Installments(purchaseId).Any(t => t.StatementClosing is { } c && StatementCalendar.IsClosed(card, c, today));
    }

    public string? Validate(PurchaseDraft d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.Description))
        {
            return "Escreva uma descrição.";
        }
        if (d.Description.Trim().Length > MaxDescriptionLength)
        {
            return $"A descrição tem no máximo {MaxDescriptionLength} caracteres.";
        }
        if ((d.Notes?.Length ?? 0) > MaxNotesLength)
        {
            return $"As observações têm no máximo {MaxNotesLength} caracteres.";
        }
        if (d.Tags.Count(t => !string.IsNullOrWhiteSpace(t)) > MaxTags || d.Tags.Any(t => t.Trim().Length > MaxTagLength))
        {
            return $"Use até {MaxTags} etiquetas de até {MaxTagLength} caracteres.";
        }
        if (Card(d.CardId) is not { } card)
        {
            return Cards.Count == 0 ? "Cadastre um cartão primeiro." : "Escolha o cartão.";
        }
        var existing = d.Id is { } id ? Purchase(id) : null;
        if (d.Id is not null && existing is null)
        {
            return "Compra não encontrada (talvez tenha sido excluída).";
        }
        if (card.IsArchived && existing?.CardId != card.Id)
        {
            return $"O cartão {card.Name} está arquivado.";
        }
        if (d.Total <= 0)
        {
            return "Informe um valor maior que zero.";
        }
        if (d.Total > Money.MaxAbsMinor)
        {
            return "Valor fora do limite.";
        }
        if (d.Installments is < 1 or > MaxInstallments)
        {
            return $"Parcelas: de 1 a {MaxInstallments}.";
        }
        if (d.Total < d.Installments)
        {
            return "Cada parcela precisa ter pelo menos 1 centavo.";
        }
        if (d.CategoryId is { } categoryId)
        {
            if (Category(categoryId) is not { Kind: CategoryKind.Expense } category)
            {
                return "Escolha uma categoria de despesa.";
            }
            if (category.IsArchived && existing?.CategoryId != categoryId)
            {
                return "Essa categoria está arquivada.";
            }
        }
        if (existing is not null && IsLocked(existing.Id) &&
            (existing.CardId != d.CardId || existing.Date != d.Date || existing.Total != d.Total || existing.Installments != d.Installments))
        {
            return "Esta compra já tem parcela em fatura fechada: valor, data, cartão e parcelas não mudam mais. Para corrigir, registre um estorno.";
        }
        return null;
    }

    public CardPurchase SavePurchase(PurchaseDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (Validate(draft) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var existing = draft.Id is { } id ? Purchase(id) : null;
        var purchase = new CardPurchase
        {
            Id = existing?.Id ?? Guid.CreateVersion7(Now),
            CardId = draft.CardId,
            Date = draft.Date,
            Description = draft.Description.Trim(),
            CategoryId = draft.CategoryId,
            Total = draft.Total,
            Installments = draft.Installments,
            Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
            Tags = NormalizeTags(draft.Tags),
            CreatedAt = existing?.CreatedAt ?? Now,
            UpdatedAt = Now,
        };
        // Mantém o ID de cada parcela existente (estornos continuam ligados a ela).
        var previous = existing is null ? [] : Installments(existing.Id).ToDictionary(t => t.InstallmentNumber ?? 0);
        // O vínculo com a recorrência (assinatura no cartão) fica na parcela e sobrevive à edição da compra.
        var recurringId = draft.RecurringId ?? previous.GetValueOrDefault(1)?.RecurringId;
        var occurrenceDate = draft.RecurringId is not null ? draft.OccurrenceDate : previous.GetValueOrDefault(1)?.OccurrenceDate;
        var installments = Plan(draft).Select(p => new FinanceTransaction
        {
            Id = previous.TryGetValue(p.Number, out var old) ? old.Id : Guid.CreateVersion7(Now),
            Kind = TransactionKind.Expense,
            Date = p.Date,
            Description = purchase.Description,
            CategoryId = purchase.CategoryId,
            Status = TransactionStatus.Cleared,
            PaymentMethod = PaymentMethod.Credit,
            Notes = purchase.Notes,
            Tags = purchase.Tags,
            PurchaseId = purchase.Id,
            InstallmentNumber = p.Number,
            RecurringId = p.Number == 1 ? recurringId : null,
            OccurrenceDate = p.Number == 1 ? occurrenceDate : null,
            Postings = [Posting.ForCard(purchase.CardId, -p.Amount, p.Closing)],
            CreatedAt = old?.CreatedAt ?? Now,
            UpdatedAt = Now,
        }).ToList();
        _store.SavePurchase(purchase, installments);
        lock (_gate)
        {
            _purchases.RemoveAll(p => p.Id == purchase.Id);
            _purchases.Add(purchase);
            var removed = _transactions.Where(t => t.PurchaseId == purchase.Id).Select(t => t.Id).ToHashSet();
            _transactions.RemoveAll(t => t.PurchaseId == purchase.Id);
            _transactions.AddRange(installments);
            // Estornos de parcelas que deixaram de existir (menos parcelas) ficam soltos, como no banco.
            var kept = installments.Select(i => i.Id).ToHashSet();
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].RefundOfId is { } r && removed.Contains(r) && !kept.Contains(r))
                {
                    _transactions[i] = _transactions[i] with { RefundOfId = null };
                }
            }
        }
        Changed();
        return purchase;
    }

    public void DeletePurchase(Guid id)
    {
        if (Purchase(id) is null)
        {
            throw new FinanceValidationException("Compra não encontrada.");
        }
        if (IsLocked(id))
        {
            throw new FinanceValidationException("Esta compra já tem parcela em fatura fechada. Para desfazer, registre um estorno.");
        }
        var installmentIds = Installments(id).Select(t => t.Id).ToHashSet();
        _store.DeletePurchase(id, Now);
        lock (_gate)
        {
            _purchases.RemoveAll(p => p.Id == id);
            _transactions.RemoveAll(t => t.PurchaseId == id);
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].RefundOfId is { } r && installmentIds.Contains(r))
                {
                    _transactions[i] = _transactions[i] with { RefundOfId = null };
                }
            }
        }
        Changed();
    }

    // ---------- Faturas ----------

    /// <summary>
    /// Faturas do cartão em ordem, da primeira com movimento até a aberta hoje (ou até a última com parcela futura).
    /// Cada uma carrega o saldo da anterior: pagamento parcial passa o restante adiante; pagamento a maior vira crédito.
    /// Juros não são calculados (dependem do contrato): o usuário lança os encargos quando vierem.
    /// </summary>
    /// <param name="paymentsUpTo">Considera só pagamentos com data até esse dia (para previsões); nulo = todos.</param>
    public IReadOnlyList<CardStatement> Statements(Guid cardId, DateOnly? paymentsUpTo = null)
    {
        var card = Card(cardId) ?? throw new FinanceValidationException("Cartão não encontrado.");
        var today = Today;
        var charges = new Dictionary<DateOnly, long>();
        var payments = new Dictionary<DateOnly, long>();
        lock (_gate)
        {
            foreach (var t in _transactions)
            {
                foreach (var p in t.Postings)
                {
                    if (p.CardId != cardId || p.StatementClosing is not { } c)
                    {
                        continue;
                    }
                    if (t.IsCardPayment)
                    {
                        if (paymentsUpTo is null || t.Date <= paymentsUpTo)
                        {
                            payments[c] = checked(payments.GetValueOrDefault(c) + p.Amount);
                        }
                    }
                    else
                    {
                        charges[c] = checked(charges.GetValueOrDefault(c) - p.Amount);
                    }
                }
            }
        }
        var open = StatementCalendar.ClosingFor(card, today);
        var keys = charges.Keys.Concat(payments.Keys).Append(open).ToList();
        var first = keys.Min();
        var last = keys.Max();
        var list = new List<CardStatement>();
        long previous = 0;
        var closing = first;
        for (var guard = 0; guard < 1200 && closing <= last; guard++)
        {
            var charge = charges.GetValueOrDefault(closing);
            var paid = payments.GetValueOrDefault(closing);
            var due = DueDate(card, closing);
            var opening = StatementCalendar.Opening(card, closing);
            var remaining = previous + charge - paid;
            StatementStatus status;
            if (!StatementCalendar.IsClosed(card, closing, today))
            {
                status = opening > today ? StatementStatus.Future : StatementStatus.Open;
            }
            else if (remaining <= 0)
            {
                status = StatementStatus.Paid;
            }
            else if (today <= due)
            {
                status = StatementStatus.Closed;
            }
            else
            {
                status = paid > 0 ? StatementStatus.PartiallyPaid : StatementStatus.Overdue;
            }
            list.Add(new CardStatement(cardId, opening, closing, due, charge, previous, paid, status));
            // Só passa para a próxima o que venceu sem ser quitado (ou o crédito de um pagamento a mais). Antes do
            // vencimento, a fatura ainda vai ser paga: as futuras não acumulam o valor dela.
            previous = due < today ? remaining : 0;
            closing = StatementCalendar.NextClosing(card, closing);
        }
        return list;
    }

    /// <summary>Dívida do cartão: compras e parcelas futuras menos pagamentos e estornos.</summary>
    public long Debt(Guid cardId)
    {
        lock (_gate)
        {
            return -Money.Sum(_transactions.SelectMany(t => t.Postings).Where(p => p.CardId == cardId).Select(p => p.Amount));
        }
    }

    public CardSummary Summary(Guid cardId)
    {
        var card = Card(cardId) ?? throw new FinanceValidationException("Cartão não encontrado.");
        var debt = Debt(cardId);
        var statements = Statements(cardId);
        var open = statements.LastOrDefault(s => s.Status == StatementStatus.Open);
        var nextDue = statements.Where(s => s.Status is StatementStatus.Closed or StatementStatus.Overdue or StatementStatus.PartiallyPaid && s.Remaining > 0)
            .LastOrDefault() ?? open;
        return new CardSummary(card, debt, card.Limit - debt, open, nextDue);
    }

    /// <summary>Fatura sugerida para pagamento: a mais antiga fechada com saldo; senão a aberta.</summary>
    public CardStatement? StatementToPay(Guid cardId)
    {
        var statements = Statements(cardId);
        return statements.LastOrDefault(s => s.Status is StatementStatus.Closed or StatementStatus.Overdue or StatementStatus.PartiallyPaid && s.Remaining > 0)
            ?? statements.LastOrDefault(s => s.Status == StatementStatus.Open);
    }

    /// <summary>
    /// Quanto as faturas tiram do caixa até o dia <paramref name="date"/> (para previsões, a partir de amanhã): para
    /// cada cartão na moeda base, o que falta pagar das faturas que vencem de hoje até lá, mais o que já venceu sem
    /// pagamento (devido desde já). Conta só pagamentos até a data: os agendados depois dela ainda não saíram.
    /// </summary>
    public long StatementOutflow(DateOnly date)
    {
        var today = Today;
        long total = 0;
        foreach (var card in Cards.Where(c => !c.IsArchived && c.Currency == BaseCurrency))
        {
            var statements = Statements(card.Id, paymentsUpTo: date);
            // O vencido sem pagamento já está carregado na primeira fatura que ainda não venceu.
            var firstPending = statements.FirstOrDefault(s => s.Due >= today);
            if (firstPending is not null && firstPending.Due > date && firstPending.Previous > 0)
            {
                total = checked(total + firstPending.Previous);
            }
            foreach (var s in statements.Where(s => s.Due >= today && s.Due <= date))
            {
                total = checked(total + Math.Max(0, s.Remaining));
            }
        }
        return total;
    }
}
