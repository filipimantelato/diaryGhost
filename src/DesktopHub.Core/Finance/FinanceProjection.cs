namespace DesktopHub.Core.Finance;

public enum SimulationKind
{
    /// <summary>Gasto à vista na conta.</summary>
    Expense,
    Income,
    /// <summary>Compra no cartão, à vista ou parcelada: sai do caixa no vencimento de cada fatura.</summary>
    CardPurchase,
    /// <summary>Guardar dinheiro (diminui o disponível, não o saldo).</summary>
    Saving,
}

/// <summary>Hipótese temporária do simulador. Nunca vira lançamento.</summary>
public sealed record SimulationItem
{
    public SimulationKind Kind { get; init; } = SimulationKind.Expense;
    public string Description { get; init; } = "";
    public required DateOnly Date { get; init; }
    public required long Amount { get; init; }
    public int Installments { get; init; } = 1;
    public Guid? CardId { get; init; }
}

/// <summary>Cenário com nome, guardado à parte dos lançamentos e sempre exibido como "simulado".</summary>
public sealed record SavedScenario(Guid Id, string Name, IReadOnlyList<SimulationItem> Items, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Baseline">Disponível livre projetado (contas do disponível − faturas − reservado − contribuições planejadas).</param>
/// <param name="Simulated">O mesmo, com as hipóteses do simulador.</param>
public sealed record ProjectionPoint(DateOnly Date, long Baseline, long Simulated);

public sealed record ProjectionMonth(int Year, int Month, ProjectionPoint Lowest, ProjectionPoint End);

/// <summary>O que a projeção considera (listado sempre abaixo do gráfico).</summary>
public sealed record ProjectionAssumptions
{
    /// <summary>Lançamentos já registrados com data futura ou ainda pendentes.</summary>
    public int ScheduledTransactions { get; init; }
    public int PlannedIncome { get; init; }
    public int PlannedExpenses { get; init; }
    /// <summary>Quantas das previstas têm valor estimado pela média.</summary>
    public int Estimated { get; init; }
    /// <summary>Faturas de cartão com vencimento no período (ou vencidas sem pagamento).</summary>
    public int Statements { get; init; }
    public int PlannedContributions { get; init; }
    public long Reserved { get; init; }
    /// <summary>Média diária de gastos variáveis aplicada (nulo = não considerada).</summary>
    public long? VariableDaily { get; init; }
    public int Simulations { get; init; }
}

public sealed record Projection(DateOnly From, DateOnly To, IReadOnlyList<ProjectionPoint> Points, IReadOnlyList<ProjectionMonth> Months,
    ProjectionAssumptions Assumptions, long SafetyMargin)
{
    public ProjectionPoint Lowest => Points.MinBy(p => p.Baseline)!;
    public ProjectionPoint LowestSimulated => Points.MinBy(p => p.Simulated)!;
    public bool HasSimulation => Assumptions.Simulations > 0;

    /// <summary>
    /// Quanto dá para reservar hoje sem o disponível ficar abaixo da margem em nenhum dia do período (com as hipóteses
    /// do simulador). Reservar hoje baixa todos os dias seguintes, então o limite é o menor ponto.
    /// </summary>
    public long CanReserve => Math.Max(0, LowestSimulated.Simulated - SafetyMargin);

    /// <summary>Mês mais apertado (menor ponto, com as hipóteses).</summary>
    public ProjectionMonth TightestMonth => Months.MinBy(m => m.Lowest.Simulated)!;
}

public sealed partial class FinanceService
{
    public const int MaxSimulationItems = 20;
    public const int MaxProjectionDays = 400;

    public string? Validate(SimulationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Amount <= 0 || item.Amount > Money.MaxAbsMinor)
        {
            return "Informe um valor maior que zero.";
        }
        if (item.Description.Length > MaxDescriptionLength)
        {
            return $"A descrição tem no máximo {MaxDescriptionLength} caracteres.";
        }
        var today = Today;
        if (item.Date < today || item.Date > today.AddDays(MaxProjectionDays))
        {
            return "Simulações vão de hoje até cerca de 1 ano à frente.";
        }
        if (item.Kind == SimulationKind.CardPurchase)
        {
            if (Card(item.CardId) is not { IsArchived: false })
            {
                return "Escolha o cartão.";
            }
            if (item.Installments is < 1 or > MaxInstallments || item.Amount < item.Installments)
            {
                return $"Parcelas: de 1 a {MaxInstallments}.";
            }
        }
        return null;
    }

    /// <summary>
    /// Projeção dia a dia do disponível livre até <paramref name="to"/>: saldo das contas do disponível (com pendentes e
    /// lançamentos futuros), menos faturas no vencimento, contas/assinaturas previstas, receitas previstas, menos o
    /// reservado e as contribuições planejadas. Simulações aparecem numa linha à parte e nunca alteram os dados reais.
    /// </summary>
    /// <param name="includeVariable">Desconta a média diária de gastos variáveis (sem recorrências) dos 3 meses anteriores.</param>
    public Projection Project(DateOnly to, IReadOnlyList<SimulationItem>? simulations = null, bool includeVariable = false, long safetyMargin = 0)
    {
        var today = Today;
        var first = today.AddDays(1);
        if (to < first)
        {
            to = first;
        }
        if (to > today.AddDays(MaxProjectionDays))
        {
            to = today.AddDays(MaxProjectionDays);
        }
        var deltas = new Dictionary<DateOnly, long>();
        void Add(Dictionary<DateOnly, long> map, DateOnly date, long amount)
        {
            var d = date < first ? first : date;
            if (d <= to)
            {
                map[d] = checked(map.GetValueOrDefault(d) + amount);
            }
        }

        long start, now;
        var scheduled = 0;
        var reserved = Reserved(today);
        var paymentDates = new HashSet<DateOnly>();
        lock (_gate)
        {
            var available = AvailableAccounts();
            var accounts = _accounts.Where(a => available.Contains(a.Id)).ToList();
            now = Money.Sum(accounts.Select(a => BalanceCore(a, today, includePending: false)));
            start = Money.Sum(accounts.Select(a => BalanceCore(a, today, includePending: true)));
            foreach (var t in _transactions)
            {
                if (t.Status == TransactionStatus.Pending && t.Date <= today && t.Postings.Any(p => available.Contains(p.AccountId)))
                {
                    scheduled++;
                }
                if (t.Date <= today)
                {
                    continue;
                }
                var touched = false;
                foreach (var p in t.Postings.Where(p => available.Contains(p.AccountId)))
                {
                    Add(deltas, t.Date, p.Amount);
                    touched = true;
                }
                if (touched)
                {
                    scheduled++;
                }
                if (t.IsCardPayment)
                {
                    paymentDates.Add(t.Date);
                }
            }
        }

        // Faturas: o quanto sai do caixa só muda num vencimento ou num pagamento agendado.
        var statements = 0;
        var breakpoints = new SortedSet<DateOnly> { first };
        foreach (var card in Cards.Where(c => !c.IsArchived && c.Currency == BaseCurrency))
        {
            var list = Statements(card.Id);
            if (list.LastOrDefault(s => s.Due < today) is { Remaining: > 0 })
            {
                statements++;
            }
            foreach (var s in list)
            {
                if (s.Due > first && s.Due <= to)
                {
                    breakpoints.Add(s.Due);
                }
                if (s.Remaining > 0 && s.Due >= today && s.Due <= to)
                {
                    statements++;
                }
            }
        }
        foreach (var d in paymentDates.Where(d => d > first && d <= to))
        {
            breakpoints.Add(d);
        }
        long previousOutflow = 0;
        foreach (var d in breakpoints)
        {
            var outflow = StatementOutflow(d);
            Add(deltas, d, previousOutflow - outflow);
            previousOutflow = outflow;
        }

        // Recorrências previstas (e atrasadas do mês): receitas e contas na conta; assinaturas no cartão, no vencimento da fatura.
        int plannedIncome = 0, plannedExpenses = 0, estimated = 0;
        HashSet<Guid> availableSet;
        lock (_gate)
        {
            availableSet = AvailableAccounts();
        }
        foreach (var o in OpenOccurrences(to))
        {
            if (!PlannedDelta(o, availableSet, out var date, out var amount))
            {
                continue;
            }
            Add(deltas, date, amount);
            if (o.Item.IsIncome)
            {
                plannedIncome++;
            }
            else
            {
                plannedExpenses++;
            }
            if (o.IsEstimated)
            {
                estimated++;
            }
        }

        var contributions = PlannedContributions(first, to);
        foreach (var c in contributions)
        {
            Add(deltas, c.Date, -c.Amount);
        }

        long? variableDaily = null;
        long variableTotal = 0;
        var variableDays = 0;
        if (includeVariable)
        {
            (variableTotal, variableDays) = VariableSpending(today);
            variableDaily = variableDays == 0 ? 0 : Money.DivideRounded(variableTotal, variableDays);
        }

        var simDeltas = new Dictionary<DateOnly, long>();
        var sims = simulations ?? [];
        foreach (var sim in sims)
        {
            if (Validate(sim) is { } error)
            {
                throw new FinanceValidationException(error);
            }
            switch (sim.Kind)
            {
                case SimulationKind.Income:
                    Add(simDeltas, sim.Date, sim.Amount);
                    break;
                case SimulationKind.CardPurchase:
                    foreach (var installment in Plan(new PurchaseDraft { CardId = sim.CardId!.Value, Date = sim.Date, Total = sim.Amount, Installments = sim.Installments }))
                    {
                        Add(simDeltas, installment.Due, -installment.Amount);
                    }
                    break;
                default:
                    Add(simDeltas, sim.Date, -sim.Amount);
                    break;
            }
        }

        var points = new List<ProjectionPoint>();
        var todayValue = checked(now - reserved);
        points.Add(new ProjectionPoint(today, todayValue, todayValue));
        var running = checked(start - reserved);
        long simulated = 0;
        var day = 0;
        for (var d = first; d <= to; d = d.AddDays(1))
        {
            day++;
            running = checked(running + deltas.GetValueOrDefault(d));
            simulated = checked(simulated + simDeltas.GetValueOrDefault(d));
            // Cumulativo exato (sem acumular arredondamentos dia a dia).
            var variable = variableDaily is null || variableDays == 0 ? 0 : Money.DivideRounded(checked(variableTotal * day), variableDays);
            var baseline = checked(running - variable);
            points.Add(new ProjectionPoint(d, baseline, checked(baseline + simulated)));
        }
        var months = points.GroupBy(p => (p.Date.Year, p.Date.Month))
            .Select(g => new ProjectionMonth(g.Key.Year, g.Key.Month, g.MinBy(p => p.Simulated)!, g.Last())).ToList();
        var assumptions = new ProjectionAssumptions
        {
            ScheduledTransactions = scheduled,
            PlannedIncome = plannedIncome,
            PlannedExpenses = plannedExpenses,
            Estimated = estimated,
            Statements = statements,
            PlannedContributions = contributions.Count,
            Reserved = reserved,
            VariableDaily = variableDaily,
            Simulations = sims.Count,
        };
        return new Projection(today, to, points, months, assumptions, Math.Max(0, safetyMargin));
    }

    /// <summary>Efeito de uma ocorrência prevista no disponível: data (conta: a da ocorrência; cartão: vencimento da fatura) e valor com sinal.</summary>
    private bool PlannedDelta(RecurringOccurrence o, HashSet<Guid> available, out DateOnly date, out long amount)
    {
        date = o.Date;
        amount = 0;
        if (o.Item.CardId is { } cardId)
        {
            if (Card(cardId) is not { IsArchived: false } card || card.Currency != BaseCurrency)
            {
                return false;
            }
            date = DueDate(card, StatementCalendar.ClosingFor(card, o.Date));
            amount = -o.Amount;
            return true;
        }
        if (o.Item.AccountId is not { } accountId || !available.Contains(accountId))
        {
            return false;
        }
        amount = o.Item.IsIncome ? o.Amount : -o.Amount;
        return true;
    }

    /// <summary>Gastos variáveis (despesas menos estornos, sem recorrências) dos 3 meses completos anteriores, e o número de dias.</summary>
    private (long Total, int Days) VariableSpending(DateOnly today)
    {
        var to = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var from = new DateOnly(to.Year, to.Month, 1).AddMonths(-2);
        long total = 0;
        lock (_gate)
        {
            foreach (var t in _transactions)
            {
                if (t.Date < from || t.Date > to || t.RecurringId is not null || CurrencyOf(t) != BaseCurrency)
                {
                    continue;
                }
                total = t.Kind switch
                {
                    TransactionKind.Expense => checked(total + t.Amount),
                    TransactionKind.Refund => checked(total - t.Amount),
                    _ => total,
                };
            }
        }
        return (Math.Max(0, total), to.DayNumber - from.DayNumber + 1);
    }

    /// <summary>
    /// Ocorrências previstas que entram no saldo previsto até <paramref name="end"/>, separadas: receitas e contas nas
    /// contas do disponível, e assinaturas no cartão (pelo vencimento da fatura, a partir de hoje).
    /// </summary>
    private (long Income, long Expense, long Card, int Count) PlannedUntil(DateOnly end)
    {
        var today = Today;
        if (end < today)
        {
            return (0, 0, 0, 0);
        }
        HashSet<Guid> available;
        lock (_gate)
        {
            available = AvailableAccounts();
        }
        long income = 0, expense = 0, card = 0;
        var count = 0;
        foreach (var o in OpenOccurrences(end))
        {
            if (!PlannedDelta(o, available, out var date, out var amount))
            {
                continue;
            }
            if (o.Item.CardId is not null)
            {
                if (date > end)
                {
                    continue;
                }
                card = checked(card - amount);
            }
            else if (amount > 0)
            {
                income = checked(income + amount);
            }
            else
            {
                expense = checked(expense - amount);
            }
            count++;
        }
        return (income, expense, card, count);
    }

    /// <summary>Efeito acumulado das ocorrências previstas no disponível, por dia (datas antes de amanhã contam amanhã).</summary>
    private Dictionary<DateOnly, long> PlannedDeltas(DateOnly to)
    {
        var first = Today.AddDays(1);
        HashSet<Guid> available;
        lock (_gate)
        {
            available = AvailableAccounts();
        }
        var result = new Dictionary<DateOnly, long>();
        foreach (var o in OpenOccurrences(to))
        {
            if (PlannedDelta(o, available, out var date, out var amount))
            {
                var d = date < first ? first : date;
                if (d <= to)
                {
                    result[d] = checked(result.GetValueOrDefault(d) + amount);
                }
            }
        }
        return result;
    }
}
