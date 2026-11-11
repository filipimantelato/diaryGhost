using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

public sealed class RecurringScheduleTests
{
    private static RecurringItem Item(RecurringFrequency frequency, DateOnly start, int interval = 1, DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        Description = "x",
        Amount = 100,
        AccountId = Guid.NewGuid(),
        Frequency = frequency,
        Interval = interval,
        StartDate = start,
        EndDate = end,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Monthly_on_31st_falls_on_last_day_of_shorter_months()
    {
        var item = Item(RecurringFrequency.Monthly, new DateOnly(2026, 10, 31));
        Assert.Equal(
            [new(2026, 10, 31), new(2026, 11, 30), new(2026, 12, 31), new(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31)],
            RecurringSchedule.Between(item, new DateOnly(2026, 1, 1), new DateOnly(2027, 3, 31)).ToArray());
    }

    [Fact]
    public void Yearly_on_february_29_uses_28_in_common_years()
    {
        var item = Item(RecurringFrequency.Yearly, new DateOnly(2028, 2, 29));
        Assert.Equal([new(2028, 2, 29), new(2029, 2, 28), new(2032, 2, 29)],
            RecurringSchedule.Between(item, new DateOnly(2028, 1, 1), new DateOnly(2032, 12, 31)).Where(d => d.Year is 2028 or 2029 or 2032).ToArray());
    }

    [Fact]
    public void Weekly_with_interval_and_end_date_skips_directly_to_range()
    {
        var item = Item(RecurringFrequency.Weekly, new DateOnly(2020, 1, 6), interval: 2, end: new DateOnly(2026, 12, 1));
        var dates = RecurringSchedule.Between(item, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)).ToArray();
        Assert.All(dates, d => Assert.Equal(0, (d.DayNumber - item.StartDate.DayNumber) % 14));
        Assert.Equal(new DateOnly(2026, 11, 2), dates[0]);
        Assert.True(dates[^1] <= new DateOnly(2026, 12, 1));
        Assert.Equal(new DateOnly(2026, 11, 30), dates[^1]);
    }

    [Fact]
    public void Equivalent_costs_are_exact_integers()
    {
        var monthly = Item(RecurringFrequency.Monthly, new DateOnly(2026, 1, 1));
        Assert.Equal(67_080, RecurringSchedule.YearlyCost(monthly, 5_590));
        Assert.Equal(5_590, RecurringSchedule.MonthlyCost(monthly, 5_590));
        var weekly = Item(RecurringFrequency.Weekly, new DateOnly(2026, 1, 1));
        Assert.Equal(52_000, RecurringSchedule.YearlyCost(weekly, 1_000));
        Assert.Equal(4_333, RecurringSchedule.MonthlyCost(weekly, 1_000));
        var yearly = Item(RecurringFrequency.Yearly, new DateOnly(2026, 1, 1));
        Assert.Equal(1_000, RecurringSchedule.MonthlyCost(yearly, 12_000));
        var bimonthly = Item(RecurringFrequency.Monthly, new DateOnly(2026, 1, 1), interval: 2);
        Assert.Equal(60_000, RecurringSchedule.YearlyCost(bimonthly, 10_000));
    }
}

public sealed class PlanningTests
{
    // Hoje = 15/11/2026 (UTC). Banco com R$ 1.000,00 desde 01/09. Cartão: fecha dia 3, vence dia 10.
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int m, int d, int y = 2026) => new(y, m, d);

    private sealed record Ctx(FinanceService S, MemoryFinanceStore Store, Guid Bank, Guid Card);

    private static Ctx Create(long bank = 100_000)
    {
        var store = new MemoryFinanceStore();
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        var bankId = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = bank, OpeningDate = D(9, 1) }).Id;
        var card = s.SaveCard(s.NewCard() with { Name = "Roxo", Limit = 500_000, PaymentAccountId = bankId, Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)] }).Id;
        return new Ctx(s, store, bankId, card);
    }

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    private static RecurringItem Bill(Ctx c, long amount, DateOnly start, string category = "Energia", bool estimated = false, RecurringKind kind = RecurringKind.Bill, bool onCard = false) =>
        c.S.SaveRecurring(c.S.NewRecurring(kind) with
        {
            Description = kind == RecurringKind.Income ? "Freela" : "Conta",
            Amount = amount,
            IsEstimated = estimated,
            StartDate = start,
            AccountId = onCard ? null : c.Bank,
            CardId = onCard ? c.Card : null,
            CategoryId = Cat(c.S, kind == RecurringKind.Income ? "Trabalhos extras" : category),
        });

    // ---------- Recorrências ----------

    [Fact]
    public void Confirming_creates_one_linked_transaction_and_never_duplicates()
    {
        var c = Create();
        var bill = Bill(c, 15_000, D(11, 20));
        var t = c.S.Confirm(bill.Id, D(11, 20), paidOn: D(11, 14), amount: 14_870);
        Assert.Equal(TransactionKind.Expense, t.Kind);
        Assert.Equal((bill.Id, D(11, 20)), (t.RecurringId!.Value, t.OccurrenceDate!.Value));
        Assert.Equal(14_870, t.Amount);
        Assert.Equal(D(11, 14), t.Date);
        Assert.Throws<FinanceValidationException>(() => c.S.Confirm(bill.Id, D(11, 20)));
        Assert.Throws<FinanceValidationException>(() => c.S.Confirm(bill.Id, D(11, 21))); // não é ocorrência
        // Um lançamento comum não pode reivindicar a mesma ocorrência.
        Assert.NotNull(c.S.Validate(new TransactionDraft
        {
            Kind = TransactionKind.Expense, Date = D(11, 20), Description = "x", Amount = 10, AccountId = c.Bank, RecurringId = bill.Id, OccurrenceDate = D(11, 20),
        }));
        var occ = c.S.Occurrences(D(11, 1), D(12, 31));
        Assert.Equal([OccurrenceState.Confirmed, OccurrenceState.Planned], occ.Select(o => o.State).ToArray());
        Assert.Equal(14_870, occ[0].Amount);
        Assert.Single(c.Store.Transactions.Values, x => x.RecurringId == bill.Id);
    }

    [Fact]
    public void Skip_overdue_and_estimate_by_average_of_last_three()
    {
        var c = Create();
        var bill = Bill(c, 10_000, D(9, 5), estimated: true);
        Assert.Equal(10_000, c.S.ExpectedAmount(bill)); // sem histórico: o valor de partida
        c.S.Confirm(bill.Id, D(9, 5), amount: 12_000);
        c.S.Confirm(bill.Id, D(10, 5), amount: 14_000);
        c.S.Confirm(bill.Id, D(11, 5), amount: 20_000);
        Assert.Equal(15_333, c.S.ExpectedAmount(bill)); // (12000 + 14000 + 20000) / 3
        c.S.Skip(bill.Id, D(12, 5));
        var occ = c.S.Occurrences(D(12, 1), D(1, 31, 2027));
        Assert.Equal(OccurrenceState.Skipped, occ[0].State);
        Assert.Equal(OccurrenceState.Planned, occ[1].State);
        Assert.True(occ[1].IsEstimated);
        Assert.Throws<FinanceValidationException>(() => c.S.Skip(bill.Id, D(11, 5))); // já confirmada

        var late = Bill(c, 5_000, D(11, 10));
        Assert.Equal(OccurrenceState.Overdue, c.S.Occurrences(D(11, 1), D(11, 30)).Single(o => o.Item.Id == late.Id).State);
    }

    [Fact]
    public void Forecast_includes_planned_occurrences_once_before_and_after_confirming()
    {
        var c = Create();
        var bill = Bill(c, 15_000, D(11, 20));
        Bill(c, 5_000, D(11, 25), kind: RecurringKind.Income);
        var o = c.S.Overview(2026, 11);
        Assert.Equal(100_000, o.AccountsBalance);
        Assert.Equal(15_000, o.ToPay);
        Assert.Equal(5_000, o.ToReceive);
        Assert.Equal(15_000, o.PlannedExpense);
        Assert.Equal(5_000, o.PlannedIncome);
        Assert.Equal(2, o.PlannedCount);
        Assert.Equal(90_000, o.Forecast);
        Assert.Equal(o.Forecast, o.AccountsBalance + o.ToReceive - o.ToPay - o.CardStatements + o.OtherChanges);
        Assert.Equal(o.Forecast, c.S.DailyBalances(2026, 11)[^1].Balance);

        c.S.Confirm(bill.Id, D(11, 20));
        var after = c.S.Overview(2026, 11);
        Assert.Equal(90_000, after.Forecast); // confirmar não conta duas vezes
        Assert.Equal(0, after.PlannedExpense);
        Assert.Equal(15_000, after.ToPay);
        // Dezembro acumula: as de dezembro entram, as de novembro já estão no saldo previsto.
        Assert.Equal(90_000 - 15_000 + 5_000, c.S.Overview(2026, 12).Forecast);
        // Mês passado não ganha previstas.
        Assert.Equal(0, c.S.Overview(2026, 10).PlannedCount);
    }

    [Fact]
    public void Overdue_of_current_month_counts_but_older_months_do_not()
    {
        var c = Create();
        Bill(c, 7_000, D(10, 5)); // 05/10 (mês passado, não confirmada) e 05/11 (atrasada no mês)
        var o = c.S.Overview(2026, 11);
        Assert.Equal(7_000, o.PlannedExpense);
        Assert.Equal(93_000, o.Forecast);
    }

    [Fact]
    public void Card_subscription_leaves_cash_on_the_statement_due_date()
    {
        var c = Create();
        var sub = Bill(c, 3_990, D(11, 20), category: "Assinaturas", kind: RecurringKind.Subscription, onCard: true);
        // 20/11 cai na fatura que fecha 03/12 e vence 10/12: fora do previsto de novembro.
        Assert.Equal(100_000, c.S.Overview(2026, 11).Forecast);
        var dec = c.S.Overview(2026, 12);
        Assert.Equal(3_990, dec.CardStatements);
        Assert.Equal(96_010, dec.Forecast);

        var installment = c.S.Confirm(sub.Id, D(11, 20));
        Assert.Equal(c.Card, installment.CardId);
        Assert.Equal(D(12, 3), installment.StatementClosing);
        Assert.Equal(sub.Id, installment.RecurringId);
        Assert.Equal(96_010, c.S.Overview(2026, 12).Forecast); // agora vem da fatura real, uma vez só
        // Editar a compra mantém o vínculo com a assinatura.
        var purchase = c.S.Purchase(installment.PurchaseId)!;
        c.S.SavePurchase(FinanceService.DraftFrom(purchase) with { Description = "Streaming" });
        Assert.Equal(sub.Id, c.S.Installments(purchase.Id)[0].RecurringId);
        var (monthly, yearly) = c.S.SubscriptionTotals();
        Assert.Equal((3_990, 47_880), (monthly, yearly));
    }

    [Fact]
    public void Deleting_recurring_keeps_confirmed_transactions_unlinked()
    {
        var c = Create();
        var bill = Bill(c, 15_000, D(11, 1));
        var t = c.S.Confirm(bill.Id, D(11, 1));
        Assert.False(c.S.CanDeleteAccount(c.Bank));
        c.S.DeleteRecurring(bill.Id);
        var kept = c.S.Find(t.Id)!;
        Assert.Null(kept.RecurringId);
        Assert.Null(kept.OccurrenceDate);
        Assert.Equal(15_000, kept.Amount);
        Assert.Empty(c.S.Occurrences(D(11, 1), D(12, 31), includeArchived: true));
    }

    [Fact]
    public void Paused_recurring_stops_forecasting_but_keeps_history()
    {
        var c = Create();
        var bill = Bill(c, 15_000, D(11, 1));
        c.S.Confirm(bill.Id, D(11, 1));
        c.S.SetArchived(bill, archived: true);
        Assert.Equal(0, c.S.Overview(2026, 12).PlannedCount);
        Assert.Single(c.S.Occurrences(D(11, 1), D(12, 31), includeArchived: true));
    }

    [Fact]
    public void Recurring_validation()
    {
        var c = Create();
        var item = c.S.NewRecurring() with { Description = "Luz", Amount = 100 };
        Assert.Null(c.S.Validate(item));
        Assert.NotNull(c.S.Validate(item with { Amount = 0 }));
        Assert.NotNull(c.S.Validate(item with { CardId = c.Card })); // conta e cartão juntos
        Assert.NotNull(c.S.Validate(item with { Kind = RecurringKind.Income, AccountId = null, CardId = c.Card }));
        Assert.NotNull(c.S.Validate(item with { EndDate = item.StartDate.AddDays(-1) }));
        Assert.NotNull(c.S.Validate(item with { Interval = 0 }));
        Assert.NotNull(c.S.Validate(item with { CategoryId = Cat(c.S, "Salário") }));
    }

    // ---------- Orçamentos ----------

    [Fact]
    public void Budget_counts_subcategories_refunds_and_planned_with_month_override()
    {
        var c = Create();
        var food = Cat(c.S, "Alimentação");
        c.S.SetBudget(food, null, 50_000);
        c.S.SetBudget(food, D(11, 1), 40_000);
        c.S.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 2), Description = "Mercado", Amount = 30_000, AccountId = c.Bank, CategoryId = Cat(c.S, "Mercado") });
        var dinner = c.S.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 3), Description = "Jantar", Amount = 5_000, AccountId = c.Bank, CategoryId = Cat(c.S, "Restaurantes") });
        c.S.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = D(11, 4), Description = "Estorno", Amount = 2_000, AccountId = c.Bank, RefundOfId = dinner.Id });
        c.S.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 5), Description = "Luz", Amount = 9_000, AccountId = c.Bank, CategoryId = Cat(c.S, "Energia") });
        Bill(c, 10_000, D(11, 28), category: "Delivery");

        var nov = c.S.BudgetMonth(2026, 11);
        var line = Assert.Single(nov.Lines);
        Assert.False(line.IsDefault);
        Assert.Equal(40_000, line.Limit);
        Assert.Equal(33_000, line.Spent);
        Assert.Equal(82.5m, line.Percent);
        Assert.Equal(BudgetState.Warning, line.State);
        Assert.Equal(10_000, line.Planned);
        Assert.True(line.WillExceed);
        Assert.Equal(7_000, line.Remaining);
        Assert.Equal(9_000, nov.UnbudgetedSpent);

        var dec = c.S.BudgetMonth(2026, 12);
        Assert.True(Assert.Single(dec.Lines).IsDefault);
        Assert.Equal(50_000, dec.TotalLimit);

        c.S.SetBudget(food, D(12, 1), 0); // dezembro sem orçamento
        Assert.Empty(c.S.BudgetMonth(2026, 12).Lines);
        c.S.RemoveBudget(food, D(12, 1));
        Assert.Single(c.S.BudgetMonth(2026, 12).Lines);

        Assert.Equal(1, c.S.CopyBudgetsFromPrevious(2027, 1));
        Assert.False(c.S.BudgetMonth(2027, 1).Lines[0].IsDefault);

        Assert.Throws<FinanceValidationException>(() => c.S.SetBudget(Cat(c.S, "Mercado"), null, 1_000)); // subcategoria
        Assert.Throws<FinanceValidationException>(() => c.S.SetBudget(Cat(c.S, "Salário"), null, 1_000));
        Assert.Throws<FinanceValidationException>(() => c.S.SetBudget(food, null, 0));
    }

    [Fact]
    public void Budget_ignores_transfers_card_payments_and_adjustments()
    {
        var c = Create();
        var shop = Cat(c.S, "Compras");
        c.S.SetBudget(shop, null, 10_000);
        c.S.SavePurchase(new PurchaseDraft { CardId = c.Card, Date = D(11, 1), Description = "Tênis", Total = 6_000, Installments = 2, CategoryId = shop });
        c.S.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = D(11, 10), Description = "Fatura", Amount = 3_000, AccountId = c.Bank, CardId = c.Card, StatementClosing = D(11, 3) });
        c.S.AdjustBalanceTo(c.Bank, D(11, 11), 50_000, "conferido");
        var line = Assert.Single(c.S.BudgetMonth(2026, 11).Lines);
        Assert.Equal(3_000, line.Spent); // só a parcela de novembro
        Assert.Equal(3_000, Assert.Single(c.S.BudgetMonth(2026, 12).Lines).Spent);
    }

    // ---------- Metas ----------

    [Fact]
    public void Reserve_goal_real_vs_planned_and_reserved_indicator()
    {
        var c = Create();
        var goal = c.S.SaveGoal(c.S.NewGoal() with { Name = "Viagem", Target = 100_000, Deadline = D(4, 30, 2027) });
        c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 10), 20_000, c.Bank);
        c.S.Contribute(goal.Id, ContributionKind.Planned, D(12, 10), 10_000, c.Bank);
        var p = c.S.Progress(goal);
        Assert.Equal(20_000, p.Real);
        Assert.Equal(10_000, p.PlannedAhead);
        Assert.Equal(6, p.MonthsLeft); // nov, dez, jan, fev, mar, abr
        Assert.Equal(13_334, p.MonthlyNeeded); // 80000 / 6, arredondado para cima
        Assert.Equal(20m, p.Percent);
        Assert.Equal(30m, p.PercentWithPlanned);

        var o = c.S.Overview(2026, 11);
        Assert.Equal(100_000, o.AccountsBalance); // reservar não muda o saldo
        Assert.Equal(20_000, o.Reserved);         // planejado nunca entra
        Assert.Equal(80_000, o.Available);

        Assert.Throws<FinanceValidationException>(() => c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 16), 1_000, c.Bank)); // futuro
        Assert.Throws<FinanceValidationException>(() => c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 15), -25_000, c.Bank)); // mais que o guardado
        c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 15), -5_000, c.Bank);
        Assert.Equal(15_000, c.S.Reserved(D(11, 15)));

        c.S.SetArchived(goal, archived: true);
        Assert.Equal(0, c.S.Reserved(D(11, 15)));
    }

    [Fact]
    public void Goal_with_own_account_uses_linked_transfers()
    {
        var c = Create();
        var savings = c.S.SaveAccount(c.S.NewAccount() with { Name = "Poupança", Kind = AccountKind.Savings, OpeningDate = D(9, 1), IncludeInAvailable = false }).Id;
        var goal = c.S.SaveGoal(c.S.NewGoal() with { Name = "Reserva", Target = 50_000, AccountId = savings });
        var contribution = c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 12), 30_000, c.Bank);
        var transfer = c.S.Find(contribution.TransactionId!.Value)!;
        Assert.Equal(TransactionKind.Transfer, transfer.Kind);
        Assert.Equal((c.Bank, savings), (transfer.AccountId, transfer.ToAccountId!.Value));
        Assert.Equal(70_000, c.S.Balance(c.Bank, D(11, 15)));
        Assert.Equal(30_000, c.S.Balance(savings, D(11, 15)));
        Assert.Equal(0, c.S.Reserved(D(11, 15))); // a poupança já está fora do disponível
        Assert.Equal(PeriodTotals(0, 0), (c.S.Totals(D(11, 1), D(11, 30)).Income, c.S.Totals(D(11, 1), D(11, 30)).Expense));
        Assert.Throws<FinanceValidationException>(() => c.S.Delete(transfer.Id));
        Assert.NotNull(c.S.Validate(FinanceService.DraftFrom(transfer) with { Amount = 1 }));
        Assert.NotNull(c.S.Validate(goal with { AccountId = null }));

        c.S.DeleteContribution(contribution.Id);
        Assert.Null(c.S.Find(transfer.Id));
        Assert.Equal(100_000, c.S.Balance(c.Bank, D(11, 15)));
    }

    private static (long, long) PeriodTotals(long income, long expense) => (income, expense);

    [Fact]
    public void Goal_account_inside_available_counts_as_reserved_and_planned_can_be_realized()
    {
        var c = Create();
        var savings = c.S.SaveAccount(c.S.NewAccount() with { Name = "Caixinha", Kind = AccountKind.Savings, OpeningDate = D(9, 1) }).Id;
        var goal = c.S.SaveGoal(c.S.NewGoal() with { Name = "Notebook", Target = 50_000, AccountId = savings });
        var planned = c.S.Contribute(goal.Id, ContributionKind.Planned, D(11, 20), 10_000, c.Bank);
        Assert.Equal(0, c.S.Reserved(D(11, 15)));
        c.S.Realize(planned.Id, c.Bank);
        Assert.Equal(10_000, c.S.Reserved(D(11, 15)));
        Assert.Equal(100_000, c.S.AccountsBalance()); // transferência entre contas do disponível
        Assert.DoesNotContain(c.S.Contributions(goal.Id), x => x.Kind == ContributionKind.Planned);
        Assert.False(c.S.CanDeleteAccount(savings));
        c.S.DeleteGoal(goal.Id);
        Assert.Single(c.S.Transactions, t => t.Kind == TransactionKind.Transfer); // a transferência fica
    }

    // ---------- Projeções ----------

    [Fact]
    public void Projection_matches_forecast_and_simulation_never_touches_data()
    {
        var c = Create();
        Bill(c, 15_000, D(11, 20));
        Bill(c, 5_000, D(11, 25), kind: RecurringKind.Income);
        var before = c.Store.Transactions.Count;

        var p = c.S.Project(D(11, 30));
        Assert.Equal(D(11, 15), p.Points[0].Date);
        Assert.Equal(100_000, p.Points[0].Baseline);
        Assert.Equal(85_000, p.Points.Single(x => x.Date == D(11, 20)).Baseline);
        Assert.Equal(c.S.Overview(2026, 11).Forecast, p.Points[^1].Baseline);
        Assert.Equal(1, p.Assumptions.PlannedExpenses);
        Assert.Equal(1, p.Assumptions.PlannedIncome);
        Assert.False(p.HasSimulation);

        var sim = c.S.Project(D(11, 30), [new SimulationItem { Kind = SimulationKind.Expense, Date = D(11, 18), Amount = 20_000 }], safetyMargin: 5_000);
        Assert.Equal(p.Points.Select(x => x.Baseline), sim.Points.Select(x => x.Baseline));
        Assert.Equal(65_000, sim.LowestSimulated.Simulated);
        Assert.Equal(D(11, 20), sim.LowestSimulated.Date);
        Assert.Equal(60_000, sim.CanReserve);
        Assert.Equal(before, c.Store.Transactions.Count);
        Assert.Equal(90_000, c.S.Overview(2026, 11).Forecast);
    }

    [Fact]
    public void Projection_card_purchase_simulation_hits_each_due_date()
    {
        var c = Create();
        var p = c.S.Project(D(2, 28, 2027), [new SimulationItem { Kind = SimulationKind.CardPurchase, Date = D(11, 16), Amount = 12_000, Installments = 3, CardId = c.Card }]);
        long Gap(DateOnly d) => p.Points.Single(x => x.Date == d).Baseline - p.Points.Single(x => x.Date == d).Simulated;
        Assert.Equal(0, Gap(D(12, 9)));
        Assert.Equal(4_000, Gap(D(12, 10)));
        Assert.Equal(8_000, Gap(D(1, 10, 2027)));
        Assert.Equal(12_000, Gap(D(2, 28, 2027)));
        Assert.Equal(4, p.Months.Count);
        Assert.Equal((2027, 2), (p.TightestMonth.Year, p.TightestMonth.Month));
    }

    [Fact]
    public void Projection_subtracts_reserved_planned_contributions_and_optional_variable_spending()
    {
        var c = Create();
        // 92 dias entre 01/08 e 31/10; R$ 92,00 variáveis = R$ 1,00 por dia.
        c.S.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(10, 10), Description = "Mercado", Amount = 9_200, AccountId = c.Bank, CategoryId = Cat(c.S, "Mercado") });
        var goal = c.S.SaveGoal(c.S.NewGoal() with { Name = "Viagem", Target = 100_000 });
        c.S.Contribute(goal.Id, ContributionKind.Real, D(11, 1), 10_000, c.Bank);
        c.S.Contribute(goal.Id, ContributionKind.Planned, D(11, 25), 5_000, c.Bank);

        var p = c.S.Project(D(11, 30));
        Assert.Equal(90_800 - 10_000, p.Points[0].Baseline);
        Assert.Equal(90_800 - 15_000, p.Points[^1].Baseline);
        Assert.Equal(10_000, p.Assumptions.Reserved);
        Assert.Equal(1, p.Assumptions.PlannedContributions);
        Assert.Null(p.Assumptions.VariableDaily);

        var v = c.S.Project(D(11, 30), includeVariable: true);
        Assert.Equal(100, v.Assumptions.VariableDaily);
        Assert.Equal(p.Points[^1].Baseline - 1_500, v.Points[^1].Baseline); // 15 dias
    }

    [Fact]
    public void Projection_includes_card_statements_and_subscriptions()
    {
        var c = Create();
        c.S.SavePurchase(new PurchaseDraft { CardId = c.Card, Date = D(11, 10), Description = "Fone", Total = 20_000, Installments = 1, CategoryId = Cat(c.S, "Tecnologia") });
        Bill(c, 3_990, D(11, 20), category: "Assinaturas", kind: RecurringKind.Subscription, onCard: true);
        var p = c.S.Project(D(12, 31));
        Assert.Equal(100_000, p.Points.Single(x => x.Date == D(12, 9)).Baseline);
        Assert.Equal(100_000 - 23_990, p.Points.Single(x => x.Date == D(12, 10)).Baseline);
        Assert.Equal(c.S.Overview(2026, 12).Forecast, p.Points[^1].Baseline);
        Assert.Equal(1, p.Assumptions.Statements);
    }

    [Fact]
    public void Scenarios_are_saved_apart_and_validated()
    {
        var c = Create();
        var items = new[] { new SimulationItem { Kind = SimulationKind.Saving, Date = D(12, 10), Amount = 50_000, Description = "Guardar" } };
        var scenario = c.S.SaveScenario(null, "Dezembro apertado", items);
        Assert.Single(c.Store.Scenarios);
        Assert.Empty(c.Store.Transactions);
        Assert.Throws<FinanceValidationException>(() => c.S.SaveScenario(null, "", items));
        Assert.Throws<FinanceValidationException>(() => c.S.SaveScenario(null, "x", [items[0] with { Date = D(11, 1) }]));
        c.S.DeleteScenario(scenario.Id);
        Assert.Empty(c.S.Scenarios);
    }
}
