namespace DesktopHub.Core.Finance;

// ---------- Recorrências, contas a pagar e assinaturas ----------

public enum RecurringKind
{
    /// <summary>Conta a pagar (luz, aluguel, internet…).</summary>
    Bill,
    Subscription,
    Income,
}

public enum RecurringFrequency
{
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// Modelo de um lançamento que se repete. As ocorrências são calculadas (previstas) até serem confirmadas: confirmar
/// cria o lançamento real com <c>(RecurringId, OccurrenceDate)</c> único, então confirmar duas vezes não duplica.
/// Dia 29–31 em mês mais curto cai no último dia do mês (conta de "todo dia 31" vence em 30/04, não some).
/// </summary>
public sealed record RecurringItem
{
    public required Guid Id { get; init; }
    public RecurringKind Kind { get; init; } = RecurringKind.Bill;
    public required string Description { get; init; }
    /// <summary>Valor fixo, ou o valor de partida quando <see cref="IsEstimated"/> (antes de haver ocorrências confirmadas).</summary>
    public long Amount { get; init; }
    /// <summary>Valor variável: estimado pela média das 3 últimas ocorrências confirmadas.</summary>
    public bool IsEstimated { get; init; }
    /// <summary>Conta que paga/recebe (exatamente um entre conta e cartão).</summary>
    public Guid? AccountId { get; init; }
    /// <summary>Assinatura cobrada no cartão: confirmar cria uma compra à vista.</summary>
    public Guid? CardId { get; init; }
    public Guid? CategoryId { get; init; }
    public RecurringFrequency Frequency { get; init; } = RecurringFrequency.Monthly;
    public int Interval { get; init; } = 1;
    /// <summary>Primeira ocorrência; define o dia (do mês, da semana ou do ano) das seguintes.</summary>
    public required DateOnly StartDate { get; init; }
    /// <summary>Última data possível (inclusiva); nulo = sem fim.</summary>
    public DateOnly? EndDate { get; init; }
    /// <summary>Avisar N dias antes (lembretes chegam na Fase 12).</summary>
    public int RemindDaysBefore { get; init; } = 3;
    public string? Notes { get; init; }
    /// <summary>Pausada/encerrada: deixa de gerar ocorrências previstas; o histórico fica.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsArchived => ArchivedAt is not null;

    public bool IsIncome => Kind == RecurringKind.Income;
}

/// <summary>Ocorrência pulada (ex.: mês sem cobrança).</summary>
public sealed record RecurringSkip(Guid RecurringId, DateOnly Date);

public enum OccurrenceState
{
    /// <summary>Prevista, ainda por vir.</summary>
    Planned,
    /// <summary>Data já passou e não foi confirmada nem pulada.</summary>
    Overdue,
    Confirmed,
    Skipped,
}

/// <param name="Amount">Valor real (confirmada) ou o previsto (fixo ou estimado).</param>
/// <param name="TransactionId">Lançamento criado ao confirmar.</param>
public sealed record RecurringOccurrence(RecurringItem Item, DateOnly Date, long Amount, bool IsEstimated, OccurrenceState State, Guid? TransactionId)
{
    /// <summary>Entra nas previsões: prevista, ou atrasada do mês atual (ainda vai sair).</summary>
    public bool IsOpen => State is OccurrenceState.Planned or OccurrenceState.Overdue;
}

/// <summary>Datas de uma recorrência: semanal, mensal (dia limitado ao fim do mês) ou anual, a cada N períodos.</summary>
public static class RecurringSchedule
{
    public const int MaxInterval = 24;

    /// <summary>A n-ésima ocorrência (n = 0 é o início).</summary>
    public static DateOnly Nth(RecurringItem item, int n)
    {
        ArgumentNullException.ThrowIfNull(item);
        var start = item.StartDate;
        return item.Frequency switch
        {
            RecurringFrequency.Weekly => start.AddDays(checked(7 * item.Interval * n)),
            RecurringFrequency.Monthly => Clamp(new DateOnly(start.Year, start.Month, 1).AddMonths(checked(item.Interval * n)), start.Day),
            RecurringFrequency.Yearly => Clamp(new DateOnly(start.Year, start.Month, 1).AddYears(checked(item.Interval * n)), start.Day),
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };
    }

    private static DateOnly Clamp(DateOnly firstOfMonth, int day) =>
        new(firstOfMonth.Year, firstOfMonth.Month, Math.Min(day, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month)));

    /// <summary>Ocorrências entre <paramref name="from"/> e <paramref name="to"/> (inclusivas), em ordem.</summary>
    public static IEnumerable<DateOnly> Between(RecurringItem item, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Interval < 1)
        {
            yield break;
        }
        var last = item.EndDate is { } end && end < to ? end : to;
        if (last < item.StartDate || from > last)
        {
            yield break;
        }
        // Pula direto para perto de "from" (sem percorrer anos de ocorrências).
        var n = 0;
        if (from > item.StartDate)
        {
            var periods = item.Frequency switch
            {
                RecurringFrequency.Weekly => (from.DayNumber - item.StartDate.DayNumber) / 7,
                RecurringFrequency.Monthly => ((from.Year - item.StartDate.Year) * 12) + from.Month - item.StartDate.Month,
                _ => from.Year - item.StartDate.Year,
            };
            n = Math.Max(0, (periods / item.Interval) - 1);
        }
        for (var guard = 0; guard < 2000; guard++, n++)
        {
            var date = Nth(item, n);
            if (date > last)
            {
                yield break;
            }
            if (date >= from)
            {
                yield return date;
            }
        }
    }

    /// <summary>Próxima ocorrência a partir de <paramref name="from"/> (inclusiva), ou nula se a recorrência terminou.</summary>
    public static DateOnly? Next(RecurringItem item, DateOnly from) =>
        Between(item, from, from.AddYears(item.Interval * 2 + 1)).Select(d => (DateOnly?)d).FirstOrDefault();

    /// <summary>Custo anual equivalente (semanal conta 52 semanas por ano).</summary>
    public static long YearlyCost(RecurringItem item, long amount)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Frequency switch
        {
            RecurringFrequency.Weekly => Money.DivideRounded(checked(amount * 52), item.Interval),
            RecurringFrequency.Monthly => Money.DivideRounded(checked(amount * 12), item.Interval),
            _ => Money.DivideRounded(amount, item.Interval),
        };
    }

    public static long MonthlyCost(RecurringItem item, long amount) => Money.DivideRounded(YearlyCost(item, amount), 12);
}

// ---------- Orçamentos ----------

/// <summary>
/// Limite de gasto para uma categoria principal (inclui as subcategorias). <see cref="Month"/> nulo = vale para
/// todo mês; um mês específico substitui o padrão naquele mês (limite 0 = sem orçamento naquele mês).
/// </summary>
public sealed record Budget
{
    public required Guid Id { get; init; }
    public required Guid CategoryId { get; init; }
    /// <summary>Primeiro dia do mês, ou nulo para o padrão.</summary>
    public DateOnly? Month { get; init; }
    public required long Limit { get; init; }
    /// <summary>Faixa de atenção, em % do limite (a de estouro é sempre 100%).</summary>
    public int WarnPercent { get; init; } = 80;
}

public enum BudgetState
{
    Ok,
    Warning,
    Over,
}

/// <param name="Spent">Despesa reconhecida no mês (confirmadas e pendentes), menos estornos; transferências, pagamentos de fatura e ajustes nunca entram.</param>
/// <param name="Planned">Contas e assinaturas previstas no mês nessa categoria, ainda não confirmadas.</param>
/// <param name="IsDefault">O limite vem do orçamento padrão (não de um ajuste só deste mês).</param>
public sealed record BudgetLine(TransactionCategory Category, Budget Budget, bool IsDefault, long Spent, long Planned)
{
    public long Limit => Budget.Limit;
    public long Remaining => Limit - Spent;
    public decimal? Percent => Money.Percent(Spent, Limit);
    public BudgetState State => Spent > Limit ? BudgetState.Over
        : Limit > 0 && Spent * 100 >= (long)Budget.WarnPercent * Limit ? BudgetState.Warning
        : BudgetState.Ok;
    /// <summary>Gasto + previsto passa do limite (vai estourar se nada mudar).</summary>
    public bool WillExceed => Spent + Planned > Limit;
}

/// <param name="UnbudgetedSpent">Gasto do mês em categorias sem orçamento (e sem categoria).</param>
public sealed record BudgetMonth(int Year, int Month, IReadOnlyList<BudgetLine> Lines, long UnbudgetedSpent)
{
    public long TotalLimit => Money.Sum(Lines.Select(l => l.Limit));
    public long TotalSpent => Money.Sum(Lines.Select(l => l.Spent));
    public long TotalPlanned => Money.Sum(Lines.Select(l => l.Planned));
    public long TotalRemaining => TotalLimit - TotalSpent;
}

// ---------- Metas ----------

/// <summary>Meta de economia. Com conta própria (ex.: poupança), contribuir é transferir para ela; sem, é reservar dentro de uma conta.</summary>
public sealed record FinancialGoal
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required long Target { get; init; }
    public DateOnly? Deadline { get; init; }
    public Guid? AccountId { get; init; }
    public string Color { get; init; } = "#5FB3A8";
    public DateTimeOffset? ArchivedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsArchived => ArchivedAt is not null;
}

public enum ContributionKind
{
    /// <summary>Dinheiro efetivamente separado: entra no "Reservado".</summary>
    Real,
    /// <summary>Só intenção ("guardar R$ 200 dia 10"): entra nas projeções, nunca no "Reservado".</summary>
    Planned,
}

/// <param name="AccountId">Real: conta onde o dinheiro está (a da meta, ou a conta da reserva). Planejada: conta de origem prevista (opcional).</param>
/// <param name="TransactionId">Transferência criada quando a meta tem conta própria (excluir a contribuição exclui a transferência).</param>
public sealed record GoalContribution
{
    public required Guid Id { get; init; }
    public required Guid GoalId { get; init; }
    public required DateOnly Date { get; init; }
    /// <summary>Positivo = guardar; negativo = retirar.</summary>
    public required long Amount { get; init; }
    public ContributionKind Kind { get; init; } = ContributionKind.Real;
    public Guid? AccountId { get; init; }
    public Guid? TransactionId { get; init; }
    public string? Notes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <param name="Real">Guardado de fato até hoje.</param>
/// <param name="PlannedAhead">Contribuições planejadas de hoje em diante.</param>
/// <param name="MonthlyNeeded">Quanto guardar por mês (de agora até o mês do prazo, inclusive) para chegar; nulo sem prazo.</param>
public sealed record GoalProgress(FinancialGoal Goal, long Real, long PlannedAhead, int? MonthsLeft, long? MonthlyNeeded)
{
    public long Remaining => Math.Max(0, Goal.Target - Real);
    public decimal? Percent => Money.Percent(Real, Goal.Target);
    public decimal? PercentWithPlanned => Money.Percent(Real + PlannedAhead, Goal.Target);
    public bool Reached => Real >= Goal.Target;
    /// <summary>Prazo passou sem atingir o alvo.</summary>
    public bool Late { get; init; }
}
