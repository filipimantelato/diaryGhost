using System.Diagnostics;
using System.Text;
using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace DesktopHub.Core.Tests;

public sealed class FinanceRefinementTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int m, int d, int y = 2026) => new(y, m, d);

    private static (FinanceService S, Guid Bank) Create()
    {
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(new MemoryFinanceStore(), new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = 100_000, OpeningDate = D(8, 1) }).Id;
        return (s, bank);
    }

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    private static void Expense(FinanceService s, Guid bank, long amount, DateOnly date, string description, string category = "Mercado") =>
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = date, Description = description, Amount = amount, AccountId = bank, CategoryId = Cat(s, category) });

    [Fact]
    public void Report_compares_with_previous_period_and_lists_months_categories_and_top_expenses()
    {
        var (s, bank) = Create();
        s.Save(new TransactionDraft { Kind = TransactionKind.Income, Date = D(10, 5), Description = "Salário", Amount = 500_000, AccountId = bank, CategoryId = Cat(s, "Salário") });
        s.Save(new TransactionDraft { Kind = TransactionKind.Income, Date = D(11, 5), Description = "Salário", Amount = 500_000, AccountId = bank, CategoryId = Cat(s, "Salário") });
        Expense(s, bank, 30_000, D(9, 10), "Mercado setembro");
        Expense(s, bank, 20_000, D(10, 10), "Mercado outubro");
        Expense(s, bank, 45_000, D(11, 10), "Mercado novembro");
        Expense(s, bank, 9_000, D(11, 12), "Uber", "Aplicativos e táxi");

        var r = s.Report(D(10, 1), D(11, 30));
        Assert.Equal(2, r.MonthCount);
        Assert.Equal((1_000_000L, 74_000L), (r.Totals.Income, r.Totals.Expense));
        Assert.Equal((0L, 30_000L), (r.PreviousTotals.Income, r.PreviousTotals.Expense)); // ago+set
        Assert.Equal(37_000, r.AverageExpense);
        Assert.Equal([480_000L, 446_000L], r.Months.Select(m => m.Net).ToArray());
        Assert.Equal(100_000 - 30_000 + 500_000 - 20_000, r.Months[0].EndBalance); // fim de outubro
        Assert.Equal("Alimentação", r.Categories[0].Name);
        Assert.Equal(65_000, r.Categories[0].Amount);
        Assert.Equal(30_000, r.PreviousCategories.Single(c => c.Name == "Alimentação").Amount);
        Assert.Equal("Mercado novembro", r.TopExpenses[0].Description);
    }

    [Fact]
    public void Csv_export_is_brazilian_safe_against_formulas_and_round_trips()
    {
        var (s, bank) = Create();
        Expense(s, bank, 123_456, D(11, 3), "=HYPERLINK(\"http://x\")");
        s.Save(new TransactionDraft { Kind = TransactionKind.Income, Date = D(11, 4), Description = "Pix; \"João\"", Amount = 5_000, AccountId = bank, Notes = "+linha\nnova" });
        var csv = s.ExportCsv(D(11, 1), D(11, 30));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Data;Descrição;Categoria", lines[0], StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("-1234,56;BRL", csv, StringComparison.Ordinal);
        Assert.Contains("\"Pix; \"\"João\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'+linha\nnova\"", csv, StringComparison.Ordinal);

        // Ida e volta: o próprio leitor de extratos entende o arquivo exportado.
        var parsed = StatementParser.ParseCsv(csv, new CsvLayout { Delimiter = ';', HasHeader = true, DateColumn = 0, DescriptionColumn = 1, AmountColumn = 6, DecimalComma = true });
        Assert.Empty(parsed.Errors);
        Assert.Equal([-123_456L, 5_000L], parsed.Rows.Select(r => r.Amount).ToArray());
        Assert.Equal("Pix; \"João\"", parsed.Rows[1].Description);
    }

    [Fact]
    public void Rules_categorize_imports_before_history_and_on_demand()
    {
        var (s, bank) = Create();
        var transport = Cat(s, "Aplicativos e táxi");
        s.SaveRule(null, "uber", transport);
        Assert.Throws<FinanceValidationException>(() => s.SaveRule(null, "UBER", transport)); // repetida
        Assert.Throws<FinanceValidationException>(() => s.SaveRule(null, "x", transport));   // curta demais
        Assert.Equal(transport, s.MatchRule("UBER *TRIP 1234", income: false));
        Assert.Null(s.MatchRule("UBER *TRIP", income: true)); // categoria de despesa não vale para entrada

        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 1), Description = "Úber trip", Amount = 2_000, AccountId = bank });
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 2), Description = "Padaria", Amount = 1_000, AccountId = bank });
        Assert.Equal(1, s.ApplyRulesToUncategorized());
        Assert.Equal(transport, s.Transactions.Single(t => t.Description == "Úber trip").CategoryId);

        var preview = s.PreviewImport(bank, [new ImportedRow(1, D(11, 5), -2_300, "UBER TRIP SAO PAULO", null)]);
        Assert.Equal(transport, preview[0].SuggestedCategoryId);
        s.DeleteRule(s.Rules.Single().Id);
        Assert.Empty(s.Rules);
    }

    [Fact]
    public void Protected_backup_round_trips_and_rejects_wrong_password_or_tampering()
    {
        var plain = Encoding.UTF8.GetBytes("SQLite format 3 — dados de teste");
        var encrypted = ProtectedBackup.Encrypt(plain, "senha-forte-1", iterations: 100_000);
        Assert.DoesNotContain("dados de teste", Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.Equal(plain, ProtectedBackup.Decrypt(encrypted, "senha-forte-1"));
        Assert.Throws<FinanceValidationException>(() => ProtectedBackup.Decrypt(encrypted, "senha-errada"));
        var tampered = (byte[])encrypted.Clone();
        tampered[^1] ^= 0xFF;
        Assert.Throws<FinanceValidationException>(() => ProtectedBackup.Decrypt(tampered, "senha-forte-1"));
        Assert.Throws<FinanceValidationException>(() => ProtectedBackup.Decrypt("nada"u8.ToArray(), "senha-forte-1"));
        Assert.Throws<FinanceValidationException>(() => ProtectedBackup.Encrypt(plain, "curta"));
        Assert.NotEqual(encrypted, ProtectedBackup.Encrypt(plain, "senha-forte-1", iterations: 100_000)); // sal e nonce novos
    }
}

/// <summary>Desempenho com volume realista (Fase 13): 50 mil lançamentos, 2 cartões, recorrências e metas.</summary>
public sealed class FinancePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Fifty_thousand_transactions_stay_responsive()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var store = new MemoryFinanceStore();
        var s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = 10_000_000, OpeningDate = new DateOnly(2018, 1, 1) }).Id;
        var wallet = s.SaveAccount(s.NewAccount() with { Name = "Carteira", Kind = AccountKind.Cash, OpeningDate = new DateOnly(2018, 1, 1) }).Id;
        var cards = new[]
        {
            s.SaveCard(s.NewCard() with { Name = "A", Limit = 5_000_000, Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)] }).Id,
            s.SaveCard(s.NewCard() with { Name = "B", Limit = 5_000_000, Rules = [new CardCycleRule(DateOnly.MinValue, 20, 28)] }).Id,
        };
        var categories = s.Categories.Where(c => c.Kind == CategoryKind.Expense).Select(c => c.Id).ToArray();
        var random = new Random(42);
        var now = time.GetUtcNow();
        var batch = new List<FinanceTransaction>(50_000);
        for (var i = 0; i < 46_000; i++)
        {
            var date = new DateOnly(2019, 1, 1).AddDays(random.Next(0, 2_900));
            var income = i % 20 == 0;
            batch.Add(new FinanceTransaction
            {
                Id = Guid.CreateVersion7(now.AddTicks(i)),
                Kind = income ? TransactionKind.Income : TransactionKind.Expense,
                Date = date,
                Description = $"Lançamento {i}",
                CategoryId = income ? null : categories[i % categories.Length],
                Postings = [new Posting(i % 7 == 0 ? wallet : bank, income ? 400_000 : -random.Next(100, 50_000))],
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        store.SaveTransactions(batch);
        // Recarrega como na abertura do app, e acrescenta compras no cartão pelo serviço (com parcelas).
        s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        for (var i = 0; i < 400; i++)
        {
            s.SavePurchase(new PurchaseDraft { CardId = cards[i % 2], Date = new DateOnly(2025, 1, 1).AddDays(i * 1.5 is var d ? (int)d : 0), Description = $"Compra {i}", Total = 120_000, Installments = 10 });
        }
        for (var i = 0; i < 20; i++)
        {
            s.SaveRecurring(s.NewRecurring() with { Description = $"Conta {i}", Amount = 10_000 + i, AccountId = bank, StartDate = new DateOnly(2025, 1, 1 + i) });
        }
        Assert.True(s.Transactions.Count >= 50_000);

        long Measure(string name, Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            output.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms");
            return sw.ElapsedMilliseconds;
        }
        var times = new Dictionary<string, long>
        {
            ["Visão geral"] = Measure("Visão geral", () => s.Overview(2026, 11)),
            ["Saldo dia a dia"] = Measure("Saldo dia a dia", () => s.DailyBalances(2026, 11)),
            ["Lançamentos do mês"] = Measure("Lançamentos do mês", () => s.Query(new TransactionFilter { From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 30) })),
            ["Busca em tudo"] = Measure("Busca em tudo", () => s.Query(new TransactionFilter { Text = "lançamento 4999" })),
            ["Faturas"] = Measure("Faturas", () => s.Statements(cards[0])),
            ["Orçamento"] = Measure("Orçamento", () => s.BudgetMonth(2026, 11)),
            ["Projeção 12 meses"] = Measure("Projeção 12 meses", () => s.Project(new DateOnly(2027, 11, 15))),
            ["Relatório 12 meses"] = Measure("Relatório 12 meses", () => s.Report(new DateOnly(2025, 12, 1), new DateOnly(2026, 11, 30))),
        };
        // Limites folgados (máquinas de teste variam); os valores medidos vão para o benchmark.
        Assert.All(times, kv => Assert.True(kv.Value < 3_000, $"{kv.Key} levou {kv.Value} ms"));
    }
}
