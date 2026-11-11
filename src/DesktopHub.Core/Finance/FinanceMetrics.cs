namespace DesktopHub.Core.Finance;

/// <summary>
/// Visão de um mês, com cada número definido de forma exata (ver docs/FINANCE.md, seção 3.4).
/// "Disponível" = contas ativas, na moeda base, marcadas como "entra no saldo disponível".
/// </summary>
/// <param name="Reference">Data de corte do "hoje" do mês: hoje no mês atual ou futuro; último dia num mês passado.</param>
/// <param name="AccountsBalance">Saldo em contas: confirmados até <paramref name="Reference"/>, contas do disponível.</param>
/// <param name="ToReceive">Receitas que ainda vão entrar até o fim do mês (pendentes, ou com data depois do corte).</param>
/// <param name="ToPay">Despesas que ainda vão sair até o fim do mês, já descontados estornos a receber.</param>
/// <param name="OtherChanges">Transferências com contas fora do disponível e ajustes, ainda por acontecer no mês.</param>
/// <param name="Forecast">Saldo previsto no fim do mês = saldo em contas + a receber − a pagar − faturas + outros (exato).</param>
/// <param name="OverduePending">Lançamentos pendentes com data anterior a hoje (atrasados ou esquecidos).</param>
/// <param name="CardStatements">Faturas que ainda vão sair do caixa até o fim do mês (só no mês atual e futuros).</param>
/// <param name="CardSpending">Gastos no cartão do mês (competência: data de cada parcela), menos estornos no cartão.</param>
/// <param name="StatementsDue">Soma do valor devido das faturas que vencem no mês.</param>
public sealed record MonthOverview(
    int Year,
    int Month,
    DateOnly Reference,
    long AccountsBalance,
    long ToReceive,
    long ToPay,
    long OtherChanges,
    long Forecast,
    long NetWorth,
    PeriodTotals Totals,
    PeriodTotals PreviousTotals,
    long PreviousForecast,
    int OverduePending,
    bool HasPreviousMonth)
{
    public DateOnly End => FinanceService.MonthRange(Year, Month).To;
    public long CardStatements { get; init; }
    public long CardSpending { get; init; }
    public long StatementsDue { get; init; }
    public int OverdueStatements { get; init; }
}

public sealed record MonthFlow(int Year, int Month, long Income, long Expense)
{
    public long Net => Income - Expense;
}

/// <summary>Gasto de uma categoria principal (somando as subcategorias), descontados os estornos.</summary>
public sealed record CategorySpending(Guid? CategoryId, string Name, string Color, long Amount, IReadOnlyList<CategorySpending> Children);

/// <summary>Saldo do disponível ao fim de um dia; <see cref="IsForecast"/> para dias depois de hoje.</summary>
public sealed record DailyBalance(DateOnly Date, long Balance, bool IsForecast);

/// <summary>Variação entre dois valores; percentual ausente quando a base é zero.</summary>
public sealed record Change(long Current, long Previous)
{
    public long Delta => Current - Previous;
    public decimal? Percent => Money.Percent(Delta, Math.Abs(Previous));
}

public sealed partial class FinanceService
{
    /// <summary>Contas que entram no "disponível".</summary>
    private HashSet<Guid> AvailableAccounts() =>
        [.. _accounts.Where(a => !a.IsArchived && a.IncludeInAvailable && a.Currency == BaseCurrency).Select(a => a.Id)];

    public DateOnly ReferenceDate(int year, int month)
    {
        var today = Today;
        var (_, end) = MonthRange(year, month);
        return end < today ? end : today;
    }

    public MonthOverview Overview(int year, int month)
    {
        var (start, end) = MonthRange(year, month);
        var reference = ReferenceDate(year, month);
        long balance, forecast, toReceive = 0, toPay = 0, netWorth;
        int overdue;
        var today = Today;
        lock (_gate)
        {
            var available = AvailableAccounts();
            var accounts = _accounts.Where(a => available.Contains(a.Id)).ToList();
            balance = Money.Sum(accounts.Select(a => BalanceCore(a, reference, includePending: false)));
            forecast = Money.Sum(accounts.Select(a => BalanceCore(a, end, includePending: true)));
            foreach (var t in _transactions)
            {
                // Tudo que entra no previsto mas ainda não está no saldo do corte.
                if (t.Date > end || (t.Status == TransactionStatus.Cleared && t.Date <= reference) || t.Kind is not (TransactionKind.Income or TransactionKind.Expense or TransactionKind.Refund))
                {
                    continue;
                }
                foreach (var p in t.Postings.Where(p => available.Contains(p.AccountId)))
                {
                    if (t.Kind == TransactionKind.Income)
                    {
                        toReceive = checked(toReceive + p.Amount);
                    }
                    else
                    {
                        toPay = checked(toPay - p.Amount);
                    }
                }
            }
            overdue = _transactions.Count(t => t.Status == TransactionStatus.Pending && t.Date < today);
        }
        // "Outros" fecha a conta por construção: transferências com contas fora do disponível, ajustes e
        // saldos iniciais de contas abertas depois do corte. Assim a decomposição sempre soma o previsto.
        var other = checked(forecast - balance - toReceive + toPay);
        netWorth = NetWorthAt(reference);
        // Faturas saem do caixa no vencimento: entram no previsto do mês atual e dos futuros. Num mês passado o
        // "previsto" é o saldo do último dia, que já reflete os pagamentos feitos.
        var statementsOut = end > today ? StatementOutflow(end) : 0;
        forecast = checked(forecast - statementsOut);

        long cardSpending = 0;
        lock (_gate)
        {
            foreach (var t in _transactions)
            {
                if (t.CardId is null || t.Date < start || t.Date > end || CurrencyOf(t) != BaseCurrency)
                {
                    continue;
                }
                cardSpending = t.Kind switch
                {
                    TransactionKind.Expense => checked(cardSpending + t.Amount),
                    TransactionKind.Refund => checked(cardSpending - t.Amount),
                    _ => cardSpending,
                };
            }
        }
        long statementsDue = 0;
        var overdueStatements = 0;
        foreach (var card in Cards.Where(c => !c.IsArchived && c.Currency == BaseCurrency))
        {
            var statements = Statements(card.Id);
            statementsDue = checked(statementsDue + Money.Sum(statements.Where(s => s.Due >= start && s.Due <= end).Select(s => s.AmountDue)));
            if (statements.LastOrDefault(s => s.Due < today) is { Status: StatementStatus.Overdue or StatementStatus.PartiallyPaid })
            {
                overdueStatements++;
            }
        }

        var previous = start.AddMonths(-1);
        var (prevStart, prevEnd) = MonthRange(previous.Year, previous.Month);
        // Comparar com um mês em que nenhuma conta existia ainda daria "+100%" enganoso: sem comparação.
        var hasPrevious = Accounts.Any(a => a.OpeningDate <= prevEnd);
        return new MonthOverview(year, month, reference, balance, toReceive, toPay, other, forecast, netWorth,
            Totals(start, end), Totals(prevStart, prevEnd), AvailableForecastAt(prevEnd), overdue, hasPrevious)
        {
            CardStatements = statementsOut,
            CardSpending = cardSpending,
            StatementsDue = statementsDue,
            OverdueStatements = overdueStatements,
        };
    }

    /// <summary>Saldo do disponível ao fim de um dia, com pendentes (o "previsto" daquele dia).</summary>
    public long AvailableForecastAt(DateOnly date)
    {
        lock (_gate)
        {
            var available = AvailableAccounts();
            return Money.Sum(_accounts.Where(a => available.Contains(a.Id)).Select(a => BalanceCore(a, date, includePending: true)));
        }
    }

    /// <summary>Receitas e despesas (com pendentes) dos <paramref name="count"/> meses terminando no mês dado.</summary>
    public IReadOnlyList<MonthFlow> MonthlyFlows(int year, int month, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var last = new DateOnly(year, month, 1);
        var list = new List<MonthFlow>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            var m = last.AddMonths(-i);
            var (from, to) = MonthRange(m.Year, m.Month);
            var totals = Totals(from, to);
            list.Add(new MonthFlow(m.Year, m.Month, totals.Income, totals.Expense));
        }
        return list;
    }

    /// <summary>
    /// Gasto por categoria principal no período (com pendentes, moeda base): despesas menos estornos.
    /// Ordem decrescente; "Sem categoria" entra como uma linha própria.
    /// </summary>
    public IReadOnlyList<CategorySpending> SpendingByCategory(DateOnly from, DateOnly to)
    {
        var byCategory = new Dictionary<Guid, long>();
        long uncategorized = 0;
        lock (_gate)
        {
            foreach (var t in _transactions)
            {
                if (t.Date < from || t.Date > to || t.Kind is not (TransactionKind.Expense or TransactionKind.Refund) || CurrencyOf(t) != BaseCurrency)
                {
                    continue;
                }
                var amount = t.Kind == TransactionKind.Expense ? t.Amount : -t.Amount;
                if (t.CategoryId is { } c)
                {
                    byCategory[c] = checked(byCategory.GetValueOrDefault(c) + amount);
                }
                else
                {
                    uncategorized = checked(uncategorized + amount);
                }
            }
        }
        var categories = Categories.ToDictionary(c => c.Id);
        var result = new List<CategorySpending>();
        foreach (var group in byCategory.GroupBy(kv => categories.TryGetValue(kv.Key, out var c) ? c.ParentId ?? c.Id : kv.Key))
        {
            var parent = categories.GetValueOrDefault(group.Key);
            var children = group.Where(kv => kv.Key != group.Key)
                .Select(kv => new CategorySpending(kv.Key, categories.GetValueOrDefault(kv.Key)?.Name ?? "?", categories.GetValueOrDefault(kv.Key)?.Color ?? "#8A8A8A", kv.Value, []))
                .OrderByDescending(c => c.Amount).ToList();
            result.Add(new CategorySpending(group.Key, parent?.Name ?? "Categoria removida", parent?.Color ?? "#8A8A8A", Money.Sum(group.Select(kv => kv.Value)), children));
        }
        if (uncategorized != 0)
        {
            result.Add(new CategorySpending(null, "Sem categoria", "#8A8A8A", uncategorized, []));
        }
        return [.. result.Where(c => c.Amount != 0).OrderByDescending(c => c.Amount)];
    }

    /// <summary>
    /// Saldo do disponível ao fim de cada dia do mês: confirmado até hoje; do dia seguinte em diante, previsto
    /// (com pendentes e com as faturas que vencem até o dia). Dias antes da abertura de uma conta contam zero para ela.
    /// </summary>
    public IReadOnlyList<DailyBalance> DailyBalances(int year, int month)
    {
        var (start, end) = MonthRange(year, month);
        var today = Today;
        var result = new List<DailyBalance>();
        var outflows = new Dictionary<DateOnly, long>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d > today)
            {
                outflows[d] = StatementOutflow(d);
            }
        }
        lock (_gate)
        {
            var available = AvailableAccounts();
            var accounts = _accounts.Where(a => available.Contains(a.Id)).ToList();
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                var forecast = d > today;
                var value = Money.Sum(accounts.Select(a => BalanceCore(a, d, includePending: forecast)));
                result.Add(new DailyBalance(d, forecast ? checked(value - outflows[d]) : value, forecast));
            }
        }
        return result;
    }

    /// <summary>Pendentes atrasados (antes de hoje) e os próximos até <paramref name="until"/>, por data.</summary>
    public IReadOnlyList<FinanceTransaction> PendingUntil(DateOnly until) =>
        [.. Transactions.Where(t => t.Status == TransactionStatus.Pending && t.Date <= until).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt)];
}
