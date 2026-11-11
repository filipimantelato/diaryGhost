namespace DesktopHub.Core.Finance;

/// <summary>Regra de ciclo do cartão, válida para as faturas que fecham a partir de <see cref="EffectiveFrom"/>.</summary>
public sealed record CardCycleRule(DateOnly EffectiveFrom, int ClosingDay, int DueDay);

/// <summary>
/// Cartão de crédito. A dívida é a soma das partidas do cartão (compras e parcelas futuras −, pagamentos e
/// estornos +); nunca é armazenada. Mudar o fechamento/vencimento acrescenta uma regra: faturas passadas não se movem.
/// </summary>
public sealed record CreditCard
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Institution { get; init; }
    /// <summary>Bandeira (Visa, Mastercard, Elo…), texto livre.</summary>
    public string? Network { get; init; }
    public string? LastDigits { get; init; }
    public long Limit { get; init; }
    public string Currency { get; init; } = Money.DefaultCurrency;
    public string Color { get; init; } = "#9A86C4";
    /// <summary>Conta sugerida ao pagar a fatura.</summary>
    public Guid? PaymentAccountId { get; init; }
    /// <summary>Compras feitas no próprio dia do fechamento entram na fatura seguinte (padrão dos emissores brasileiros).</summary>
    public bool ClosingDayGoesNext { get; init; } = true;
    public required IReadOnlyList<CardCycleRule> Rules { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public int SortOrder { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>Regra mais recente (a que vale para as próximas faturas).</summary>
    public CardCycleRule CurrentRule => Rules.OrderBy(r => r.EffectiveFrom).Last();
}

/// <summary>Compra no cartão, à vista (1 parcela) ou parcelada; as parcelas são lançamentos com partida no cartão.</summary>
public sealed record CardPurchase
{
    public required Guid Id { get; init; }
    public required Guid CardId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Description { get; init; }
    public Guid? CategoryId { get; init; }
    public required long Total { get; init; }
    public required int Installments { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Vencimento corrigido à mão para uma fatura específica (ex.: banco prorrogou por feriado).</summary>
public sealed record StatementOverride(Guid CardId, DateOnly Closing, DateOnly Due);

public enum StatementStatus
{
    /// <summary>Ainda não começou (só parcelas futuras).</summary>
    Future,
    /// <summary>Recebendo compras.</summary>
    Open,
    /// <summary>Fechada, aguardando pagamento até o vencimento.</summary>
    Closed,
    /// <summary>Paga por completo (ou com crédito).</summary>
    Paid,
    /// <summary>Pagamento parcial; o restante passou para a próxima fatura.</summary>
    PartiallyPaid,
    /// <summary>Venceu sem pagamento suficiente.</summary>
    Overdue,
}

/// <summary>
/// Uma fatura calculada. <see cref="Charges"/> = compras/parcelas − estornos do ciclo; <see cref="Previous"/> = saldo
/// que veio da fatura anterior (positivo: não pago; negativo: crédito); <see cref="AmountDue"/> = anterior + ciclo;
/// <see cref="Remaining"/> = devido − pagamentos.
/// </summary>
public sealed record CardStatement(
    Guid CardId,
    DateOnly Opening,
    DateOnly Closing,
    DateOnly Due,
    long Charges,
    long Previous,
    long Payments,
    StatementStatus Status)
{
    public long AmountDue => Previous + Charges;
    public long Remaining => AmountDue - Payments;
}

/// <summary>Visão geral de um cartão.</summary>
public sealed record CardSummary(CreditCard Card, long Debt, long Available, CardStatement? OpenStatement, CardStatement? NextDue)
{
    public long Used => Debt;
}

/// <summary>
/// Cálculo de datas de fatura (puro, sem estado). Fechamento no dia D do mês (limitado ao último dia); uma compra
/// pertence à primeira fatura cujo fechamento é posterior a ela (no próprio dia, conforme o cartão). Vencimento =
/// primeira ocorrência do dia de vencimento depois do fechamento. Não desloca para dia útil (sem calendário de
/// feriados): o usuário pode corrigir o vencimento de uma fatura.
/// </summary>
public static class StatementCalendar
{
    public static DateOnly Clamp(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>
    /// Regra do mês: a mais recente cuja vigência começa até o dia 1 daquele mês. Assim cada mês tem exatamente
    /// um fechamento, e a troca de regra gera um ciclo de transição (mais curto ou mais longo), sem faturas sumindo.
    /// </summary>
    private static CardCycleRule RuleForMonth(CreditCard card, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(card);
        var monthStart = new DateOnly(year, month, 1);
        return card.Rules.OrderByDescending(r => r.EffectiveFrom).FirstOrDefault(r => r.EffectiveFrom <= monthStart)
            ?? card.Rules.OrderBy(r => r.EffectiveFrom).First();
    }

    /// <summary>Fechamento da fatura do mês dado, pela regra que vale para ele.</summary>
    public static DateOnly ClosingInMonth(CreditCard card, int year, int month) =>
        Clamp(year, month, RuleForMonth(card, year, month).ClosingDay);

    private static CardCycleRule RuleFor(CreditCard card, DateOnly closing) => RuleForMonth(card, closing.Year, closing.Month);

    /// <summary>Fechamento da fatura em que entra um lançamento do dia <paramref name="date"/>.</summary>
    public static DateOnly ClosingFor(CreditCard card, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(card);
        var month = new DateOnly(date.Year, date.Month, 1);
        for (var i = 0; i < 3; i++)
        {
            var m = month.AddMonths(i);
            var closing = ClosingInMonth(card, m.Year, m.Month);
            if (date < closing || (date == closing && !card.ClosingDayGoesNext))
            {
                return closing;
            }
        }
        throw new InvalidOperationException("Ciclo do cartão inválido.");
    }

    public static DateOnly NextClosing(CreditCard card, DateOnly closing)
    {
        ArgumentNullException.ThrowIfNull(card);
        var month = new DateOnly(closing.Year, closing.Month, 1);
        for (var i = 0; i < 3; i++)
        {
            var m = month.AddMonths(i);
            var candidate = ClosingInMonth(card, m.Year, m.Month);
            if (candidate > closing)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Ciclo do cartão inválido.");
    }

    public static DateOnly PreviousClosing(CreditCard card, DateOnly closing)
    {
        ArgumentNullException.ThrowIfNull(card);
        var month = new DateOnly(closing.Year, closing.Month, 1);
        for (var i = 0; i < 3; i++)
        {
            var m = month.AddMonths(-i);
            var candidate = ClosingInMonth(card, m.Year, m.Month);
            if (candidate < closing)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Ciclo do cartão inválido.");
    }

    /// <summary>Fechamento da k-ésima fatura a partir da primeira (k = 0 é a própria).</summary>
    public static DateOnly Advance(CreditCard card, DateOnly closing, int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            closing = NextClosing(card, closing);
        }
        return closing;
    }

    /// <summary>Primeiro dia que entra na fatura (dia seguinte ao fechamento anterior, ou o próprio dia dele).</summary>
    public static DateOnly Opening(CreditCard card, DateOnly closing)
    {
        ArgumentNullException.ThrowIfNull(card);
        var previous = PreviousClosing(card, closing);
        return card.ClosingDayGoesNext ? previous : previous.AddDays(1);
    }

    public static DateOnly Due(CreditCard card, DateOnly closing, IEnumerable<StatementOverride>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (overrides?.FirstOrDefault(o => o.CardId == card.Id && o.Closing == closing) is { } manual)
        {
            return manual.Due;
        }
        var rule = RuleFor(card, closing);
        var due = Clamp(closing.Year, closing.Month, rule.DueDay);
        if (due <= closing)
        {
            var next = closing.AddMonths(1);
            due = Clamp(next.Year, next.Month, rule.DueDay);
        }
        return due;
    }

    /// <summary>A fatura está fechada no dia <paramref name="today"/>?</summary>
    public static bool IsClosed(CreditCard card, DateOnly closing, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(card);
        return today > closing || (today == closing && card.ClosingDayGoesNext);
    }
}
