using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

/// <summary>Armazenamento em memória para testar o serviço sem banco; pode simular falha de gravação.</summary>
internal sealed class MemoryFinanceStore : IFinanceStore
{
    public Dictionary<Guid, FinancialAccount> Accounts { get; } = [];
    public Dictionary<Guid, TransactionCategory> Categories { get; } = [];
    public Dictionary<Guid, FinanceTransaction> Transactions { get; } = [];
    public bool Fail { get; set; }

    private void Guard()
    {
        if (Fail)
        {
            throw new IOException("disco cheio (simulado)");
        }
    }

    public Dictionary<Guid, CreditCard> Cards { get; } = [];
    public Dictionary<Guid, CardPurchase> Purchases { get; } = [];
    public List<StatementOverride> Overrides { get; } = [];

    public FinanceSnapshot Load() => new([.. Accounts.Values], [.. Categories.Values], [.. Transactions.Values])
    {
        Cards = [.. Cards.Values],
        Purchases = [.. Purchases.Values],
        StatementOverrides = [.. Overrides],
        Recurring = [.. Recurring.Values],
        RecurringSkips = [.. Skips],
        Budgets = [.. Budgets.Values],
        Goals = [.. Goals.Values],
        Contributions = [.. Contributions.Values],
        Scenarios = [.. Scenarios.Values],
    };

    public void SaveCard(CreditCard card)
    {
        Guard();
        Cards[card.Id] = card;
    }

    public void DeleteCard(Guid id)
    {
        Guard();
        Cards.Remove(id);
    }

    public void SavePurchase(CardPurchase purchase, IReadOnlyList<FinanceTransaction> installments)
    {
        Guard();
        Assert.Equal(purchase.Total, installments.Sum(i => -i.Postings.Single().Amount)); // mesma regra do banco
        Purchases[purchase.Id] = purchase;
        foreach (var old in Transactions.Values.Where(t => t.PurchaseId == purchase.Id).ToList())
        {
            Transactions.Remove(old.Id);
        }
        installments.ToList().ForEach(i => Transactions[i.Id] = i);
    }

    public void DeletePurchase(Guid id, DateTimeOffset at)
    {
        Guard();
        Purchases.Remove(id);
        foreach (var old in Transactions.Values.Where(t => t.PurchaseId == id).ToList())
        {
            Transactions.Remove(old.Id);
        }
    }

    public void SaveStatementOverride(StatementOverride value)
    {
        Guard();
        Overrides.RemoveAll(o => o.CardId == value.CardId && o.Closing == value.Closing);
        Overrides.Add(value);
    }

    public void SaveTransactions(IReadOnlyList<FinanceTransaction> transactions)
    {
        Guard();
        transactions.ToList().ForEach(t => Transactions[t.Id] = t);
    }

    public void SaveAccount(FinancialAccount account)
    {
        Guard();
        Accounts[account.Id] = account;
    }

    public void DeleteAccount(Guid id)
    {
        Guard();
        Accounts.Remove(id);
    }

    public void SaveCategory(TransactionCategory category)
    {
        Guard();
        Categories[category.Id] = category;
    }

    public void DeleteCategory(Guid id)
    {
        Guard();
        Categories.Remove(id);
    }

    public void SaveTransaction(FinanceTransaction transaction)
    {
        Guard();
        Transactions[transaction.Id] = transaction;
    }

    public void DeleteTransaction(Guid id, DateTimeOffset at)
    {
        Guard();
        Transactions.Remove(id);
    }

    public Dictionary<Guid, RecurringItem> Recurring { get; } = [];
    public HashSet<RecurringSkip> Skips { get; } = [];
    public Dictionary<Guid, Budget> Budgets { get; } = [];
    public Dictionary<Guid, FinancialGoal> Goals { get; } = [];
    public Dictionary<Guid, GoalContribution> Contributions { get; } = [];
    public Dictionary<Guid, SavedScenario> Scenarios { get; } = [];

    public void SaveRecurring(RecurringItem item)
    {
        Guard();
        Recurring[item.Id] = item;
    }

    public void DeleteRecurring(Guid id, DateTimeOffset at)
    {
        Guard();
        Recurring.Remove(id);
        Skips.RemoveWhere(s => s.RecurringId == id);
        foreach (var t in Transactions.Values.Where(t => t.RecurringId == id).ToList())
        {
            Transactions[t.Id] = t with { RecurringId = null, OccurrenceDate = null };
        }
    }

    public void SetRecurringSkip(RecurringSkip skip, bool skipped)
    {
        Guard();
        if (skipped)
        {
            Skips.Add(skip);
        }
        else
        {
            Skips.Remove(skip);
        }
    }

    public void SaveBudget(Budget budget)
    {
        Guard();
        Budgets[budget.Id] = budget;
    }

    public void DeleteBudget(Guid id)
    {
        Guard();
        Budgets.Remove(id);
    }

    public void SaveGoal(FinancialGoal goal)
    {
        Guard();
        Goals[goal.Id] = goal;
    }

    public void DeleteGoal(Guid id, DateTimeOffset at)
    {
        Guard();
        Goals.Remove(id);
        foreach (var c in Contributions.Values.Where(c => c.GoalId == id).ToList())
        {
            Contributions.Remove(c.Id);
        }
    }

    public void SaveContribution(GoalContribution contribution, FinanceTransaction? transfer)
    {
        Guard();
        if (transfer is not null)
        {
            Transactions[transfer.Id] = transfer;
        }
        Contributions[contribution.Id] = contribution;
    }

    public void DeleteContribution(Guid id, DateTimeOffset at)
    {
        Guard();
        if (Contributions.Remove(id, out var c) && c.TransactionId is { } tx)
        {
            Transactions.Remove(tx);
        }
    }

    public void SaveScenario(SavedScenario scenario)
    {
        Guard();
        Scenarios[scenario.Id] = scenario;
    }

    public void DeleteScenario(Guid id)
    {
        Guard();
        Scenarios.Remove(id);
    }
}

public sealed class MoneyTests
{
    [Theory]
    [InlineData("1.234,56", 123456)]
    [InlineData("1234,56", 123456)]
    [InlineData("1234.56", 123456)]
    [InlineData("1,234.56", 123456)]
    [InlineData("1.234", 123400)]
    [InlineData("1.234.567", 123456700)]
    [InlineData("10", 1000)]
    [InlineData("0,5", 50)]
    [InlineData(",99", 99)]
    [InlineData("R$ 10,00", 1000)]
    [InlineData("R$ 1.000,00", 100000)]
    [InlineData("-10,50", -1050)]
    [InlineData("−10,50", -1050)]
    [InlineData("(10,00)", -1000)]
    [InlineData("+3", 300)]
    public void Parses_brazilian_and_plain_amounts(string text, long expected)
    {
        Assert.True(Money.TryParse(text, out var minor), text);
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,234")] // ambíguo: três casas depois da vírgula
    [InlineData("10,555")]
    [InlineData("1.23.4")]
    [InlineData("12e3")]
    [InlineData("999999999999999999")]
    public void Rejects_ambiguous_or_invalid_amounts(string text) => Assert.False(Money.TryParse(text, out _), text);

    [Fact]
    public void Formats_in_brazilian_style_with_typographic_minus()
    {
        Assert.Equal("R$ 1.234,56", Money.Format(123456));
        Assert.Equal("−R$ 0,05", Money.Format(-5));
        Assert.Equal("+R$ 5,00", Money.Format(500, showPlus: true));
        Assert.Equal("1.000,00", Money.Format(100000, symbol: false));
        Assert.Equal("¥ 1.500", Money.Format(1500, "JPY"));
        Assert.Equal("R$ ••••", Money.Masked());
    }

    [Fact]
    public void Arithmetic_is_checked_against_overflow()
    {
        Assert.Throws<OverflowException>(() => Money.Add(long.MaxValue, 1));
        Assert.Throws<OverflowException>(() => Money.Sum([long.MaxValue, 1]));
        Assert.Equal(0, Money.Sum([]));
    }

    [Fact]
    public void Split_always_adds_up_and_gives_the_rest_to_the_first_part()
    {
        Assert.Equal([3334, 3333, 3333], Money.Split(10000, 3));
        Assert.Equal([-3334, -3333, -3333], Money.Split(-10000, 3));
        var random = new Random(42);
        for (var i = 0; i < 5000; i++)
        {
            var total = random.NextInt64(1, 10_000_000);
            var parts = random.Next(1, 49);
            var split = Money.Split(total, parts);
            Assert.Equal(total, split.Sum());
            Assert.Equal(parts, split.Length);
            Assert.True(split.Skip(1).All(p => p == split[1]));
            Assert.InRange(split[0] - split[^1], 0, parts - 1);
        }
    }

    [Fact]
    public void Percent_is_null_when_base_is_zero()
    {
        Assert.Null(Money.Percent(10, 0));
        Assert.Equal(33.3m, Money.Percent(1, 3));
        Assert.Equal(-50m, Money.Percent(-1, 2));
    }
}

public sealed class FinanceServiceTests
{
    // 15/11/2026 às 12:00 UTC; o fuso de teste é UTC, então "hoje" = 15/11/2026.
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static (FinanceService Service, MemoryFinanceStore Store, FakeTimeProvider Time) Create()
    {
        var store = new MemoryFinanceStore();
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var service = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        service.Start();
        return (service, store, time);
    }

    private static FinancialAccount AddAccount(FinanceService s, string name, long opening = 0, AccountKind kind = AccountKind.Checking, string currency = "BRL", DateOnly? openingDate = null) =>
        s.SaveAccount(s.NewAccount() with { Name = name, OpeningBalance = opening, Kind = kind, Currency = currency, OpeningDate = openingDate ?? new DateOnly(2026, 1, 1), IncludeInAvailable = kind != AccountKind.Investment });

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    private static FinanceTransaction Expense(FinanceService s, Guid account, long amount, DateOnly? date = null, string category = "Mercado", TransactionStatus status = TransactionStatus.Cleared) =>
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = date ?? Today, Description = "Compra", Amount = amount, AccountId = account, CategoryId = Cat(s, category), Status = status });

    private static FinanceTransaction Income(FinanceService s, Guid account, long amount, DateOnly? date = null, TransactionStatus status = TransactionStatus.Cleared) =>
        s.Save(new TransactionDraft { Kind = TransactionKind.Income, Date = date ?? Today, Description = "Salário", Amount = amount, AccountId = account, CategoryId = Cat(s, "Salário"), Status = status });

    [Fact]
    public void Seeds_default_categories_once_with_subcategories_and_the_system_adjustment()
    {
        var (s, store, _) = Create();
        Assert.Contains(s.Categories, c => c.Name == "Alimentação" && c.ParentId is null && c.Kind == CategoryKind.Expense);
        Assert.Contains(s.Categories, c => c.Name == "Mercado" && s.Category(c.ParentId)?.Name == "Alimentação");
        Assert.Contains(s.Categories, c => c.Name == "Salário" && c.Kind == CategoryKind.Income);
        Assert.Equal(CategoryKind.System, s.Category(FinanceService.AdjustmentCategoryId)!.Kind);
        var count = store.Categories.Count;

        var again = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), new FakeTimeProvider(T0), NullLogger<FinanceService>.Instance);
        again.Start();
        Assert.Equal(count, again.Categories.Count);
    }

    [Fact]
    public void Balance_is_opening_plus_postings_and_pending_only_counts_in_the_forecast()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 100_000);
        Income(s, bank.Id, 500_000, new DateOnly(2026, 11, 5));
        Expense(s, bank.Id, 12_345, new DateOnly(2026, 11, 10));
        Expense(s, bank.Id, 20_000, Today, status: TransactionStatus.Pending);
        Expense(s, bank.Id, 1_000, new DateOnly(2026, 11, 20)); // futuro: fora do saldo de hoje

        Assert.Equal(100_000 + 500_000 - 12_345, s.Balance(bank.Id, Today));
        Assert.Equal(100_000 + 500_000 - 12_345 - 20_000, s.Balance(bank.Id, Today, includePending: true));
        Assert.Equal(100_000 + 500_000 - 12_345 - 1_000, s.Balance(bank.Id, new DateOnly(2026, 11, 30)));
        Assert.Equal(100_000, s.Balance(bank.Id, new DateOnly(2026, 11, 4)));
        Assert.Equal(0, s.Balance(bank.Id, new DateOnly(2025, 12, 31))); // antes do saldo inicial
        Assert.Equal(s.Balance(bank.Id, Today), s.CurrentBalances()[bank.Id]);
    }

    [Fact]
    public void Transfer_moves_money_without_counting_as_income_or_expense()
    {
        var (s, _, _) = Create();
        var a = AddAccount(s, "Corrente", 100_000);
        var b = AddAccount(s, "Poupança", 0, AccountKind.Savings);
        var before = s.NetWorth();
        var t = s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = Today, Description = "Guardar", Amount = 30_000, AccountId = a.Id, ToAccountId = b.Id });

        Assert.Equal(0, t.Postings.Sum(p => p.Amount));
        Assert.Null(t.CategoryId);
        Assert.Equal(70_000, s.Balance(a.Id, Today));
        Assert.Equal(30_000, s.Balance(b.Id, Today));
        Assert.Equal(before, s.NetWorth());
        Assert.Equal(new PeriodTotals(0, 0, 0, 0), s.Totals(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        Assert.Equal(a.Id, t.AccountId);
        Assert.Equal(b.Id, t.ToAccountId);
    }

    [Fact]
    public void Transfer_rules_are_enforced()
    {
        var (s, _, _) = Create();
        var a = AddAccount(s, "A", 100_000);
        var usd = AddAccount(s, "Dólar", 0, currency: "USD");
        var draft = new TransactionDraft { Kind = TransactionKind.Transfer, Date = Today, Description = "x", Amount = 100, AccountId = a.Id };
        Assert.Equal("Escolha a conta de destino.", s.Validate(draft));
        Assert.Equal("Origem e destino precisam ser contas diferentes.", s.Validate(draft with { ToAccountId = a.Id }));
        Assert.Contains("moedas diferentes", s.Validate(draft with { ToAccountId = usd.Id }));
    }

    [Fact]
    public void Refund_reduces_expense_and_cannot_exceed_the_original()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var purchase = Expense(s, bank.Id, 10_000, category: "Restaurantes");
        var refund = s.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = Today, Description = "Estorno", Amount = 4_000, AccountId = bank.Id, RefundOfId = purchase.Id });

        Assert.Equal(purchase.CategoryId, refund.CategoryId); // herda a categoria da despesa
        var totals = s.Totals(Today, Today);
        Assert.Equal(6_000, totals.Expense);
        Assert.Equal(0, totals.Income);
        Assert.Equal(-6_000, s.Balance(bank.Id, Today));

        var tooMuch = new TransactionDraft { Kind = TransactionKind.Refund, Date = Today, Description = "x", Amount = 6_001, AccountId = bank.Id, RefundOfId = purchase.Id };
        Assert.Contains("passa do valor", s.Validate(tooMuch));
        Assert.Null(s.Validate(tooMuch with { Amount = 6_000 }));
    }

    [Fact]
    public void Adjustment_sets_the_balance_without_touching_income_or_expense()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 50_000);
        Expense(s, bank.Id, 1_000);
        var adj = s.AdjustBalanceTo(bank.Id, Today, 48_500, "conferido com o extrato");

        Assert.NotNull(adj);
        Assert.Equal(-500, adj!.Postings[0].Amount);
        Assert.Equal(FinanceService.AdjustmentCategoryId, adj.CategoryId);
        Assert.Equal(48_500, s.Balance(bank.Id, Today));
        Assert.Equal(1_000, s.Totals(Today, Today).Expense);
        Assert.Null(s.AdjustBalanceTo(bank.Id, Today, 48_500, "de novo")); // já está certo
        Assert.Contains("Explique", s.Validate(new TransactionDraft { Kind = TransactionKind.Adjustment, Date = Today, Description = "Ajuste", Amount = 1, AccountId = bank.Id }));
    }

    [Fact]
    public void Totals_split_cleared_and_pending_and_ignore_other_currencies()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco");
        var usd = AddAccount(s, "Conta em dólar", currency: "USD");
        Income(s, bank.Id, 800_000, new DateOnly(2026, 11, 5));
        Income(s, bank.Id, 100_000, new DateOnly(2026, 11, 25), TransactionStatus.Pending);
        Expense(s, bank.Id, 30_000, new DateOnly(2026, 11, 2));
        Expense(s, bank.Id, 5_000, new DateOnly(2026, 11, 28), status: TransactionStatus.Pending);
        Expense(s, bank.Id, 99_999, new DateOnly(2026, 10, 31)); // outro mês
        Expense(s, usd.Id, 7_777, new DateOnly(2026, 11, 3)); // outra moeda

        var (from, to) = FinanceService.MonthRange(2026, 11);
        Assert.Equal(new PeriodTotals(800_000, 100_000, 30_000, 5_000), s.Totals(from, to));
        Assert.Equal(900_000 - 35_000, s.Totals(from, to).Net);
    }

    [Fact]
    public void Account_and_net_worth_totals_follow_their_definitions()
    {
        var (s, _, _) = Create();
        AddAccount(s, "Corrente", 100_000);
        AddAccount(s, "Investimentos", 1_000_000, AccountKind.Investment);
        AddAccount(s, "Viagem", 7_000, currency: "EUR");
        var archived = AddAccount(s, "Antiga", 5_000);
        s.SetArchived(archived.Id, true);

        Assert.Equal(100_000, s.AccountsBalance()); // sem investimento, outra moeda nem arquivada
        Assert.Equal(1_100_000, s.NetWorth()); // com investimento; sem outra moeda nem arquivada
    }

    [Fact]
    public void Validation_messages_cover_the_common_mistakes()
    {
        var (s, _, _) = Create();
        var draft = new TransactionDraft { Kind = TransactionKind.Expense, Date = Today, Description = "Pão", Amount = 500 };
        Assert.Equal("Cadastre uma conta primeiro.", s.Validate(draft));
        var bank = AddAccount(s, "Banco", 0, openingDate: new DateOnly(2026, 11, 1));
        draft = draft with { AccountId = bank.Id };
        Assert.Null(s.Validate(draft));
        Assert.Equal("Escreva uma descrição.", s.Validate(draft with { Description = "  " }));
        Assert.Equal("Informe um valor maior que zero.", s.Validate(draft with { Amount = 0 }));
        Assert.Equal("Informe um valor maior que zero.", s.Validate(draft with { Amount = -5 }));
        Assert.Equal("Valor fora do limite.", s.Validate(draft with { Amount = Money.MaxAbsMinor + 1 }));
        Assert.Contains("anterior ao saldo inicial", s.Validate(draft with { Date = new DateOnly(2026, 10, 31) }));
        Assert.Equal("Escolha uma categoria de despesa.", s.Validate(draft with { CategoryId = Cat(s, "Salário") }));
        Assert.Equal("Escolha uma categoria de receita.", s.Validate(draft with { Kind = TransactionKind.Income, CategoryId = Cat(s, "Mercado") }));
        Assert.Contains("etiquetas", s.Validate(draft with { Tags = [.. Enumerable.Range(0, 11).Select(i => $"t{i}")] }));
    }

    [Fact]
    public void Failed_write_leaves_memory_unchanged()
    {
        var (s, store, _) = Create();
        var bank = AddAccount(s, "Banco", 1_000);
        store.Fail = true;
        Assert.Throws<IOException>(() => Expense(s, bank.Id, 500));
        Assert.Empty(s.Transactions);
        Assert.Equal(1_000, s.Balance(bank.Id, Today));
    }

    [Fact]
    public void Editing_keeps_identity_and_recomputes_postings()
    {
        var (s, store, time) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var t = Expense(s, bank.Id, 1_000);
        time.Advance(TimeSpan.FromMinutes(5));
        var edited = s.Save(FinanceService.DraftFrom(t) with { Amount = 2_500, Kind = TransactionKind.Income, CategoryId = Cat(s, "Salário") });

        Assert.Equal(t.Id, edited.Id);
        Assert.Equal(t.CreatedAt, edited.CreatedAt);
        Assert.True(edited.UpdatedAt > t.UpdatedAt);
        Assert.Equal(2_500, edited.Postings.Single().Amount);
        Assert.Single(store.Transactions);
        Assert.Contains("transferência não pode virar", s.Validate(FinanceService.DraftFrom(t) with { Kind = TransactionKind.Transfer }));
    }

    [Fact]
    public void Duplicate_is_a_new_draft_dated_today_and_delete_can_be_undone()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var t = s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = new DateOnly(2026, 11, 1), Description = "Academia", Amount = 9_990, AccountId = bank.Id, CategoryId = Cat(s, "Saúde"), Tags = ["fixo"] });
        var copy = s.Duplicate(t.Id);
        Assert.Null(copy.Id);
        Assert.Equal(Today, copy.Date);
        Assert.Equal(9_990, copy.Amount);
        Assert.Equal(["fixo"], copy.Tags);

        var removed = s.Delete(t.Id);
        Assert.Empty(s.Transactions);
        s.Restore(removed);
        Assert.Equal(t, s.Find(t.Id));
        Assert.Equal(t, removed.Transaction);
    }

    [Fact]
    public void Deleting_an_expense_releases_its_refunds()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var purchase = Expense(s, bank.Id, 10_000);
        var refund = s.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = Today, Description = "Estorno", Amount = 10_000, AccountId = bank.Id, RefundOfId = purchase.Id });
        var deleted = s.Delete(purchase.Id);
        Assert.Null(s.Find(refund.Id)!.RefundOfId);
        Assert.Equal([refund.Id], deleted.UnlinkedRefunds);

        s.Restore(deleted); // desfazer religa o estorno à despesa
        Assert.Equal(purchase.Id, s.Find(refund.Id)!.RefundOfId);
        Assert.Equal(0, s.Totals(Today, Today).Expense);
    }

    [Fact]
    public void Query_filters_by_every_field_and_searches_without_accents()
    {
        var (s, _, _) = Create();
        var a = AddAccount(s, "A", 0);
        var b = AddAccount(s, "B", 0);
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = new DateOnly(2026, 11, 3), Description = "Café da manhã", Amount = 1_500, AccountId = a.Id, CategoryId = Cat(s, "Restaurantes"), Tags = ["trabalho"] });
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = new DateOnly(2026, 11, 10), Description = "Mercado do mês", Amount = 45_000, AccountId = b.Id, CategoryId = Cat(s, "Mercado"), Status = TransactionStatus.Pending });
        s.Save(new TransactionDraft { Kind = TransactionKind.Income, Date = new DateOnly(2026, 11, 5), Description = "Salário", Amount = 500_000, AccountId = a.Id, CategoryId = Cat(s, "Salário") });
        s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = new DateOnly(2026, 11, 6), Description = "Para B", Amount = 10_000, AccountId = a.Id, ToAccountId = b.Id });

        Assert.Equal(4, s.Query(new TransactionFilter()).Count);
        Assert.Equal(["Mercado do mês", "Para B", "Salário", "Café da manhã"], s.Query(new TransactionFilter()).Select(t => t.Description));
        Assert.Equal(2, s.Query(new TransactionFilter { CategoryId = Cat(s, "Alimentação") }).Count); // inclui subcategorias
        Assert.Equal(["Mercado do mês", "Para B"], s.Query(new TransactionFilter { AccountId = b.Id }).Select(t => t.Description)); // transferência aparece nas duas
        Assert.Single(s.Query(new TransactionFilter { Status = TransactionStatus.Pending }));
        Assert.Single(s.Query(new TransactionFilter { Kind = TransactionKind.Income }));
        Assert.Equal(2, s.Query(new TransactionFilter { MinAmount = 10_000, MaxAmount = 50_000 }).Count);
        Assert.Equal(2, s.Query(new TransactionFilter { From = new DateOnly(2026, 11, 4), To = new DateOnly(2026, 11, 6) }).Count);
        Assert.Single(s.Query(new TransactionFilter { Text = "cafe" }));
        Assert.Single(s.Query(new TransactionFilter { Text = "TRABALHO" })); // etiqueta
        Assert.Equal(2, s.Query(new TransactionFilter { Text = "alimentação" }).Count); // nome da categoria-mãe
        Assert.Empty(s.Query(new TransactionFilter { Text = "café mercado" })); // todos os termos
    }

    [Fact]
    public void Tags_are_trimmed_and_deduplicated_ignoring_case_and_accents()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var t = s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = Today, Description = "x", Amount = 1, AccountId = bank.Id, Tags = [" Viagem ", "viagem", "VIAGEM", "férias", "ferias", "a,b", ""] });
        Assert.Equal(["Viagem", "férias", "a b"], t.Tags);
    }

    [Fact]
    public void Accounts_with_history_are_archived_not_deleted()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        var empty = AddAccount(s, "Vazia", 0);
        Expense(s, bank.Id, 100);
        Assert.False(s.CanDeleteAccount(bank.Id));
        Assert.Throws<FinanceValidationException>(() => s.DeleteAccount(bank.Id));
        s.DeleteAccount(empty.Id);
        Assert.Null(s.Account(empty.Id));

        s.SetArchived(bank.Id, true);
        Assert.Contains("arquivada", s.Validate(new TransactionDraft { Kind = TransactionKind.Expense, Date = Today, Description = "x", Amount = 1, AccountId = bank.Id }));
        var old = s.Transactions.Single();
        Assert.Null(s.Validate(FinanceService.DraftFrom(old) with { Description = "editar lançamento antigo continua possível" }));
    }

    [Fact]
    public void Account_opening_date_and_currency_are_protected_by_history()
    {
        var (s, _, _) = Create();
        var bank = AddAccount(s, "Banco", 0);
        Expense(s, bank.Id, 100, new DateOnly(2026, 3, 1));
        Assert.Contains("data do saldo inicial", s.Validate(s.Account(bank.Id)! with { OpeningDate = new DateOnly(2026, 4, 1) }));
        Assert.Equal("A moeda de uma conta com lançamentos não pode mudar.", s.Validate(s.Account(bank.Id)! with { Currency = "USD" }));
        Assert.Equal("Dê um nome à conta.", s.Validate(s.NewAccount()));
        Assert.Equal("Moeda não suportada.", s.Validate(s.NewAccount() with { Name = "x", Currency = "XYZ" }));
    }

    [Fact]
    public void Category_rules_keep_one_level_same_kind_and_unique_names()
    {
        var (s, _, _) = Create();
        var food = s.Categories.First(c => c.Name == "Alimentação");
        var market = s.Categories.First(c => c.Name == "Mercado");
        Assert.Contains("um nível só", s.Validate(s.NewCategory(CategoryKind.Expense, market.Id) with { Name = "Hortifruti" }));
        Assert.Contains("mesmo tipo", s.Validate(s.NewCategory(CategoryKind.Income, food.Id) with { Name = "x" }));
        Assert.Contains("Já existe", s.Validate(s.NewCategory(CategoryKind.Expense, food.Id) with { Name = "mercado" }));
        Assert.Null(s.Validate(s.NewCategory(CategoryKind.Expense) with { Name = "Mercado" })); // outro nível: ok
        Assert.Contains("tem subcategorias", s.Validate(food with { ParentId = s.Categories.First(c => c.Name == "Lazer").Id }));
        Assert.Contains("sistema", s.Validate(s.Category(FinanceService.AdjustmentCategoryId)! with { Name = "x" }));

        var games = s.SaveCategory(s.NewCategory(CategoryKind.Expense, s.Categories.First(c => c.Name == "Lazer").Id) with { Name = "Steam" });
        Assert.Equal("Lazer › Steam", s.CategoryPath(games.Id));
        Assert.True(s.CanDeleteCategory(games.Id));
        Assert.False(s.CanDeleteCategory(food.Id)); // tem subcategorias
        Assert.False(s.CanDeleteCategory(FinanceService.AdjustmentCategoryId));

        s.SetArchived(food, true);
        Assert.True(s.Category(market.Id)!.IsArchived); // filhas acompanham
        Assert.DoesNotContain(s.OrderedCategories(CategoryKind.Expense), c => c.Id == market.Id);
    }

    [Fact]
    public void Ordered_categories_list_each_parent_followed_by_children()
    {
        var (s, _, _) = Create();
        var ordered = s.OrderedCategories(CategoryKind.Expense);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].ParentId is { } parent)
            {
                var parentIndex = ordered.ToList().FindIndex(c => c.Id == parent);
                Assert.True(parentIndex >= 0 && parentIndex < i);
                Assert.True(ordered.Skip(parentIndex + 1).Take(i - parentIndex).All(c => c.ParentId == parent));
            }
        }
    }

    [Fact]
    public void Today_follows_the_local_time_zone()
    {
        var store = new MemoryFinanceStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 1, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"));
        var s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        Assert.Equal(new DateOnly(2026, 11, 15), s.Today); // 22:30 do dia 15 em Brasília
    }

    [Fact]
    public void Fold_removes_accents_and_case() => Assert.Equal("acao cafe c", FinanceService.Fold("AÇÃO Café Ç"));
}

public sealed class FinanceMetricsTests
{
    // Hoje = 15/11/2026 (fuso UTC).
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

    private static FinanceService Create()
    {
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(new MemoryFinanceStore(), new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        return s;
    }

    private static Guid Account(FinanceService s, string name, long opening, AccountKind kind = AccountKind.Checking) =>
        s.SaveAccount(s.NewAccount() with { Name = name, OpeningBalance = opening, Kind = kind, OpeningDate = D(9, 1), IncludeInAvailable = kind != AccountKind.Investment }).Id;

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    private static void Tx(FinanceService s, TransactionKind kind, Guid account, long amount, DateOnly date, string? category = null, TransactionStatus status = TransactionStatus.Cleared, Guid? to = null) =>
        s.Save(new TransactionDraft { Kind = kind, Date = date, Description = "x", Amount = amount, AccountId = account, ToAccountId = to, CategoryId = category is null ? null : Cat(s, category), Status = status });

    /// <summary>Cenário de novembro montado à mão; os valores esperados estão calculados nos comentários.</summary>
    private static (FinanceService S, Guid Bank, Guid Invest) November()
    {
        var s = Create();
        var bank = Account(s, "Banco", 100_000);                                  // 1.000,00
        var invest = Account(s, "Investimentos", 500_000, AccountKind.Investment);  // fora do disponível
        Tx(s, TransactionKind.Income, bank, 300_000, D(10, 5), "Salário");          // outubro
        Tx(s, TransactionKind.Expense, bank, 50_000, D(10, 20), "Mercado");         // outubro
        Tx(s, TransactionKind.Income, bank, 400_000, D(11, 5), "Salário");          // recebido
        Tx(s, TransactionKind.Expense, bank, 30_000, D(11, 8), "Mercado");          // pago
        Tx(s, TransactionKind.Expense, bank, 20_000, D(11, 10), "Restaurantes");    // pago
        Tx(s, TransactionKind.Refund, bank, 5_000, D(11, 12), "Restaurantes");      // estorno recebido
        Tx(s, TransactionKind.Expense, bank, 9_000, D(11, 14), "Lazer", TransactionStatus.Pending);   // atrasado
        Tx(s, TransactionKind.Expense, bank, 150_000, D(11, 20), "Aluguel", TransactionStatus.Pending); // a pagar
        Tx(s, TransactionKind.Income, bank, 60_000, D(11, 25), "Trabalhos extras", TransactionStatus.Pending); // a receber
        Tx(s, TransactionKind.Expense, bank, 7_000, D(11, 28), "Mercado");          // confirmado com data futura
        Tx(s, TransactionKind.Transfer, bank, 100_000, D(11, 30), to: invest);      // guardar no investimento
        Tx(s, TransactionKind.Expense, bank, 1_000, D(12, 2), "Mercado", TransactionStatus.Pending); // dezembro
        return (s, bank, invest);
    }

    [Fact]
    public void Overview_decomposes_the_forecast_exactly()
    {
        var (s, _, _) = November();
        var o = s.Overview(2026, 11);

        // Saldo hoje (15/11, confirmados): 1.000 + 3.000 − 500 + 4.000 − 300 − 200 + 50 = 7.050,00
        Assert.Equal(D(11, 15), o.Reference);
        Assert.Equal(705_000, o.AccountsBalance);
        Assert.Equal(60_000, o.ToReceive);                // trabalho extra
        Assert.Equal(9_000 + 150_000 + 7_000, o.ToPay);   // atrasado + aluguel + mercado com data futura
        Assert.Equal(-100_000, o.OtherChanges);           // transferência para fora do disponível
        Assert.Equal(705_000 + 60_000 - 166_000 - 100_000, o.Forecast);
        Assert.Equal(o.Forecast, s.AvailableForecastAt(D(11, 30)));
        Assert.Equal(o.AccountsBalance + o.ToReceive - o.ToPay + o.OtherChanges, o.Forecast);
        Assert.Equal(705_000 + 500_000, o.NetWorth);      // com investimentos, confirmados até hoje
        Assert.Equal(1, o.OverduePending);
        // Previsto do fim de outubro: 1.000 + 3.000 − 500 = 3.500,00
        Assert.Equal(350_000, o.PreviousForecast);
        Assert.Equal(new PeriodTotals(300_000, 0, 50_000, 0), o.PreviousTotals);
        Assert.Equal(new PeriodTotals(400_000, 60_000, 30_000 + 20_000 - 5_000 + 7_000, 9_000 + 150_000), o.Totals);
    }

    [Fact]
    public void Past_month_is_measured_at_its_last_day_and_future_month_from_today()
    {
        var (s, _, _) = November();
        var october = s.Overview(2026, 10);
        Assert.Equal(D(10, 31), october.Reference);
        Assert.Equal(350_000, october.AccountsBalance);
        Assert.Equal(350_000, october.Forecast);
        Assert.Equal(0, october.ToReceive + october.ToPay + october.OtherChanges);

        var december = s.Overview(2026, 12);
        Assert.Equal(D(11, 15), december.Reference); // ainda não chegou: parte de hoje
        Assert.Equal(s.AvailableForecastAt(D(12, 31)), december.Forecast);
        Assert.Equal(december.AccountsBalance + december.ToReceive - december.ToPay + december.OtherChanges, december.Forecast);
        Assert.Equal(166_000 + 1_000, december.ToPay);
    }

    [Fact]
    public void Change_has_no_percent_over_zero()
    {
        Assert.Null(new Change(100, 0).Percent);
        Assert.Equal(50m, new Change(150, 100).Percent);
        Assert.Equal(-200m, new Change(-100, 100).Percent);
        Assert.Equal(100m, new Change(0, -100).Percent);
    }

    [Fact]
    public void Monthly_flows_cover_the_requested_window_in_order()
    {
        var (s, _, _) = November();
        var flows = s.MonthlyFlows(2026, 11, 3);
        Assert.Equal([(2026, 9), (2026, 10), (2026, 11)], flows.Select(f => (f.Year, f.Month)));
        Assert.Equal(new MonthFlow(2026, 9, 0, 0), flows[0]);
        Assert.Equal(new MonthFlow(2026, 10, 300_000, 50_000), flows[1]);
        Assert.Equal(460_000, flows[2].Income);
        Assert.Equal(52_000 + 159_000, flows[2].Expense);
        Assert.Equal((2025, 12), (s.MonthlyFlows(2026, 1, 2)[0].Year, s.MonthlyFlows(2026, 1, 2)[0].Month));
    }

    [Fact]
    public void Spending_by_category_rolls_up_children_and_subtracts_refunds()
    {
        var (s, _, _) = November();
        var spending = s.SpendingByCategory(D(11, 1), D(11, 30));
        // Moradia 1.500 (aluguel) · Alimentação 300 + 200 − 50 + 70 = 520 · Lazer 90
        Assert.Equal(["Moradia", "Alimentação", "Lazer"], spending.Select(c => c.Name));
        Assert.Equal([150_000, 52_000, 9_000], spending.Select(c => c.Amount));
        var food = spending[1];
        Assert.Equal(["Mercado", "Restaurantes"], food.Children.Select(c => c.Name));
        Assert.Equal([37_000, 15_000], food.Children.Select(c => c.Amount));
        Assert.Equal(s.Totals(D(11, 1), D(11, 30)).Expense, spending.Sum(c => c.Amount));
    }

    [Fact]
    public void Spending_includes_uncategorized_and_ignores_other_currencies()
    {
        var s = Create();
        var bank = Account(s, "Banco", 0);
        var usd = s.SaveAccount(s.NewAccount() with { Name = "USD", Currency = "USD", OpeningDate = D(9, 1) }).Id;
        Tx(s, TransactionKind.Expense, bank, 1_000, D(11, 3));
        Tx(s, TransactionKind.Expense, usd, 9_999, D(11, 3), "Mercado");
        var only = Assert.Single(s.SpendingByCategory(D(11, 1), D(11, 30)));
        Assert.Equal(("Sem categoria", 1_000L), (only.Name, only.Amount));
    }

    [Fact]
    public void Daily_balances_are_actual_until_today_and_forecast_after()
    {
        var (s, _, _) = November();
        var days = s.DailyBalances(2026, 11);
        Assert.Equal(30, days.Count);
        Assert.All(days.Take(15), d => Assert.False(d.IsForecast));
        Assert.All(days.Skip(15), d => Assert.True(d.IsForecast));
        Assert.Equal(350_000, days[0].Balance);                  // 01/11: igual ao fim de outubro
        Assert.Equal(350_000 + 400_000, days[4].Balance);        // 05/11: salário
        Assert.Equal(705_000, days[14].Balance);                 // hoje: confirmados (atrasado fora)
        Assert.Equal(705_000 - 9_000, days[15].Balance);         // amanhã: previsto inclui o atrasado
        Assert.Equal(s.Overview(2026, 11).Forecast, days[^1].Balance);
    }

    [Fact]
    public void Pending_list_brings_overdue_first_and_stops_at_the_limit()
    {
        var (s, _, _) = November();
        Assert.Equal([D(11, 14), D(11, 20), D(11, 25)], s.PendingUntil(D(11, 30)).Select(t => t.Date));
        Assert.Equal(4, s.PendingUntil(D(12, 31)).Count);
    }

    [Fact]
    public void Comparison_is_flagged_only_when_an_account_existed_in_the_previous_month()
    {
        var (s, _, _) = November();
        Assert.True(s.Overview(2026, 11).HasPreviousMonth);
        Assert.False(s.Overview(2026, 9).HasPreviousMonth); // contas abertas em 01/09: agosto não tinha nada

        var fresh = Create();
        fresh.SaveAccount(fresh.NewAccount() with { Name = "Nova", OpeningDate = D(11, 1) });
        Assert.False(fresh.Overview(2026, 11).HasPreviousMonth);
        Assert.True(fresh.Overview(2026, 12).HasPreviousMonth);
    }

    [Fact]
    public void Empty_finances_give_zeroes_not_errors()
    {
        var s = Create();
        var o = s.Overview(2026, 11);
        Assert.Equal(0, o.AccountsBalance + o.Forecast + o.NetWorth + o.ToPay + o.ToReceive + o.OtherChanges);
        Assert.Empty(s.SpendingByCategory(D(11, 1), D(11, 30)));
        Assert.Equal(28, s.DailyBalances(2026, 2).Count);
        Assert.All(s.DailyBalances(2026, 2), d => Assert.Equal(0, d.Balance));
    }
}

public sealed class StatementCalendarTests
{
    private static CreditCard Card(int closing, int due, bool goesNext = true, params CardCycleRule[] extra) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Teste",
        ClosingDayGoesNext = goesNext,
        Rules = [new CardCycleRule(DateOnly.MinValue, closing, due), .. extra],
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Purchase_before_on_and_after_the_closing_day()
    {
        var card = Card(3, 10);
        Assert.Equal(D(2026, 11, 3), StatementCalendar.ClosingFor(card, D(2026, 11, 2)));
        Assert.Equal(D(2026, 12, 3), StatementCalendar.ClosingFor(card, D(2026, 11, 3))); // no dia: vai para a próxima
        Assert.Equal(D(2026, 12, 3), StatementCalendar.ClosingFor(card, D(2026, 11, 4)));
        Assert.Equal(D(2027, 1, 3), StatementCalendar.ClosingFor(card, D(2026, 12, 31)));
        var sameDay = Card(3, 10, goesNext: false);
        Assert.Equal(D(2026, 11, 3), StatementCalendar.ClosingFor(sameDay, D(2026, 11, 3)));
        Assert.Equal(D(2026, 11, 3), StatementCalendar.Opening(card, D(2026, 12, 3)));
        Assert.Equal(D(2026, 11, 4), StatementCalendar.Opening(sameDay, D(2026, 12, 3)));
    }

    [Fact]
    public void Closing_day_31_follows_short_months_and_leap_years()
    {
        var card = Card(31, 8);
        Assert.Equal(D(2026, 2, 28), StatementCalendar.ClosingFor(card, D(2026, 2, 10)));
        Assert.Equal(D(2026, 3, 31), StatementCalendar.ClosingFor(card, D(2026, 2, 28)));
        Assert.Equal(D(2028, 2, 29), StatementCalendar.ClosingFor(card, D(2028, 2, 28)));
        Assert.Equal(D(2026, 4, 30), StatementCalendar.NextClosing(card, D(2026, 3, 31)));
        Assert.Equal(D(2026, 3, 8), StatementCalendar.Due(card, D(2026, 2, 28)));
        Assert.Equal(D(2026, 2, 28), StatementCalendar.PreviousClosing(card, D(2026, 3, 31)));
    }

    [Fact]
    public void Due_date_is_the_first_due_day_after_closing()
    {
        Assert.Equal(D(2026, 11, 10), StatementCalendar.Due(Card(3, 10), D(2026, 11, 3)));     // mesmo mês
        Assert.Equal(D(2026, 12, 5), StatementCalendar.Due(Card(25, 5), D(2026, 11, 25)));     // mês seguinte
        Assert.Equal(D(2026, 2, 28), StatementCalendar.Due(Card(20, 31), D(2026, 2, 20)));     // 31 limitado
        Assert.Equal(D(2026, 2, 28), StatementCalendar.Due(Card(31, 30), D(2026, 1, 31)));     // 30 < 31: mês seguinte
        var card = Card(3, 10);
        Assert.Equal(D(2026, 11, 12), StatementCalendar.Due(card, D(2026, 11, 3), [new StatementOverride(card.Id, D(2026, 11, 3), D(2026, 11, 12))]));
    }

    [Fact]
    public void New_cycle_rule_only_applies_from_its_date()
    {
        var card = Card(3, 10, true, new CardCycleRule(D(2027, 1, 1), 15, 25));
        Assert.Equal(D(2026, 12, 3), StatementCalendar.ClosingFor(card, D(2026, 11, 20))); // regra antiga
        Assert.Equal(D(2027, 1, 15), StatementCalendar.ClosingFor(card, D(2026, 12, 3)));  // depois de 03/12: nova (ciclo de transição)
        Assert.Equal(D(2026, 12, 10), StatementCalendar.Due(card, D(2026, 12, 3)));
        Assert.Equal(D(2027, 1, 25), StatementCalendar.Due(card, D(2027, 1, 15)));
        Assert.Equal(D(2027, 1, 15), StatementCalendar.NextClosing(card, D(2026, 12, 3)));
    }

    [Fact]
    public void Advancing_cycles_never_skips_or_repeats_a_month()
    {
        foreach (var day in new[] { 1, 15, 28, 29, 30, 31 })
        {
            var card = Card(day, day == 1 ? 10 : 5);
            var closing = StatementCalendar.ClosingFor(card, D(2026, 1, 1));
            for (var i = 0; i < 36; i++)
            {
                var next = StatementCalendar.NextClosing(card, closing);
                Assert.Equal(closing.AddMonths(1).Month, next.Month);
                Assert.True(StatementCalendar.Due(card, next) > next);
                closing = next;
            }
        }
    }
}

public sealed class CreditCardTests
{
    // Hoje = 15/11/2026. Cartão: fecha dia 3, vence dia 10.
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int m, int d, int y = 2026) => new(y, m, d);

    private static (FinanceService S, Guid Bank, Guid Card, FakeTimeProvider Time, MemoryFinanceStore Store) Create(long bankOpening = 1_000_000, long limit = 500_000)
    {
        var store = new MemoryFinanceStore();
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = bankOpening, OpeningDate = D(9, 1) }).Id;
        var card = s.SaveCard(s.NewCard() with { Name = "Roxo", Limit = limit, PaymentAccountId = bank, Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)] }).Id;
        return (s, bank, card, time, store);
    }

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    private static CardPurchase Buy(FinanceService s, Guid card, long total, DateOnly date, int installments = 1, string category = "Tecnologia") =>
        s.SavePurchase(new PurchaseDraft { CardId = card, Date = date, Description = "Compra", Total = total, Installments = installments, CategoryId = Cat(s, category) });

    private static FinanceTransaction Pay(FinanceService s, Guid bank, Guid card, long amount, DateOnly date, DateOnly closing, TransactionStatus status = TransactionStatus.Cleared) =>
        s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = date, Description = "Pagamento da fatura", Amount = amount, AccountId = bank, CardId = card, StatementClosing = closing, Status = status });

    [Fact]
    public void Installments_add_up_get_monthly_dates_and_consecutive_statements()
    {
        var (s, _, card, _, _) = Create();
        var purchase = Buy(s, card, 10_000, D(11, 10), 3);
        var installments = s.Installments(purchase.Id);
        Assert.Equal([3_334, 3_333, 3_333], installments.Select(t => t.Amount));
        Assert.Equal(10_000, installments.Sum(t => t.Amount));
        Assert.Equal([D(11, 10), D(12, 10), D(1, 10, 2027)], installments.Select(t => t.Date));
        Assert.Equal([D(12, 3), D(1, 3, 2027), D(2, 3, 2027)], installments.Select(t => t.StatementClosing!.Value));
        Assert.Equal([1, 2, 3], installments.Select(t => t.InstallmentNumber!.Value));
        Assert.All(installments, t => Assert.Equal(TransactionKind.Expense, t.Kind));
        var plan = s.Plan(FinanceService.DraftFrom(purchase));
        Assert.Equal([D(12, 10), D(1, 10, 2027), D(2, 10, 2027)], plan.Select(p => p.Due));
    }

    [Fact]
    public void Debt_and_limit_include_future_installments()
    {
        var (s, _, card, _, _) = Create(limit: 500_000);
        Buy(s, card, 120_000, D(11, 10), 12);
        Assert.Equal(120_000, s.Debt(card));
        var summary = s.Summary(card);
        Assert.Equal(380_000, summary.Available);
        Assert.Equal(10_000, summary.OpenStatement!.Charges); // só a 1ª parcela na fatura aberta
    }

    [Fact]
    public void Card_purchase_and_its_payment_count_as_one_expense()
    {
        var (s, bank, card, _, _) = Create();
        Buy(s, card, 30_000, D(11, 10));
        Pay(s, bank, card, 30_000, D(11, 12), D(12, 3));
        var (from, to) = FinanceService.MonthRange(2026, 11);
        Assert.Equal(30_000, s.Totals(from, to).Expense);          // a compra
        Assert.Equal(1_000_000 - 30_000, s.Balance(bank, D(11, 15))); // o pagamento saiu da conta
        Assert.Equal(0, s.Debt(card));
        Assert.Equal(30_000, s.SpendingByCategory(from, to).Single().Amount);
        Assert.Equal(1_000_000 - 30_000, s.NetWorth());            // conta − dívida: contado uma vez
    }

    [Fact]
    public void Partial_payment_carries_the_rest_and_overpayment_becomes_credit()
    {
        var (s, bank, card, time, _) = Create();
        Buy(s, card, 50_000, D(10, 20));                             // fatura 03/11, vence 10/11
        Pay(s, bank, card, 20_000, D(11, 8), D(11, 3));
        var statements = s.Statements(card);
        var november = statements.Single(x => x.Closing == D(11, 3));
        Assert.Equal((50_000L, 20_000L, 30_000L), (november.Charges, november.Payments, november.Remaining));
        Assert.Equal(StatementStatus.PartiallyPaid, november.Status);   // hoje (15/11) já venceu
        var december = statements.Single(x => x.Closing == D(12, 3));
        Assert.Equal(30_000, december.Previous);                        // o restante foi para a próxima
        Assert.Equal(StatementStatus.Open, december.Status);
        Assert.Equal(D(11, 3), s.StatementToPay(card)!.Closing);       // a fechada com saldo vem primeiro

        Pay(s, bank, card, 40_000, D(11, 16), D(12, 3));                // paga a mais
        time.Advance(TimeSpan.FromDays(30));                            // 15/12: fatura de 03/12 fechada
        var after = s.Statements(card);
        var dec = after.Single(x => x.Closing == D(12, 3));
        Assert.Equal(-10_000, dec.Remaining);
        Assert.Equal(StatementStatus.Paid, dec.Status);
        Assert.Equal(-10_000, after.Single(x => x.Closing == D(1, 3, 2027)).Previous); // crédito na seguinte
        Assert.Equal(-10_000, s.Debt(card));
    }

    [Fact]
    public void Statement_status_follows_closing_and_due_dates()
    {
        var (s, _, card, time, _) = Create();
        Buy(s, card, 10_000, D(11, 1));                                 // fatura 03/11
        Buy(s, card, 5_000, D(1, 20, 2027));                            // fatura 03/02/2027 (futura)
        var list = s.Statements(card);
        Assert.Equal(StatementStatus.Overdue, list.Single(x => x.Closing == D(11, 3)).Status);
        Assert.Equal(StatementStatus.Open, list.Single(x => x.Closing == D(12, 3)).Status);
        Assert.Equal(StatementStatus.Future, list.Single(x => x.Closing == D(2, 3, 2027)).Status);
        Assert.Equal([D(11, 3), D(12, 3), D(1, 3, 2027), D(2, 3, 2027)], list.Select(x => x.Closing)); // sem buracos

        Buy(s, card, 2_000, D(11, 20));                                 // fatura 03/12
        time.SetUtcNow(new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.Zero)); // fechou, vence 10/12
        Assert.Equal(StatementStatus.Closed, s.Statements(card).Single(x => x.Closing == D(12, 3)).Status);
        Assert.Equal(StatementStatus.Open, s.Statements(card).Single(x => x.Closing == D(1, 3, 2027)).Status);
    }

    [Fact]
    public void Forecast_subtracts_statements_due_by_the_end_of_the_month_and_stays_exact()
    {
        var (s, bank, card, _, _) = Create(bankOpening: 1_000_000);
        Buy(s, card, 100_000, D(11, 10));                               // fatura 03/12, vence 10/12
        var november = s.Overview(2026, 11);
        Assert.Equal(0, november.CardStatements);                       // nada vence até 30/11
        Assert.Equal(1_000_000, november.Forecast);
        Assert.Equal(100_000, november.CardSpending);
        Assert.Equal(100_000, november.Totals.Expense);

        var december = s.Overview(2026, 12);
        Assert.Equal(100_000, december.CardStatements);
        Assert.Equal(900_000, december.Forecast);
        Assert.Equal(100_000, december.StatementsDue);
        Assert.Equal(december.Forecast, s.DailyBalances(2026, 12)[^1].Balance);
        Assert.Equal(1_000_000, s.DailyBalances(2026, 12)[8].Balance);  // 09/12: ainda não venceu
        Assert.Equal(900_000, s.DailyBalances(2026, 12)[9].Balance);    // 10/12: vencimento

        Pay(s, bank, card, 100_000, D(12, 9), D(12, 3), TransactionStatus.Pending); // pagamento agendado
        var paid = s.Overview(2026, 12);
        Assert.Equal(0, paid.CardStatements);                           // não conta duas vezes
        Assert.Equal(900_000, paid.Forecast);
    }

    [Fact]
    public void Future_statements_do_not_accumulate_unpaid_statements_that_are_not_due_yet()
    {
        var (s, _, card, _, _) = Create(bankOpening: 1_000_000);
        Buy(s, card, 240_000, D(11, 10), 12);                           // 12× 200,00
        Buy(s, card, 15_000, D(11, 5));                                 // à vista, mesma fatura
        var list = s.Statements(card);
        Assert.Equal(35_000, list.Single(x => x.Closing == D(12, 3)).AmountDue);
        Assert.Equal(20_000, list.Single(x => x.Closing == D(1, 3, 2027)).AmountDue); // não 55.000
        Assert.All(list.Where(x => x.Status == StatementStatus.Future), x => Assert.Equal(0, x.Previous));
        // Previsto de janeiro: saem a fatura de dezembro (vence 10/12) e a de janeiro (vence 10/01).
        Assert.Equal(55_000, s.StatementOutflow(D(1, 31, 2027)));
        Assert.Equal(1_000_000 - 55_000, s.Overview(2027, 1).Forecast);
    }

    [Fact]
    public void Overdue_statement_is_reported_and_included_in_the_forecast()
    {
        var (s, _, card, _, _) = Create();
        Buy(s, card, 20_000, D(10, 20));                                // venceu em 10/11 sem pagamento
        var o = s.Overview(2026, 11);
        Assert.Equal(1, o.OverdueStatements);
        Assert.Equal(20_000, o.CardStatements);
        Assert.Equal(1_000_000 - 20_000, o.Forecast);
    }

    [Fact]
    public void Refund_on_card_reduces_debt_and_spending_and_is_capped_by_the_whole_purchase()
    {
        var (s, _, card, _, _) = Create();
        var purchase = Buy(s, card, 90_000, D(11, 10), 3);
        var first = s.Installments(purchase.Id)[0];
        var refund = s.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = D(11, 14), Description = "Estorno", Amount = 50_000, CardId = card, RefundOfId = first.Id });
        Assert.Equal(D(12, 3), refund.StatementClosing);
        Assert.Equal(first.CategoryId, refund.CategoryId);
        Assert.Equal(40_000, s.Debt(card));
        Assert.Equal(30_000 - 50_000, s.Totals(D(11, 1), D(11, 30)).Expense);
        Assert.Contains("restam", s.Validate(new TransactionDraft { Kind = TransactionKind.Refund, Date = D(11, 14), Description = "x", Amount = 40_001, CardId = card, RefundOfId = first.Id }));
        Assert.Null(s.Validate(new TransactionDraft { Kind = TransactionKind.Refund, Date = D(11, 14), Description = "x", Amount = 40_000, CardId = card, RefundOfId = first.Id }));
        Assert.Contains("mesmo cartão", s.Validate(new TransactionDraft { Kind = TransactionKind.Refund, Date = D(11, 14), Description = "x", Amount = 1, AccountId = s.Accounts[0].Id, RefundOfId = first.Id }));
    }

    [Fact]
    public void Installments_cannot_be_edited_or_deleted_one_by_one()
    {
        var (s, _, card, _, _) = Create();
        var purchase = Buy(s, card, 9_000, D(11, 10), 3);
        var installment = s.Installments(purchase.Id)[1];
        Assert.Contains("edite a compra", s.Validate(FinanceService.DraftFrom(installment) with { Description = "x" }));
        Assert.Throws<FinanceValidationException>(() => s.Delete(installment.Id));
        Assert.Throws<FinanceValidationException>(() => s.Duplicate(installment.Id));
        Assert.Contains("Compra no cartão", s.Validate(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 10), Description = "x", Amount = 1, CardId = card }));
    }

    [Fact]
    public void Purchases_lock_once_an_installment_is_in_a_closed_statement()
    {
        var (s, _, card, time, _) = Create();
        var purchase = Buy(s, card, 30_000, D(11, 10), 3);
        var ids = s.Installments(purchase.Id).Select(t => t.Id).ToList();
        Assert.False(s.IsLocked(purchase.Id));
        var edited = s.SavePurchase(FinanceService.DraftFrom(purchase) with { Total = 60_000, Installments = 2 });
        Assert.Equal([30_000, 30_000], s.Installments(edited.Id).Select(t => t.Amount));
        Assert.Equal(ids.Take(2), s.Installments(edited.Id).Select(t => t.Id)); // parcelas mantêm o ID

        time.SetUtcNow(new DateTimeOffset(2026, 12, 4, 12, 0, 0, TimeSpan.Zero)); // fatura de 03/12 fechou
        Assert.True(s.IsLocked(purchase.Id));
        Assert.Contains("fatura fechada", s.Validate(FinanceService.DraftFrom(edited) with { Total = 1_000 }));
        var renamed = s.SavePurchase(FinanceService.DraftFrom(edited) with { Description = "Novo nome", CategoryId = Cat(s, "Lazer") });
        Assert.All(s.Installments(renamed.Id), t => Assert.Equal(("Novo nome", Cat(s, "Lazer")), (t.Description, t.CategoryId!.Value)));
        Assert.Throws<FinanceValidationException>(() => s.DeletePurchase(purchase.Id));
    }

    [Fact]
    public void Deleting_an_open_purchase_removes_all_installments()
    {
        var (s, _, card, _, store) = Create();
        var purchase = Buy(s, card, 30_000, D(11, 10), 3);
        s.DeletePurchase(purchase.Id);
        Assert.Empty(s.Transactions);
        Assert.Empty(store.Transactions);
        Assert.Equal(0, s.Debt(card));
    }

    [Fact]
    public void Changing_the_cycle_moves_only_future_statements()
    {
        var (s, _, card, _, _) = Create();
        var purchase = Buy(s, card, 30_000, D(11, 10), 3);              // faturas 03/12, 03/01, 03/02
        var moved = s.ChangeCycle(card, 15, 25);
        Assert.Equal(2, moved);
        Assert.Equal([D(12, 3), D(1, 15, 2027), D(2, 15, 2027)], s.Installments(purchase.Id).Select(t => t.StatementClosing!.Value));
        Assert.Equal(D(1, 25, 2027), s.DueDate(s.Card(card)!, D(1, 15, 2027)));
        Assert.Equal(D(12, 10), s.DueDate(s.Card(card)!, D(12, 3)));    // a aberta não muda
        Assert.Equal(0, s.ChangeCycle(card, 15, 25));                   // mesma regra: nada
    }

    [Fact]
    public void Statement_due_date_can_be_corrected()
    {
        var (s, _, card, _, _) = Create();
        Buy(s, card, 10_000, D(11, 10));
        s.SetStatementDue(card, D(12, 3), D(12, 11));
        Assert.Equal(D(12, 11), s.Statements(card).Single(x => x.Closing == D(12, 3)).Due);
        Assert.Throws<FinanceValidationException>(() => s.SetStatementDue(card, D(12, 3), D(12, 3)));
    }

    [Fact]
    public void Net_worth_counts_card_debt_from_the_purchase_date()
    {
        var (s, _, card, _, _) = Create(bankOpening: 1_000_000);
        Buy(s, card, 120_000, D(11, 10), 12);
        Assert.Equal(1_000_000 - 120_000, s.NetWorth());
        Assert.Equal(1_000_000, s.Overview(2026, 10).NetWorth);         // antes da compra
    }

    [Fact]
    public void Card_and_payment_validation()
    {
        var (s, bank, card, _, _) = Create();
        Assert.Contains("4 números", s.Validate(s.Card(card)! with { LastDigits = "12345" }));
        Assert.Contains("4 números", s.Validate(s.Card(card)! with { LastDigits = "12a4" }));
        Assert.Null(s.Validate(s.Card(card)! with { LastDigits = "1234" }));
        Assert.Contains("diferente", s.Validate(s.Card(card)! with { Rules = [new CardCycleRule(DateOnly.MinValue, 10, 10)] }));
        Assert.Contains("1 a 31", s.Validate(s.Card(card)! with { Rules = [new CardCycleRule(DateOnly.MinValue, 0, 10)] }));
        var draft = new PurchaseDraft { CardId = card, Date = D(11, 10), Description = "x", Total = 5, Installments = 6 };
        Assert.Contains("1 centavo", s.Validate(draft));
        Assert.Contains("1 a 72", s.Validate(draft with { Total = 1_000, Installments = 73 }));
        Assert.Contains("categoria de despesa", s.Validate(draft with { Total = 1_000, Installments = 1, CategoryId = Cat(s, "Salário") }));
        Assert.Contains("Fatura inválida", s.Validate(new TransactionDraft { Kind = TransactionKind.Transfer, Date = D(11, 15), Description = "x", Amount = 1, AccountId = bank, CardId = card, StatementClosing = D(12, 4) }));
        var usd = s.SaveAccount(s.NewAccount() with { Name = "USD", Currency = "USD", OpeningDate = D(9, 1) }).Id;
        Assert.Contains("mesma moeda", s.Validate(new TransactionDraft { Kind = TransactionKind.Transfer, Date = D(11, 15), Description = "x", Amount = 1, AccountId = usd, CardId = card, StatementClosing = D(12, 3) }));
        Assert.False(s.CanDeleteCard(card) && s.Transactions.Any());
    }

    [Fact]
    public void Card_with_history_is_archived_not_deleted()
    {
        var (s, _, card, _, _) = Create();
        var empty = s.SaveCard(s.NewCard() with { Name = "Vazio" });
        s.DeleteCard(empty.Id);
        Assert.Null(s.Card(empty.Id));
        Buy(s, card, 1_000, D(11, 10));
        Assert.Throws<FinanceValidationException>(() => s.DeleteCard(card));
        s.SetArchived(s.Card(card)!, true);
        Assert.Contains("arquivado", s.Validate(new PurchaseDraft { CardId = card, Date = D(11, 10), Description = "x", Total = 100 }));
    }
}
