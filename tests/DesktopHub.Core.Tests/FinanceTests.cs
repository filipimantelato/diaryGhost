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

    public FinanceSnapshot Load() => new([.. Accounts.Values], [.. Categories.Values], [.. Transactions.Values]);

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
