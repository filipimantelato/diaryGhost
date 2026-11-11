using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using DesktopHub.Infrastructure.Finance;
using DesktopHub.Infrastructure.Logging;
using DesktopHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Infrastructure.Tests;

public sealed class FinancePersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static FinanceDatabase OpenDb(TempDataDir dir)
    {
        var db = new FinanceDatabase(dir.Paths, NullLogger<SqliteDatabase>.Instance);
        db.Initialize();
        return db;
    }

    private static FinanceService Service(FinanceDatabase db, ILoggerFactory? logs = null)
    {
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var factory = logs ?? NullLoggerFactory.Instance;
        var s = new FinanceService(new SqliteFinanceStore(db, factory.CreateLogger<SqliteFinanceStore>()), new EventBus(NullLogger<EventBus>.Instance), time, factory.CreateLogger<FinanceService>());
        s.Start();
        return s;
    }

    private static Guid Cat(FinanceService s, string name) => s.Categories.First(c => c.Name == name).Id;

    [Fact]
    public void Finance_lives_in_its_own_file_with_its_own_schema_version()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var db = OpenDb(dir);
        Assert.True(File.Exists(dir.Paths.FinanceDatabaseFile));
        Assert.NotEqual(dir.Paths.DatabaseFile, db.FilePath);
        using var connection = db.OpenConnection();
        Assert.Equal(FinanceMigrations.All[^1].Version, Migrator.GetVersion(connection));
        using var main = dir.Database.OpenConnection();
        using var cmd = main.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'fin_%';";
        Assert.Equal(0L, cmd.ExecuteScalar()); // nada do financeiro no hub.db
    }

    [Fact]
    public void Everything_survives_closing_and_reopening_the_app()
    {
        using var dir = new TempDataDir();
        var s = Service(OpenDb(dir));
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco; DROP TABLE fin_accounts;--", Institution = "Instituição", OpeningBalance = 123_456, OpeningDate = new DateOnly(2026, 1, 1), Color = "#6B9AC4" });
        var wallet = s.SaveAccount(s.NewAccount() with { Name = "Carteira", Kind = AccountKind.Cash, OpeningDate = new DateOnly(2026, 1, 1) });
        var sub = s.SaveCategory(s.NewCategory(CategoryKind.Expense, Cat(s, "Lazer")) with { Name = "Cinema" });
        var expense = s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = new DateOnly(2026, 11, 2), Description = "Ingresso 'estreia'", Amount = 4_550, AccountId = bank.Id, CategoryId = sub.Id, PaymentMethod = PaymentMethod.Pix, Notes = "linha 1\nlinha 2", Tags = ["filme", "sábado"], Status = TransactionStatus.Pending });
        var transfer = s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = new DateOnly(2026, 11, 3), Description = "Saque", Amount = 10_000, AccountId = bank.Id, ToAccountId = wallet.Id });
        var refund = s.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = Today, Description = "Estorno", Amount = 550, AccountId = bank.Id, RefundOfId = expense.Id });
        s.AdjustBalanceTo(wallet.Id, Today, 9_000, "contei as notas");
        var balances = s.CurrentBalances();

        dir.Reopen();
        var again = Service(OpenDb(dir));
        Assert.Equal(s.Accounts.OrderBy(a => a.Id), again.Accounts.OrderBy(a => a.Id));
        Assert.Equal(s.Categories.OrderBy(c => c.Id), again.Categories.OrderBy(c => c.Id));
        foreach (var t in s.Transactions)
        {
            var loaded = again.Find(t.Id)!;
            Assert.Equal(t with { Postings = [], Tags = [] }, loaded with { Postings = [], Tags = [] });
            Assert.Equal(t.Postings, loaded.Postings);
            Assert.Equal(t.Tags, loaded.Tags);
        }
        Assert.Equal(balances, again.CurrentBalances());
        Assert.Equal(transfer.Postings, again.Find(transfer.Id)!.Postings);
        Assert.Equal(expense.Id, again.Find(refund.Id)!.RefundOfId);
    }

    [Fact]
    public void Edit_replaces_postings_atomically_and_delete_cascades_and_is_audited_without_values()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var s = Service(db);
        var a = s.SaveAccount(s.NewAccount() with { Name = "A", OpeningDate = new DateOnly(2026, 1, 1) });
        var b = s.SaveAccount(s.NewAccount() with { Name = "B", OpeningDate = new DateOnly(2026, 1, 1) });
        var t = s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = Today, Description = "x", Amount = 777, AccountId = a.Id, ToAccountId = b.Id });
        s.Save(FinanceService.DraftFrom(t) with { Amount = 888, AccountId = b.Id, ToAccountId = a.Id });

        long Count(string sql)
        {
            using var c = db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar()!;
        }
        Assert.Equal(2, Count("SELECT count(*) FROM fin_postings;"));
        Assert.Equal(0, Count("SELECT sum(amount_minor) FROM fin_postings;"));

        s.Delete(t.Id);
        Assert.Equal(0, Count("SELECT count(*) FROM fin_postings;"));
        var store = new SqliteFinanceStore(db, NullLogger<SqliteFinanceStore>.Instance);
        var audit = Assert.Single(store.AuditLog());
        Assert.Equal(("delete", "transaction", t.Id, "Transfer"), (audit.Action, audit.Entity, audit.EntityId, audit.Detail));
    }

    [Fact]
    public void Database_constraints_reject_inconsistent_rows_even_bypassing_the_service()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var s = Service(db);
        var a = s.SaveAccount(s.NewAccount() with { Name = "A", OpeningDate = new DateOnly(2026, 1, 1) });
        var t = s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = Today, Description = "x", Amount = 1, AccountId = a.Id });

        void Fails(string sql)
        {
            using var c = db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        }
        Fails($"INSERT INTO fin_postings (transaction_id, seq, account_id, amount_minor) VALUES ('{t.Id}', 1, '{a.Id}', 0);"); // valor zero
        Fails($"INSERT INTO fin_postings (transaction_id, seq, account_id, card_id, amount_minor) VALUES ('{t.Id}', 1, NULL, NULL, 5);"); // sem conta
        Fails($"INSERT INTO fin_postings (transaction_id, seq, account_id, amount_minor) VALUES ('{t.Id}', 1, '{Guid.NewGuid()}', 5);"); // conta inexistente
        Fails($"DELETE FROM fin_accounts WHERE id = '{a.Id}';"); // conta com partidas
        Fails($"UPDATE fin_transactions SET date = '15/11/2026' WHERE id = '{t.Id}';"); // data fora do formato
        Fails($"UPDATE fin_transactions SET kind = 'Transfer', category_id = '{Cat(s, "Mercado")}' WHERE id = '{t.Id}';"); // transferência com categoria
        Fails($"UPDATE fin_postings SET amount_minor = 1.5 WHERE transaction_id = '{t.Id}';"); // STRICT: nada de ponto flutuante
    }

    [Fact]
    public void Store_refuses_transfers_that_do_not_sum_to_zero()
    {
        using var dir = new TempDataDir();
        var store = new SqliteFinanceStore(OpenDb(dir), NullLogger<SqliteFinanceStore>.Instance);
        var bad = new FinanceTransaction
        {
            Id = Guid.CreateVersion7(),
            Kind = TransactionKind.Transfer,
            Date = Today,
            Description = "x",
            Postings = [new(Guid.NewGuid(), -100), new(Guid.NewGuid(), 99)],
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        Assert.Throws<InvalidOperationException>(() => store.SaveTransaction(bad));
    }

    [Fact]
    public void Failed_transaction_write_rolls_back_the_header_too()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var s = Service(db);
        var a = s.SaveAccount(s.NewAccount() with { Name = "A", OpeningDate = new DateOnly(2026, 1, 1) });
        var store = new SqliteFinanceStore(db, NullLogger<SqliteFinanceStore>.Instance);
        // A segunda partida aponta para uma conta inexistente: a FK falha no meio da gravação.
        var broken = new FinanceTransaction
        {
            Id = Guid.CreateVersion7(),
            Kind = TransactionKind.Transfer,
            Date = Today,
            Description = "x",
            Postings = [new(a.Id, -100), new(Guid.NewGuid(), 100)],
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        Assert.Throws<SqliteException>(() => store.SaveTransaction(broken));
        Assert.Empty(store.Load().Transactions);
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM fin_transactions;";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Logs_never_contain_amounts_descriptions_or_account_names()
    {
        using var dir = new TempDataDir();
        var logDir = Path.Combine(dir.Paths.Root, "logs-test");
        using (var provider = new JsonFileLoggerProvider(logDir))
        using (var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace)))
        {
            var db = OpenDb(dir);
            var s = Service(db, factory);
            var a = s.SaveAccount(s.NewAccount() with { Name = "ContaSecretaXYZ", OpeningBalance = 987_654, OpeningDate = new DateOnly(2026, 1, 1) });
            s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = Today, Description = "DescricaoSecretaXYZ", Amount = 13_579, AccountId = a.Id, Notes = "NotaSecretaXYZ" });
            using (var c = db.OpenConnection())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "UPDATE fin_transactions SET kind = 'Inexistente';"; // força uma linha ilegível (gera aviso)
                Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery()); // CHECK impede; então remove as partidas
                cmd.CommandText = "DELETE FROM fin_postings;";
                cmd.ExecuteNonQuery();
            }
            Service(db, factory); // recarrega: registra contagens e o aviso de linha sem partidas
        }
        var text = string.Concat(Directory.GetFiles(logDir).Select(File.ReadAllText));
        Assert.Contains("Finanças carregadas", text);
        Assert.Contains("ilegível", text);
        foreach (var secret in new[] { "ContaSecretaXYZ", "DescricaoSecretaXYZ", "NotaSecretaXYZ", "987654", "9876,54", "9.876,54", "13579", "135,79" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Finance_module_has_no_network_dependency()
    {
        // ADR: o módulo financeiro nunca fala com a rede. Nenhum tipo do namespace referencia System.Net.Http.
        foreach (var assembly in new[] { typeof(FinanceService).Assembly, typeof(SqliteFinanceStore).Assembly })
        {
            foreach (var type in assembly.GetTypes().Where(t => t.Namespace?.Contains(".Finance", StringComparison.Ordinal) == true))
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                var referenced = type.GetFields(flags).Select(f => f.FieldType)
                    .Concat(type.GetConstructors(flags).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                    .Concat(type.GetMethods(flags).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
                Assert.DoesNotContain(referenced, t => t.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true);
            }
        }
    }

    // ---------- Cartões (migração 2) ----------

    private static (FinanceService S, Guid Bank, CreditCard Card) WithCard(FinanceDatabase db)
    {
        var s = Service(db);
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = 1_000_000, OpeningDate = new DateOnly(2026, 9, 1) }).Id;
        var card = s.SaveCard(s.NewCard() with { Name = "Roxo", Institution = "Banco X", Network = "Visa", LastDigits = "4321", Limit = 800_000, PaymentAccountId = bank, Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)] });
        return (s, bank, card);
    }

    [Fact]
    public void Cards_purchases_refunds_payments_and_overrides_survive_reopening()
    {
        using var dir = new TempDataDir();
        var (s, bank, card) = WithCard(OpenDb(dir));
        var purchase = s.SavePurchase(new PurchaseDraft { CardId = card.Id, Date = new DateOnly(2026, 11, 10), Description = "Notebook", Total = 600_000, Installments = 6, CategoryId = Cat(s, "Tecnologia"), Tags = ["trabalho"] });
        var first = s.Installments(purchase.Id)[0];
        s.Save(new TransactionDraft { Kind = TransactionKind.Refund, Date = new DateOnly(2026, 11, 12), Description = "Estorno parcial", Amount = 10_000, CardId = card.Id, RefundOfId = first.Id });
        s.Save(new TransactionDraft { Kind = TransactionKind.Transfer, Date = new DateOnly(2026, 11, 14), Description = "Pagamento", Amount = 50_000, AccountId = bank, CardId = card.Id, StatementClosing = new DateOnly(2026, 12, 3) });
        s.SetStatementDue(card.Id, new DateOnly(2026, 12, 3), new DateOnly(2026, 12, 11));
        s.ChangeCycle(card.Id, 15, 25);
        var statements = s.Statements(card.Id);
        var debt = s.Debt(card.Id);

        dir.Reopen();
        var again = Service(OpenDb(dir));
        Assert.Equal(s.Card(card.Id)! with { Rules = [] }, again.Card(card.Id)! with { Rules = [] });
        Assert.Equal(s.Card(card.Id)!.Rules, again.Card(card.Id)!.Rules);
        Assert.Equal(s.Purchase(purchase.Id)! with { Tags = [] }, again.Purchase(purchase.Id)! with { Tags = [] });
        Assert.Equal(s.Installments(purchase.Id).Select(t => (t.Id, t.Amount, t.Date, t.StatementClosing, t.InstallmentNumber)),
            again.Installments(purchase.Id).Select(t => (t.Id, t.Amount, t.Date, t.StatementClosing, t.InstallmentNumber)));
        Assert.Equal(statements, again.Statements(card.Id));
        Assert.Equal(debt, again.Debt(card.Id));
        Assert.Equal(600_000 - 10_000 - 50_000, again.Debt(card.Id));
        Assert.Equal(new DateOnly(2026, 12, 11), again.Statements(card.Id).Single(x => x.Closing == new DateOnly(2026, 12, 3)).Due);
    }

    [Fact]
    public void Purchase_edit_replaces_installments_atomically_and_delete_cascades()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var (s, _, card) = WithCard(db);
        var purchase = s.SavePurchase(new PurchaseDraft { CardId = card.Id, Date = new DateOnly(2026, 11, 10), Description = "Fone", Total = 30_000, Installments = 3 });
        s.SavePurchase(FinanceService.DraftFrom(purchase) with { Installments = 2 });

        long Scalar(string sql)
        {
            using var c = db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar()!;
        }
        Assert.Equal(2, Scalar("SELECT count(*) FROM fin_transactions WHERE purchase_id IS NOT NULL;"));
        Assert.Equal(-30_000, Scalar("SELECT sum(amount_minor) FROM fin_postings WHERE card_id IS NOT NULL;"));
        s.DeletePurchase(purchase.Id);
        Assert.Equal(0, Scalar("SELECT count(*) FROM fin_transactions;"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM fin_postings;"));
        Assert.Contains(new SqliteFinanceStore(db, NullLogger<SqliteFinanceStore>.Instance).AuditLog(), a => a.Entity == "purchase" && a.EntityId == purchase.Id);
    }

    [Fact]
    public void Store_refuses_installments_that_do_not_add_up()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var (_, _, card) = WithCard(db);
        var store = new SqliteFinanceStore(db, NullLogger<SqliteFinanceStore>.Instance);
        var purchase = new CardPurchase { Id = Guid.CreateVersion7(), CardId = card.Id, Date = Today, Description = "x", Total = 1_000, Installments = 1, CreatedAt = T0, UpdatedAt = T0 };
        var wrong = new FinanceTransaction
        {
            Id = Guid.CreateVersion7(),
            Kind = TransactionKind.Expense,
            Date = Today,
            Description = "x",
            PurchaseId = purchase.Id,
            InstallmentNumber = 1,
            Postings = [Posting.ForCard(card.Id, -999, new DateOnly(2026, 12, 3))],
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        Assert.Throws<InvalidOperationException>(() => store.SavePurchase(purchase, [wrong]));
        Assert.Empty(store.Load().Purchases);
    }

    [Fact]
    public void Database_guards_card_postings_and_card_deletion()
    {
        using var dir = new TempDataDir();
        var db = OpenDb(dir);
        var (s, _, card) = WithCard(db);
        s.SavePurchase(new PurchaseDraft { CardId = card.Id, Date = new DateOnly(2026, 11, 10), Description = "x", Total = 1_000 });
        var tx = s.Transactions.Single();

        void Fails(string sql)
        {
            using var c = db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        }
        Fails($"INSERT INTO fin_postings (transaction_id, seq, card_id, statement_closing, amount_minor) VALUES ('{tx.Id}', 1, '{Guid.NewGuid()}', '2026-12-03', 5);"); // cartão inexistente
        Fails($"INSERT INTO fin_postings (transaction_id, seq, card_id, amount_minor) VALUES ('{tx.Id}', 1, '{card.Id}', 5);"); // sem fatura
        Fails($"DELETE FROM fin_cards WHERE id = '{card.Id}';"); // cartão com lançamentos
        Fails($"UPDATE fin_cards SET last_digits = '1234567812345678' WHERE id = '{card.Id}';"); // nunca o número completo
        Fails($"UPDATE fin_card_purchases SET installments = 0;");
        Fails($"INSERT INTO fin_card_cycle_rules (card_id, effective_from, closing_day, due_day) VALUES ('{card.Id}', '2027-01-01', 32, 10);");
    }

    [Fact]
    public void Phase_8_database_is_migrated_with_a_backup_and_keeps_its_data()
    {
        using var dir = new TempDataDir();
        // Banco criado só com a migração 1 (como na versão 0.8/0.9), com dados.
        var v1 = new SqliteDatabase(dir.Paths, NullLogger<SqliteDatabase>.Instance, dir.Paths.FinanceDatabaseFile, [FinanceMigrations.All[0]]);
        v1.Initialize();
        using (var c = v1.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO fin_accounts VALUES ('0192f000-0000-7000-8000-000000000001', 'Antiga', 'Checking', NULL, 'BRL', 12345, '2026-01-01', '#9A86C4', 1, NULL, 0, 0, 0);
                INSERT INTO fin_transactions (id, kind, date, description, category_id, status, payment_method, notes, tags, refund_of_id, created_at_ms, updated_at_ms)
                    VALUES ('0192f000-0000-7000-8000-000000000002', 'Income', '2026-11-05', 'Salário', NULL, 'Cleared', 'Pix', NULL, NULL, NULL, 0, 0);
                INSERT INTO fin_postings (transaction_id, seq, account_id, card_id, amount_minor) VALUES ('0192f000-0000-7000-8000-000000000002', 0, '0192f000-0000-7000-8000-000000000001', NULL, 500000);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var report = new FinanceDatabase(dir.Paths, NullLogger<SqliteDatabase>.Instance).Initialize();
        Assert.Equal((1, FinanceMigrations.All[^1].Version), (report.Migration.FromVersion, report.Migration.ToVersion));
        Assert.NotNull(report.BackupFile);
        Assert.StartsWith("finance-v1-", Path.GetFileName(report.BackupFile));
        var s = Service(new FinanceDatabase(dir.Paths, NullLogger<SqliteDatabase>.Instance));
        Assert.Equal(12_345 + 500_000, s.Balance(s.Accounts.Single().Id, Today));
    }
}
