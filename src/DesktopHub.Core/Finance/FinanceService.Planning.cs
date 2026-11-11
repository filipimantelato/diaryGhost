namespace DesktopHub.Core.Finance;

/// <summary>
/// Planejamento (Fase 11): recorrências (contas, assinaturas e receitas), orçamentos por categoria e metas.
/// Ocorrências previstas nunca são gravadas como lançamentos: só a confirmação cria o lançamento real.
/// </summary>
public sealed partial class FinanceService
{
    public const int MaxRemindDays = 30;

    private List<RecurringItem> _recurring = [];
    private List<RecurringSkip> _skips = [];
    private List<Budget> _budgets = [];
    private List<FinancialGoal> _goals = [];
    private List<GoalContribution> _contributions = [];
    private List<SavedScenario> _scenarios = [];

    private void LoadPlanning(FinanceSnapshot snapshot)
    {
        _recurring = [.. snapshot.Recurring];
        _skips = [.. snapshot.RecurringSkips];
        _budgets = [.. snapshot.Budgets];
        _goals = [.. snapshot.Goals];
        _contributions = [.. snapshot.Contributions];
        _scenarios = [.. snapshot.Scenarios];
        _batches = [.. snapshot.ImportBatches];
        _rules = [.. snapshot.Rules];
    }

    // ---------- Recorrências ----------

    public IReadOnlyList<RecurringItem> Recurring
    {
        get
        {
            lock (_gate)
            {
                return [.. _recurring.OrderBy(r => r.IsArchived).ThenBy(r => r.Description, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public RecurringItem? RecurringItem(Guid? id)
    {
        if (id is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _recurring.FirstOrDefault(r => r.Id == id);
        }
    }

    public RecurringItem NewRecurring(RecurringKind kind = RecurringKind.Bill) => new()
    {
        Id = Guid.CreateVersion7(Now),
        Kind = kind,
        Description = "",
        StartDate = Today,
        AccountId = Accounts.FirstOrDefault(a => !a.IsArchived && a.Kind == AccountKind.Checking)?.Id ?? Accounts.FirstOrDefault(a => !a.IsArchived)?.Id,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public string? Validate(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Description))
        {
            return "Escreva uma descrição.";
        }
        if (item.Description.Trim().Length > MaxDescriptionLength)
        {
            return $"A descrição tem no máximo {MaxDescriptionLength} caracteres.";
        }
        if ((item.Notes?.Length ?? 0) > MaxNotesLength)
        {
            return $"As observações têm no máximo {MaxNotesLength} caracteres.";
        }
        if (item.Amount <= 0 || item.Amount > Money.MaxAbsMinor)
        {
            return item.IsEstimated ? "Informe um valor aproximado (usado até haver pagamentos confirmados)." : "Informe um valor maior que zero.";
        }
        if (item.Interval is < 1 or > RecurringSchedule.MaxInterval)
        {
            return $"Repetição: a cada 1 a {RecurringSchedule.MaxInterval} períodos.";
        }
        if (item.EndDate is { } end && end < item.StartDate)
        {
            return "O fim precisa ser depois da primeira ocorrência.";
        }
        if (item.RemindDaysBefore is < 0 or > MaxRemindDays)
        {
            return $"Aviso: de 0 a {MaxRemindDays} dias antes.";
        }
        if ((item.AccountId is null) == (item.CardId is null))
        {
            return "Escolha a conta (ou, para assinaturas, o cartão).";
        }
        var existing = RecurringItem(item.Id);
        if (item.CardId is { } cardId)
        {
            if (item.IsIncome)
            {
                return "Receitas entram numa conta, não no cartão.";
            }
            if (Card(cardId) is not { } card)
            {
                return "Cartão não encontrado.";
            }
            if (card.IsArchived && existing?.CardId != cardId)
            {
                return $"O cartão {card.Name} está arquivado.";
            }
        }
        else if (Account(item.AccountId) is not { } account)
        {
            return "Conta não encontrada.";
        }
        else if (account.IsArchived && existing?.AccountId != account.Id)
        {
            return $"A conta {account.Name} está arquivada.";
        }
        if (item.CategoryId is { } categoryId)
        {
            var expected = item.IsIncome ? CategoryKind.Income : CategoryKind.Expense;
            if (Category(categoryId) is not { } category || category.Kind != expected)
            {
                return item.IsIncome ? "Escolha uma categoria de receita." : "Escolha uma categoria de despesa.";
            }
            if (category.IsArchived && existing?.CategoryId != categoryId)
            {
                return "Essa categoria está arquivada.";
            }
        }
        if (existing is not null && existing.IsIncome != item.IsIncome && ConfirmedOf(item.Id).Count > 0)
        {
            return "Uma receita recorrente com pagamentos confirmados não pode virar conta (e vice-versa).";
        }
        return null;
    }

    public RecurringItem SaveRecurring(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Validate(item) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var value = item with
        {
            Description = item.Description.Trim(),
            Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim(),
            UpdatedAt = Now,
        };
        _store.SaveRecurring(value);
        lock (_gate)
        {
            _recurring.RemoveAll(r => r.Id == value.Id);
            _recurring.Add(value);
        }
        Changed();
        return value;
    }

    /// <summary>Pausa (ou retoma) as ocorrências previstas. O histórico confirmado fica.</summary>
    public void SetArchived(RecurringItem item, bool archived)
    {
        ArgumentNullException.ThrowIfNull(item);
        SaveRecurring(item with { ArchivedAt = archived ? Now : null });
    }

    /// <summary>Exclui o modelo; os lançamentos confirmados ficam como lançamentos comuns.</summary>
    public void DeleteRecurring(Guid id)
    {
        if (RecurringItem(id) is null)
        {
            throw new FinanceValidationException("Recorrência não encontrada.");
        }
        _store.DeleteRecurring(id, Now);
        lock (_gate)
        {
            _recurring.RemoveAll(r => r.Id == id);
            _skips.RemoveAll(s => s.RecurringId == id);
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].RecurringId == id)
                {
                    _transactions[i] = _transactions[i] with { RecurringId = null, OccurrenceDate = null };
                }
            }
        }
        Changed();
    }

    /// <summary>Lançamentos confirmados da recorrência, do mais recente para o mais antigo.</summary>
    public IReadOnlyList<FinanceTransaction> ConfirmedOf(Guid recurringId)
    {
        lock (_gate)
        {
            return [.. _transactions.Where(t => t.RecurringId == recurringId).OrderByDescending(t => t.OccurrenceDate).ThenByDescending(t => t.Date)];
        }
    }

    /// <summary>Valor previsto: o fixo, ou a média das 3 últimas ocorrências confirmadas (o valor de partida se não houver).</summary>
    public long ExpectedAmount(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsEstimated)
        {
            return item.Amount;
        }
        var last = ConfirmedOf(item.Id).Take(3).Select(t => t.Amount).ToList();
        return last.Count == 0 ? item.Amount : Money.DivideRounded(Money.Sum(last), last.Count);
    }

    /// <summary>
    /// Ocorrências das recorrências no período (inclusive pausadas, se pedido), com a situação de cada uma.
    /// Confirmadas aparecem pela data prevista, mesmo que o lançamento tenha outra data.
    /// </summary>
    public IReadOnlyList<RecurringOccurrence> Occurrences(DateOnly from, DateOnly to, bool includeArchived = false)
    {
        var today = Today;
        var result = new List<RecurringOccurrence>();
        List<RecurringItem> items;
        Dictionary<(Guid, DateOnly), FinanceTransaction> confirmed;
        HashSet<(Guid, DateOnly)> skipped;
        lock (_gate)
        {
            items = [.. _recurring.Where(r => includeArchived || !r.IsArchived)];
            confirmed = _transactions.Where(t => t.RecurringId is not null && t.OccurrenceDate is not null)
                .GroupBy(t => (t.RecurringId!.Value, t.OccurrenceDate!.Value)).ToDictionary(g => g.Key, g => g.First());
            skipped = [.. _skips.Select(s => (s.RecurringId, s.Date))];
        }
        foreach (var item in items)
        {
            var expected = ExpectedAmount(item);
            foreach (var date in RecurringSchedule.Between(item, from, to))
            {
                if (confirmed.TryGetValue((item.Id, date), out var t))
                {
                    result.Add(new RecurringOccurrence(item, date, t.Amount, false, OccurrenceState.Confirmed, t.Id));
                }
                else if (skipped.Contains((item.Id, date)))
                {
                    result.Add(new RecurringOccurrence(item, date, expected, item.IsEstimated, OccurrenceState.Skipped, null));
                }
                else if (!item.IsArchived)
                {
                    result.Add(new RecurringOccurrence(item, date, expected, item.IsEstimated, date < today ? OccurrenceState.Overdue : OccurrenceState.Planned, null));
                }
            }
        }
        return [.. result.OrderBy(o => o.Date).ThenBy(o => o.Item.Description, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Ocorrências que entram nas previsões até <paramref name="until"/>: previstas e as atrasadas do mês atual (ainda
    /// vão sair ou entrar). Atrasadas de meses anteriores não entram: provavelmente foram pagas sem confirmar.
    /// </summary>
    public IReadOnlyList<RecurringOccurrence> OpenOccurrences(DateOnly until)
    {
        var today = Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return until < monthStart ? [] : [.. Occurrences(monthStart, until).Where(o => o.IsOpen)];
    }

    /// <summary>Próxima ocorrência em aberto (a mais antiga atrasada do mês, ou a próxima prevista).</summary>
    public RecurringOccurrence? NextOccurrence(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var today = Today;
        var from = new DateOnly(today.Year, today.Month, 1);
        return Occurrences(from, today.AddYears(item.Interval * 2 + 1)).FirstOrDefault(o => o.Item.Id == item.Id && o.IsOpen);
    }

    /// <summary>
    /// Confirma a ocorrência: cria o lançamento real (despesa ou receita na conta; compra à vista no cartão). Confirmar
    /// de novo a mesma ocorrência é recusado — nunca duplica.
    /// </summary>
    /// <param name="paidOn">Data real do pagamento/recebimento (padrão: a data prevista).</param>
    /// <param name="amount">Valor real (padrão: o previsto).</param>
    public FinanceTransaction Confirm(Guid recurringId, DateOnly occurrence, DateOnly? paidOn = null, long? amount = null, Guid? accountId = null)
    {
        var item = RecurringItem(recurringId) ?? throw new FinanceValidationException("Recorrência não encontrada.");
        if (!RecurringSchedule.Between(item, occurrence, occurrence).Any())
        {
            throw new FinanceValidationException("Essa data não é uma ocorrência desta recorrência.");
        }
        if (FindOccurrence(recurringId, occurrence) is not null)
        {
            throw new FinanceValidationException("Essa ocorrência já foi confirmada.");
        }
        var value = amount ?? ExpectedAmount(item);
        var date = paidOn ?? occurrence;
        if (item.CardId is { } cardId)
        {
            var purchase = SavePurchase(new PurchaseDraft
            {
                CardId = cardId,
                Date = date,
                Description = item.Description,
                Total = value,
                Installments = 1,
                CategoryId = item.CategoryId,
                RecurringId = item.Id,
                OccurrenceDate = occurrence,
            });
            SetSkip(recurringId, occurrence, false, publish: false);
            return Installments(purchase.Id)[0];
        }
        var saved = Save(new TransactionDraft
        {
            Kind = item.IsIncome ? TransactionKind.Income : TransactionKind.Expense,
            Date = date,
            Description = item.Description,
            Amount = value,
            AccountId = accountId ?? item.AccountId!.Value,
            CategoryId = item.CategoryId,
            Status = TransactionStatus.Cleared,
            PaymentMethod = item.Kind == RecurringKind.Bill ? PaymentMethod.BankSlip : PaymentMethod.None,
            RecurringId = item.Id,
            OccurrenceDate = occurrence,
        });
        SetSkip(recurringId, occurrence, false, publish: false);
        return saved;
    }

    private FinanceTransaction? FindOccurrence(Guid recurringId, DateOnly occurrence)
    {
        lock (_gate)
        {
            return _transactions.FirstOrDefault(t => t.RecurringId == recurringId && t.OccurrenceDate == occurrence);
        }
    }

    /// <summary>Pula (ou deixa de pular) uma ocorrência; ocorrência já confirmada não pode ser pulada.</summary>
    public void Skip(Guid recurringId, DateOnly occurrence, bool skipped = true)
    {
        if (RecurringItem(recurringId) is null)
        {
            throw new FinanceValidationException("Recorrência não encontrada.");
        }
        if (skipped && FindOccurrence(recurringId, occurrence) is not null)
        {
            throw new FinanceValidationException("Essa ocorrência já foi confirmada; exclua o lançamento para desfazer.");
        }
        SetSkip(recurringId, occurrence, skipped, publish: true);
    }

    private void SetSkip(Guid recurringId, DateOnly occurrence, bool skipped, bool publish)
    {
        var exists = false;
        lock (_gate)
        {
            exists = _skips.Any(s => s.RecurringId == recurringId && s.Date == occurrence);
        }
        if (exists == skipped)
        {
            return;
        }
        var skip = new RecurringSkip(recurringId, occurrence);
        _store.SetRecurringSkip(skip, skipped);
        lock (_gate)
        {
            _skips.RemoveAll(s => s == skip);
            if (skipped)
            {
                _skips.Add(skip);
            }
        }
        if (publish)
        {
            Changed();
        }
    }

    /// <summary>Custo mensal e anual equivalentes das assinaturas ativas na moeda base.</summary>
    public (long Monthly, long Yearly) SubscriptionTotals()
    {
        long yearly = 0;
        foreach (var item in Recurring.Where(r => r.Kind == RecurringKind.Subscription && !r.IsArchived && CurrencyOfRecurring(r) == BaseCurrency))
        {
            if (item.EndDate is { } end && end < Today)
            {
                continue;
            }
            yearly = checked(yearly + RecurringSchedule.YearlyCost(item, ExpectedAmount(item)));
        }
        return (Money.DivideRounded(yearly, 12), yearly);
    }

    public string? CurrencyOfRecurring(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.CardId is { } card ? Card(card)?.Currency : Account(item.AccountId)?.Currency;
    }

    // ---------- Orçamentos ----------

    public IReadOnlyList<Budget> Budgets
    {
        get
        {
            lock (_gate)
            {
                return [.. _budgets];
            }
        }
    }

    /// <summary>Orçamento que vale para a categoria no mês: o ajuste do mês, ou o padrão.</summary>
    public Budget? BudgetFor(Guid categoryId, int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        lock (_gate)
        {
            return _budgets.FirstOrDefault(b => b.CategoryId == categoryId && b.Month == first)
                ?? _budgets.FirstOrDefault(b => b.CategoryId == categoryId && b.Month is null);
        }
    }

    /// <summary>
    /// Define o limite de uma categoria principal de despesa: para todo mês (<paramref name="month"/> nulo) ou só para
    /// um mês. Limite 0 num mês específico = sem orçamento naquele mês.
    /// </summary>
    public Budget SetBudget(Guid categoryId, DateOnly? month, long limit, int warnPercent = 80)
    {
        if (Category(categoryId) is not { Kind: CategoryKind.Expense, ParentId: null } category)
        {
            throw new FinanceValidationException("Orçamentos são por categoria principal de despesa (incluem as subcategorias).");
        }
        if (category.IsArchived)
        {
            throw new FinanceValidationException("Essa categoria está arquivada.");
        }
        if (limit < 0 || limit > Money.MaxAbsMinor || (month is null && limit == 0))
        {
            throw new FinanceValidationException("Informe um limite maior que zero.");
        }
        if (warnPercent is < 1 or > 100)
        {
            throw new FinanceValidationException("A faixa de atenção vai de 1% a 100%.");
        }
        var first = month is { } m ? new DateOnly(m.Year, m.Month, 1) : (DateOnly?)null;
        Budget? existing;
        lock (_gate)
        {
            existing = _budgets.FirstOrDefault(b => b.CategoryId == categoryId && b.Month == first);
        }
        var value = new Budget { Id = existing?.Id ?? Guid.CreateVersion7(Now), CategoryId = categoryId, Month = first, Limit = limit, WarnPercent = warnPercent };
        _store.SaveBudget(value);
        lock (_gate)
        {
            _budgets.RemoveAll(b => b.Id == value.Id);
            _budgets.Add(value);
        }
        Changed();
        return value;
    }

    /// <summary>Remove o orçamento padrão ou o ajuste de um mês (o mês volta a usar o padrão).</summary>
    public void RemoveBudget(Guid categoryId, DateOnly? month)
    {
        var first = month is { } m ? new DateOnly(m.Year, m.Month, 1) : (DateOnly?)null;
        Budget? existing;
        lock (_gate)
        {
            existing = _budgets.FirstOrDefault(b => b.CategoryId == categoryId && b.Month == first);
        }
        if (existing is null)
        {
            return;
        }
        _store.DeleteBudget(existing.Id);
        lock (_gate)
        {
            _budgets.RemoveAll(b => b.Id == existing.Id);
        }
        Changed();
    }

    /// <summary>
    /// Orçamento do mês: para cada categoria principal com limite, o gasto reconhecido (despesas confirmadas e pendentes,
    /// menos estornos, na moeda base; parcelas de cartão na data de cada parcela) e o que ainda está previsto em
    /// contas e assinaturas.
    /// </summary>
    public BudgetMonth BudgetMonth(int year, int month)
    {
        var (start, end) = MonthRange(year, month);
        var spending = SpendingByCategory(start, end);
        var planned = new Dictionary<Guid, long>();
        var categories = Categories.ToDictionary(c => c.Id);
        foreach (var o in Occurrences(start, end).Where(o => o.IsOpen && !o.Item.IsIncome && CurrencyOfRecurring(o.Item) == BaseCurrency))
        {
            if (OccurrenceCounts(o) && o.Item.CategoryId is { } c && categories.TryGetValue(c, out var category))
            {
                var parent = category.ParentId ?? category.Id;
                planned[parent] = checked(planned.GetValueOrDefault(parent) + o.Amount);
            }
        }
        var lines = new List<BudgetLine>();
        var budgeted = new HashSet<Guid>();
        foreach (var category in OrderedCategories(CategoryKind.Expense, includeArchived: true).Where(c => c.ParentId is null))
        {
            if (BudgetFor(category.Id, year, month) is not { Limit: > 0 } budget)
            {
                continue;
            }
            budgeted.Add(category.Id);
            var spent = spending.FirstOrDefault(s => s.CategoryId == category.Id)?.Amount ?? 0;
            lines.Add(new BudgetLine(category, budget, budget.Month is null, spent, planned.GetValueOrDefault(category.Id)));
        }
        var unbudgeted = Money.Sum(spending.Where(s => s.CategoryId is not { } id || !budgeted.Contains(id)).Select(s => s.Amount));
        return new BudgetMonth(year, month, lines, unbudgeted);
    }

    /// <summary>Atrasadas de meses anteriores ao atual não contam como "previstas" (ver <see cref="OpenOccurrences"/>).</summary>
    private bool OccurrenceCounts(RecurringOccurrence o)
    {
        var today = Today;
        return o.State == OccurrenceState.Planned || o.Date >= new DateOnly(today.Year, today.Month, 1);
    }

    /// <summary>Copia os limites que valiam no mês anterior como ajustes deste mês. Devolve quantos foram copiados.</summary>
    public int CopyBudgetsFromPrevious(int year, int month)
    {
        var previous = new DateOnly(year, month, 1).AddMonths(-1);
        var count = 0;
        foreach (var category in Categories.Where(c => c.Kind == CategoryKind.Expense && c.ParentId is null && !c.IsArchived))
        {
            if (BudgetFor(category.Id, previous.Year, previous.Month) is { Limit: > 0 } budget)
            {
                SetBudget(category.Id, new DateOnly(year, month, 1), budget.Limit, budget.WarnPercent);
                count++;
            }
        }
        return count;
    }

    // ---------- Metas ----------

    public IReadOnlyList<FinancialGoal> Goals
    {
        get
        {
            lock (_gate)
            {
                return [.. _goals.OrderBy(g => g.IsArchived).ThenBy(g => g.Deadline ?? DateOnly.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public FinancialGoal? Goal(Guid? id)
    {
        if (id is null)
        {
            return null;
        }
        lock (_gate)
        {
            return _goals.FirstOrDefault(g => g.Id == id);
        }
    }

    public IReadOnlyList<GoalContribution> Contributions(Guid goalId)
    {
        lock (_gate)
        {
            return [.. _contributions.Where(c => c.GoalId == goalId).OrderByDescending(c => c.Date).ThenByDescending(c => c.CreatedAt)];
        }
    }

    private List<GoalContribution> AllContributions()
    {
        lock (_gate)
        {
            return [.. _contributions];
        }
    }

    public FinancialGoal NewGoal() => new()
    {
        Id = Guid.CreateVersion7(Now),
        Name = "",
        Target = 0,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public string? Validate(FinancialGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (string.IsNullOrWhiteSpace(goal.Name))
        {
            return "Dê um nome à meta.";
        }
        if (goal.Name.Trim().Length > MaxNameLength)
        {
            return $"O nome tem no máximo {MaxNameLength} caracteres.";
        }
        if (goal.Target <= 0 || goal.Target > Money.MaxAbsMinor)
        {
            return "Informe quanto quer juntar.";
        }
        if (goal.AccountId is { } accountId)
        {
            if (Account(accountId) is not { } account)
            {
                return "Conta não encontrada.";
            }
            if (account.Currency != BaseCurrency)
            {
                return $"A conta da meta precisa ser em {BaseCurrency}.";
            }
        }
        var existing = Goal(goal.Id);
        if (existing is not null && existing.AccountId != goal.AccountId && Contributions(goal.Id).Any(c => c.Kind == ContributionKind.Real))
        {
            return "A conta de uma meta com dinheiro guardado não pode mudar (retire as contribuições antes).";
        }
        return null;
    }

    public FinancialGoal SaveGoal(FinancialGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (Validate(goal) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var value = goal with { Name = goal.Name.Trim(), UpdatedAt = Now };
        _store.SaveGoal(value);
        lock (_gate)
        {
            _goals.RemoveAll(g => g.Id == value.Id);
            _goals.Add(value);
        }
        Changed();
        return value;
    }

    /// <summary>Arquivar (concluir) libera o "Reservado" da meta; as transferências feitas continuam no histórico.</summary>
    public void SetArchived(FinancialGoal goal, bool archived)
    {
        ArgumentNullException.ThrowIfNull(goal);
        SaveGoal(goal with { ArchivedAt = archived ? Now : null });
    }

    public void DeleteGoal(Guid id)
    {
        if (Goal(id) is null)
        {
            throw new FinanceValidationException("Meta não encontrada.");
        }
        _store.DeleteGoal(id, Now);
        lock (_gate)
        {
            _goals.RemoveAll(g => g.Id == id);
            _contributions.RemoveAll(c => c.GoalId == id);
        }
        Changed();
    }

    /// <summary>
    /// Contribuição para a meta. Real: até hoje; se a meta tem conta própria, vira uma transferência de
    /// <paramref name="accountId"/> para ela (retirada: da conta da meta de volta); se não, é uma reserva dentro de
    /// <paramref name="accountId"/>, que diminui o disponível, não o saldo. Planejada: só intenção, qualquer data.
    /// </summary>
    public GoalContribution Contribute(Guid goalId, ContributionKind kind, DateOnly date, long amount, Guid? accountId, string? notes = null)
    {
        var goal = Goal(goalId) ?? throw new FinanceValidationException("Meta não encontrada.");
        if (goal.IsArchived)
        {
            throw new FinanceValidationException("A meta está arquivada.");
        }
        if (amount == 0 || Math.Abs(amount) > Money.MaxAbsMinor)
        {
            throw new FinanceValidationException("Informe um valor diferente de zero.");
        }
        if ((notes?.Length ?? 0) > MaxNotesLength)
        {
            throw new FinanceValidationException($"As observações têm no máximo {MaxNotesLength} caracteres.");
        }
        var contribution = new GoalContribution
        {
            Id = Guid.CreateVersion7(Now),
            GoalId = goalId,
            Date = date,
            Amount = amount,
            Kind = kind,
            AccountId = accountId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = Now,
        };
        FinanceTransaction? transfer = null;
        if (kind == ContributionKind.Real)
        {
            if (date > Today)
            {
                throw new FinanceValidationException("Dinheiro guardado de fato tem data de hoje ou antes. Para o futuro, use uma contribuição planejada.");
            }
            var real = RealSaved(goalId, Today);
            if (amount < 0 && -amount > real)
            {
                throw new FinanceValidationException($"A meta tem {Money.Format(real, BaseCurrency)} guardados; não dá para retirar mais que isso.");
            }
            if (goal.AccountId is { } goalAccount)
            {
                if (accountId is not { } source || source == goalAccount)
                {
                    throw new FinanceValidationException(amount > 0 ? "Escolha a conta de onde sai o dinheiro." : "Escolha a conta para onde o dinheiro volta.");
                }
                var draft = new TransactionDraft
                {
                    Kind = TransactionKind.Transfer,
                    Date = date,
                    Description = amount > 0 ? $"Meta: {goal.Name}" : $"Retirada da meta: {goal.Name}",
                    Amount = Math.Abs(amount),
                    AccountId = amount > 0 ? source : goalAccount,
                    ToAccountId = amount > 0 ? goalAccount : source,
                };
                transfer = TryBuild(draft, out var error) ?? throw new FinanceValidationException(error ?? "Transferência inválida.");
                contribution = contribution with { AccountId = goalAccount, TransactionId = transfer.Id };
            }
            else
            {
                if (Account(accountId) is not { } account)
                {
                    throw new FinanceValidationException("Escolha a conta onde o dinheiro fica reservado.");
                }
                if (account.Currency != BaseCurrency)
                {
                    throw new FinanceValidationException($"A reserva precisa ser numa conta em {BaseCurrency}.");
                }
                if (amount < 0 && -amount > ReservedIn(goalId, account.Id))
                {
                    throw new FinanceValidationException($"Há {Money.Format(ReservedIn(goalId, account.Id), BaseCurrency)} desta meta reservados em {account.Name}.");
                }
            }
        }
        else if (accountId is { } planned && Account(planned) is null)
        {
            throw new FinanceValidationException("Conta não encontrada.");
        }
        _store.SaveContribution(contribution, transfer);
        lock (_gate)
        {
            _contributions.Add(contribution);
            if (transfer is not null)
            {
                _transactions.Add(transfer);
            }
        }
        Changed();
        return contribution;
    }

    /// <summary>Transforma uma contribuição planejada em real (hoje, ou na data informada).</summary>
    public GoalContribution Realize(Guid plannedId, Guid? accountId, DateOnly? date = null)
    {
        GoalContribution planned;
        lock (_gate)
        {
            planned = _contributions.FirstOrDefault(c => c.Id == plannedId && c.Kind == ContributionKind.Planned)
                ?? throw new FinanceValidationException("Contribuição planejada não encontrada.");
        }
        var real = Contribute(planned.GoalId, ContributionKind.Real, date ?? Today, planned.Amount, accountId ?? planned.AccountId, planned.Notes);
        DeleteContribution(plannedId);
        return real;
    }

    /// <summary>Exclui a contribuição; se ela criou uma transferência, a transferência sai junto.</summary>
    public void DeleteContribution(Guid id)
    {
        GoalContribution value;
        lock (_gate)
        {
            value = _contributions.FirstOrDefault(c => c.Id == id) ?? throw new FinanceValidationException("Contribuição não encontrada.");
        }
        if (value.Kind == ContributionKind.Real && value.Amount > 0)
        {
            // Retirar o que entrou não pode deixar retiradas posteriores maiores que o guardado.
            var after = RealSaved(value.GoalId, DateOnly.MaxValue) - value.Amount;
            if (after < 0)
            {
                throw new FinanceValidationException("Há retiradas que dependem desta contribuição; exclua-as antes.");
            }
        }
        _store.DeleteContribution(id, Now);
        lock (_gate)
        {
            _contributions.RemoveAll(c => c.Id == id);
            if (value.TransactionId is { } tx)
            {
                _transactions.RemoveAll(t => t.Id == tx);
            }
        }
        Changed();
    }

    /// <summary>Contribuição ligada ao lançamento (transferência para a conta da meta), se houver.</summary>
    public GoalContribution? ContributionOf(Guid transactionId)
    {
        lock (_gate)
        {
            return _contributions.FirstOrDefault(c => c.TransactionId == transactionId);
        }
    }

    private long RealSaved(Guid goalId, DateOnly asOf)
    {
        lock (_gate)
        {
            return Money.Sum(_contributions.Where(c => c.GoalId == goalId && c.Kind == ContributionKind.Real && c.Date <= asOf).Select(c => c.Amount));
        }
    }

    private long ReservedIn(Guid goalId, Guid accountId)
    {
        lock (_gate)
        {
            return Money.Sum(_contributions.Where(c => c.GoalId == goalId && c.Kind == ContributionKind.Real && c.AccountId == accountId).Select(c => c.Amount));
        }
    }

    public GoalProgress Progress(FinancialGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var today = Today;
        var real = RealSaved(goal.Id, today);
        long plannedAhead;
        lock (_gate)
        {
            plannedAhead = Money.Sum(_contributions.Where(c => c.GoalId == goal.Id && c.Kind == ContributionKind.Planned && c.Date >= today).Select(c => c.Amount));
        }
        int? monthsLeft = null;
        long? monthly = null;
        var late = false;
        if (goal.Deadline is { } deadline)
        {
            if (deadline < today)
            {
                late = real < goal.Target;
            }
            else
            {
                monthsLeft = ((deadline.Year - today.Year) * 12) + deadline.Month - today.Month + 1;
                var remaining = Math.Max(0, goal.Target - real);
                // Arredonda para cima: guardar o valor indicado todo mês chega ao alvo, nunca um centavo abaixo.
                monthly = (remaining + monthsLeft.Value - 1) / monthsLeft.Value;
            }
        }
        return new GoalProgress(goal, real, plannedAhead, monthsLeft, monthly) { Late = late };
    }

    /// <summary>
    /// Reservado: dinheiro guardado de fato (até <paramref name="asOf"/>) em metas ativas, que está em contas do
    /// disponível. Contribuições planejadas nunca entram. Meta com conta fora do disponível (ex.: investimento) não
    /// reserva de novo: o dinheiro já saiu do disponível.
    /// </summary>
    public long Reserved(DateOnly asOf)
    {
        lock (_gate)
        {
            var available = AvailableAccounts();
            var active = _goals.Where(g => !g.IsArchived).Select(g => g.Id).ToHashSet();
            var total = Money.Sum(_contributions.Where(c => c.Kind == ContributionKind.Real && c.Date <= asOf && active.Contains(c.GoalId)
                    && c.AccountId is { } a && available.Contains(a)).Select(c => c.Amount));
            return Math.Max(0, total);
        }
    }

    /// <summary>Contribuições planejadas de metas ativas com data entre as duas (inclusivas).</summary>
    public IReadOnlyList<GoalContribution> PlannedContributions(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            var active = _goals.Where(g => !g.IsArchived).Select(g => g.Id).ToHashSet();
            return [.. _contributions.Where(c => c.Kind == ContributionKind.Planned && active.Contains(c.GoalId) && c.Date >= from && c.Date <= to).OrderBy(c => c.Date)];
        }
    }

    // ---------- Cenários salvos ----------

    public IReadOnlyList<SavedScenario> Scenarios
    {
        get
        {
            lock (_gate)
            {
                return [.. _scenarios.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public SavedScenario SaveScenario(Guid? id, string name, IReadOnlyList<SimulationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new FinanceValidationException($"Dê um nome ao cenário (até {MaxNameLength} caracteres).");
        }
        if (items.Count is 0 or > MaxSimulationItems)
        {
            throw new FinanceValidationException($"Um cenário tem de 1 a {MaxSimulationItems} hipóteses.");
        }
        foreach (var item in items)
        {
            if (Validate(item) is { } error)
            {
                throw new FinanceValidationException(error);
            }
        }
        SavedScenario? existing;
        lock (_gate)
        {
            existing = id is null ? null : _scenarios.FirstOrDefault(s => s.Id == id);
        }
        var value = new SavedScenario(existing?.Id ?? Guid.CreateVersion7(Now), name.Trim(), items, existing?.CreatedAt ?? Now, Now);
        _store.SaveScenario(value);
        lock (_gate)
        {
            _scenarios.RemoveAll(s => s.Id == value.Id);
            _scenarios.Add(value);
        }
        Changed();
        return value;
    }

    public void DeleteScenario(Guid id)
    {
        _store.DeleteScenario(id);
        lock (_gate)
        {
            _scenarios.RemoveAll(s => s.Id == id);
        }
        Changed();
    }
}
