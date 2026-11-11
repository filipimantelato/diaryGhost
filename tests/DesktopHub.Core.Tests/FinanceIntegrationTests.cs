using System.Text;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

public sealed class StatementParserTests
{
    [Fact]
    public void Csv_with_semicolon_decimal_comma_and_brazilian_dates_is_detected_and_read()
    {
        const string csv = "Data;Descrição;Valor\r\n05/11/2026;PADARIA DO ZÉ;-12,50\r\n06/11/2026;\"Salário; empresa\";5.000,00\r\n07/11/2026;Mercado;-1.234,56\r\n";
        var layout = StatementParser.Detect(csv);
        Assert.Equal(';', layout.Delimiter);
        Assert.True(layout.HasHeader);
        Assert.Equal((0, 1, 2), (layout.DateColumn, layout.DescriptionColumn, layout.AmountColumn!.Value));
        Assert.True(layout.DecimalComma);
        var parsed = StatementParser.ParseCsv(csv, layout);
        Assert.Empty(parsed.Errors);
        Assert.Equal([-1_250, 500_000, -123_456], parsed.Rows.Select(r => r.Amount).ToArray());
        Assert.Equal("Salário; empresa", parsed.Rows[1].Description);
        Assert.Equal(new DateOnly(2026, 11, 7), parsed.Rows[2].Date);
    }

    [Fact]
    public void Csv_with_debit_and_credit_columns_and_invalid_lines()
    {
        const string csv = "Data,Histórico,Débito,Crédito\n2026-11-05,Luz,150.00,\n2026-11-06,Pix recebido,,80.10\nxx,quebrada,1,\n2026-11-07,Juros,0.001,\n";
        var layout = StatementParser.Detect(csv);
        Assert.Equal(',', layout.Delimiter);
        Assert.False(layout.DecimalComma);
        Assert.Equal((2, 3), (layout.DebitColumn!.Value, layout.CreditColumn!.Value));
        var parsed = StatementParser.ParseCsv(csv, layout);
        Assert.Equal([-15_000, 8_010], parsed.Rows.Select(r => r.Amount).ToArray());
        Assert.Equal(2, parsed.Errors.Count); // data inválida e fração de centavo (nunca arredonda)
    }

    [Fact]
    public void Amounts_are_exact()
    {
        Assert.True(StatementParser.TryParseAmount("R$ 1.234,56", true, out var a) && a == 123_456);
        Assert.True(StatementParser.TryParseAmount("(10,00)", true, out var b) && b == -1_000);
        Assert.True(StatementParser.TryParseAmount("10,00-", true, out var c) && c == -1_000);
        Assert.True(StatementParser.TryParseAmount("-1,234.5", false, out var d) && d == -123_450);
        Assert.False(StatementParser.TryParseAmount("12,345", true, out _));
        Assert.False(StatementParser.TryParseAmount("abc", true, out _));
    }

    private const string Ofx1 = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX>
        <BANKMSGSRSV1><STMTTRNRS><STMTRS>
        <CURDEF>BRL
        <BANKACCTFROM><BANKID>0001<ACCTID>12345-6789
        </BANKACCTFROM>
        <BANKTRANLIST>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20261105120000[-3:BRT]
        <TRNAMT>-45.90
        <FITID>A1
        <MEMO>FARMACIA &amp; CIA
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20261106
        <TRNAMT>1500.00
        <FITID>A2
        <NAME>PIX RECEBIDO
        </BANKTRANLIST>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public void Ofx_1_sgml_without_closing_tags_is_read()
    {
        Assert.True(StatementParser.LooksLikeOfx(Ofx1));
        var parsed = StatementParser.ParseOfx(Ofx1);
        Assert.Empty(parsed.Errors);
        Assert.Equal("BRL", parsed.Currency);
        Assert.Equal("6789", parsed.AccountHint);
        Assert.Equal([-4_590, 150_000], parsed.Rows.Select(r => r.Amount).ToArray());
        Assert.Equal(["A1", "A2"], parsed.Rows.Select(r => r.ExternalId!).ToArray());
        Assert.Equal("FARMACIA & CIA", parsed.Rows[0].Description);
        Assert.Equal(new DateOnly(2026, 11, 5), parsed.Rows[0].Date);
    }

    [Fact]
    public void Ofx_2_xml_is_read_and_dtd_is_rejected()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?><?OFX OFXHEADER="200" VERSION="211"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>BRL</CURDEF><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261107</DTPOSTED><TRNAMT>-9.99</TRNAMT><FITID>X9</FITID><NAME>Streaming</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var parsed = StatementParser.ParseOfx(xml);
        var row = Assert.Single(parsed.Rows);
        Assert.Equal((-999L, "X9", "Streaming"), (row.Amount, row.ExternalId, row.Description));
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><OFX>&e;</OFX>";
        Assert.Throws<FinanceValidationException>(() => StatementParser.ParseOfx(evil));
    }

    [Fact]
    public void Decode_falls_back_to_latin1_and_rejects_huge_files()
    {
        var latin1 = Encoding.Latin1.GetBytes("Descrição;Valor");
        Assert.Equal("Descrição;Valor", StatementParser.Decode(latin1));
        Assert.Equal("ok", StatementParser.Decode([0xEF, 0xBB, 0xBF, (byte)'o', (byte)'k']));
        Assert.Throws<FinanceValidationException>(() => StatementParser.Decode(new byte[StatementParser.MaxBytes + 1]));
    }
}

public sealed class FinanceImportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static (FinanceService S, MemoryFinanceStore Store, Guid Bank) Create()
    {
        var store = new MemoryFinanceStore();
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(store, new EventBus(NullLogger<EventBus>.Instance), time, NullLogger<FinanceService>.Instance);
        s.Start();
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = 100_000, OpeningDate = D(9, 1) }).Id;
        return (s, store, bank);
    }

    private static ImportedRow Row(int line, DateOnly date, long amount, string description, string? fitId = null) => new(line, date, amount, description, fitId);

    [Fact]
    public void Same_file_twice_imports_nothing_new_and_repeated_lines_stay_distinct()
    {
        var (s, _, bank) = Create();
        ImportedRow[] rows = [Row(1, D(11, 5), -500, "Café"), Row(2, D(11, 5), -500, "Café"), Row(3, D(11, 6), 300_000, "Salário")];
        var preview = s.PreviewImport(bank, rows);
        Assert.All(preview, c => Assert.Equal(ImportRowStatus.New, c.Status));
        Assert.Equal(3, preview.Select(c => c.Fingerprint).Distinct().Count());
        var batch = s.Import(bank, ImportFormat.Csv, @"C:\x\extrato.csv", new string('a', 64), preview, _ => null, skipped: 0);
        Assert.Equal("extrato.csv", batch.FileName);
        Assert.Equal(100_000 - 1_000 + 300_000, s.Balance(bank, D(11, 15)));
        var income = s.Transactions.Single(t => t.Amount == 300_000);
        Assert.Equal(TransactionKind.Income, income.Kind);
        Assert.Equal(300_000, s.Totals(D(11, 1), D(11, 30)).Income);

        var again = s.PreviewImport(bank, rows);
        Assert.All(again, c => Assert.Equal(ImportRowStatus.AlreadyImported, c.Status));
        Assert.Throws<FinanceValidationException>(() => s.Import(bank, ImportFormat.Csv, "e.csv", new string('a', 64), again, _ => null, 0));
        Assert.NotNull(s.PreviousImport(new string('a', 64)));
    }

    [Fact]
    public void Repeated_fitid_and_possible_duplicates_are_flagged()
    {
        var (s, _, bank) = Create();
        s.Import(bank, ImportFormat.Ofx, "a.ofx", new string('b', 64), s.PreviewImport(bank, [Row(1, D(11, 3), -4_590, "Farmácia", "A1")]), _ => null, 0);
        var manual = s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(11, 8), Description = "Mercado", Amount = 12_000, AccountId = s.Accounts[0].Id });
        var preview = s.PreviewImport(bank,
        [
            Row(1, D(11, 3), -4_590, "FARMACIA CENTRO", "A1"), // mesmo FITID, descrição diferente
            Row(2, D(11, 10), -12_000, "SUPERMERCADO X"),       // valor igual, 2 dias depois
            Row(3, D(11, 11), -12_000, "SUPERMERCADO X"),       // o lançamento manual já "casou" com a anterior
            Row(4, D(8, 30), -100, "Antes da abertura"),
        ]);
        Assert.Equal([ImportRowStatus.AlreadyImported, ImportRowStatus.PossibleDuplicate, ImportRowStatus.New, ImportRowStatus.BeforeOpening], preview.Select(c => c.Status).ToArray());
        Assert.Equal(manual.Id, preview[1].MatchId);
        Assert.False(preview[1].IncludedByDefault);
    }

    [Fact]
    public void Categories_are_suggested_from_history_and_undo_removes_the_batch()
    {
        var (s, _, bank) = Create();
        var food = s.Categories.First(c => c.Name == "Mercado").Id;
        s.Save(new TransactionDraft { Kind = TransactionKind.Expense, Date = D(10, 2), Description = "Supermercado X", Amount = 5_000, AccountId = bank, CategoryId = food });
        var preview = s.PreviewImport(bank, [Row(1, D(11, 2), -7_000, "SUPERMERCADO  X ")]);
        Assert.Equal(food, preview[0].SuggestedCategoryId);
        var batch = s.Import(bank, ImportFormat.Csv, "n.csv", new string('c', 64), preview, c => c.SuggestedCategoryId, 0);
        var imported = Assert.Single(s.BatchTransactions(batch.Id));
        Assert.Equal(food, imported.CategoryId);
        // Editar não desliga do lote; desfazer remove inclusive o editado.
        s.Save(FinanceService.DraftFrom(imported) with { Description = "Mercado do mês" });
        Assert.Equal(batch.Id, s.Find(imported.Id)!.ImportBatchId);
        Assert.Equal(1, s.UndoImport(batch.Id));
        Assert.Null(s.Find(imported.Id));
        Assert.NotNull(s.ImportBatches.Single().UndoneAt);
        Assert.Throws<FinanceValidationException>(() => s.UndoImport(batch.Id));
        // Desfeito, o mesmo arquivo pode entrar de novo.
        Assert.Equal(ImportRowStatus.New, s.PreviewImport(bank, [Row(1, D(11, 2), -7_000, "SUPERMERCADO  X ")])[0].Status);
    }
}

public sealed class FinanceReminderTests
{
    // Hoje = 15/11/2026 07:00 em UTC (aviso das 09:00 ainda não passou).
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 7, 0, 0, TimeSpan.Zero);

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    private static (FinanceService S, FinanceReminders Reminders, FinanceCalendarOverlay Overlay, SettingsService Settings, Guid Bank, Guid Card) Create()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new FinanceService(new MemoryFinanceStore(), bus, time, NullLogger<FinanceService>.Instance);
        s.Start();
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        var bank = s.SaveAccount(s.NewAccount() with { Name = "Banco", OpeningBalance = 100_000, OpeningDate = new DateOnly(2026, 9, 1) }).Id;
        var card = s.SaveCard(s.NewCard() with { Name = "Roxo", Limit = 500_000, PaymentAccountId = bank, Rules = [new CardCycleRule(DateOnly.MinValue, 3, 10)] }).Id;
        return (s, new FinanceReminders(s, settings, bus), new FinanceCalendarOverlay(s, settings, bus), settings, bank, card);
    }

    private static DateTimeOffset At(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bill_reminder_fires_n_days_before_at_nine_without_amounts_and_disappears_when_confirmed()
    {
        var (s, reminders, _, _, bank, _) = Create();
        var bill = s.SaveRecurring(s.NewRecurring() with { Description = "Luz", Amount = 15_000, AccountId = bank, StartDate = new DateOnly(2026, 11, 20), RemindDaysBefore = 3 });
        var triggers = reminders.TriggersBetween(T0, At(11, 30), null);
        var t = Assert.Single(triggers);
        Assert.Equal(At(11, 17), t.FireAt);
        Assert.Equal(At(11, 20), t.Anchor);
        Assert.Equal("Conta a pagar: Luz", t.Title);
        Assert.StartsWith("Finance:", t.Key, StringComparison.Ordinal);
        var body = reminders.Body(t, At(11, 17));
        Assert.Equal("Vence em 3 dias (20/11) · confirme o pagamento em Finanças.", body);
        Assert.DoesNotContain("150", body, StringComparison.Ordinal);

        s.Confirm(bill.Id, new DateOnly(2026, 11, 20));
        Assert.Empty(reminders.TriggersBetween(T0, At(11, 30), null));
        Assert.DoesNotContain(reminders.TriggersBetween(T0, At(12, 1), null), x => x.Anchor == At(11, 20));
    }

    [Fact]
    public void Statement_reminder_uses_setting_and_can_be_turned_off()
    {
        var (s, reminders, _, settings, _, card) = Create();
        s.SavePurchase(new PurchaseDraft { CardId = card, Date = new DateOnly(2026, 11, 1), Description = "Fone", Total = 20_000, Installments = 1 });
        // Fatura fechou em 03/11 e vence em 10/11 (já passou); a seguinte (fecha 03/12, vence 10/12) está vazia.
        s.SavePurchase(new PurchaseDraft { CardId = card, Date = new DateOnly(2026, 11, 14), Description = "Livro", Total = 5_000, Installments = 1 });
        var t = Assert.Single(reminders.TriggersBetween(T0, At(12, 31), null), x => x.Title == "Fatura do cartão Roxo" && x.Anchor == At(12, 10));
        Assert.Equal(At(12, 7), t.FireAt);
        Assert.Contains("Cartões", reminders.Body(t, At(12, 7)), StringComparison.Ordinal);

        settings.UpdateFinance(f => f with { StatementReminderDays = 1 });
        Assert.Contains(reminders.TriggersBetween(T0, At(12, 31), null), x => x.FireAt == At(12, 9));
        settings.UpdateFinance(f => f with { DueReminders = false });
        Assert.Empty(reminders.TriggersBetween(T0, At(12, 31), null));
    }

    [Fact]
    public void Calendar_overlay_lists_due_items_and_masks_values_in_privacy_mode()
    {
        var (s, _, overlay, settings, bank, card) = Create();
        var bill = s.SaveRecurring(s.NewRecurring() with { Description = "Luz", Amount = 15_000, AccountId = bank, StartDate = new DateOnly(2026, 11, 20) });
        s.SaveRecurring(s.NewRecurring(RecurringKind.Income) with { Description = "Freela", Amount = 50_000, AccountId = bank, StartDate = new DateOnly(2026, 11, 25) });
        s.SavePurchase(new PurchaseDraft { CardId = card, Date = new DateOnly(2026, 11, 14), Description = "Livro", Total = 5_000, Installments = 1 });
        s.Confirm(bill.Id, new DateOnly(2026, 11, 20));

        var items = overlay.Between(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31));
        var luz = items.First(i => i.Title == "Luz");
        Assert.True(luz.Done);
        Assert.Contains("R$ 150,00", luz.Detail, StringComparison.Ordinal);
        Assert.Contains(items, i => i.Title == "↑ Freela" && !i.Done);
        Assert.Contains(items, i => i.Title == "Fatura Roxo" && i.Date == new DateOnly(2026, 12, 10));

        settings.UpdateFinance(f => f with { HideValues = true });
        Assert.All(overlay.Between(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)), i => Assert.Contains("••••", i.Detail, StringComparison.Ordinal));
    }
}

public sealed class FinanceReminderDeliveryTests
{
    private sealed class Sink : DesktopHub.Core.Notifications.INotificationSink
    {
        public List<DesktopHub.Core.Notifications.NotificationRequest> Shown { get; } = [];
        public DesktopHub.Core.Notifications.NotificationChannelStatus Status => DesktopHub.Core.Notifications.NotificationChannelStatus.Enabled;
        public event Action<Guid, string?>? Activated;

        public DesktopHub.Core.Notifications.DeliveryOutcome Show(Guid id, DesktopHub.Core.Notifications.NotificationRequest request)
        {
            Shown.Add(request);
            return DesktopHub.Core.Notifications.DeliveryOutcome.Shown;
        }

        public void Raise() => Activated?.Invoke(Guid.Empty, null);
    }

    private sealed class History : DesktopHub.Core.Notifications.INotificationHistory
    {
        private readonly List<DesktopHub.Core.Notifications.NotificationRecord> _records = [];
        public void Add(DesktopHub.Core.Notifications.NotificationRecord record) => _records.Add(record);
        public bool ExistsSince(string dedupKey, DateTimeOffset since) => _records.Any(r => r.DedupKey == dedupKey && r.CreatedAt >= since);
        public int CountShownSince(DesktopHub.Core.Notifications.NotificationCategory category, DateTimeOffset since) => _records.Count(r => r.Category == category && r.CreatedAt >= since);
        public IReadOnlyList<DesktopHub.Core.Notifications.NotificationRecord> Recent(int count) => [.. _records.TakeLast(count)];
        public int PruneBefore(DateTimeOffset cutoff) => 0;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    [Fact]
    public void Scheduler_delivers_finance_reminder_once_and_without_amounts()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 7, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        var finance = new FinanceService(new MemoryFinanceStore(), bus, time, NullLogger<FinanceService>.Instance);
        finance.Start();
        var bank = finance.SaveAccount(finance.NewAccount() with { Name = "Banco", OpeningBalance = 100_000, OpeningDate = new DateOnly(2026, 9, 1) }).Id;
        var agenda = new AgendaService(new MemoryAgendaStore(), bus, time, NullLogger<AgendaService>.Instance);
        agenda.Start();
        var sink = new Sink();
        var center = new DesktopHub.Core.Notifications.NotificationCenter(sink, new History(), settings, bus, time, NullLogger<DesktopHub.Core.Notifications.NotificationCenter>.Instance);
        using var scheduler = new ReminderScheduler(agenda, new MemoryReminderLog(), center, bus, time, NullLogger<ReminderScheduler>.Instance,
            [new FinanceReminders(finance, settings, bus)]);
        scheduler.Start();

        // Conta criada depois do início: o agendador reavalia ao mudar as finanças.
        finance.SaveRecurring(finance.NewRecurring() with { Description = "Internet", Amount = 12_990, AccountId = bank, StartDate = new DateOnly(2026, 11, 17), RemindDaysBefore = 1 });
        Assert.Empty(sink.Shown);
        time.Advance(TimeSpan.FromHours(26) + TimeSpan.FromMinutes(1)); // 16/11 09:01
        var shown = Assert.Single(sink.Shown);
        Assert.Equal("Conta a pagar: Internet", shown.Title);
        Assert.StartsWith("Vence amanhã (17/11)", shown.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("129", shown.Body, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromDays(2));
        Assert.Single(sink.Shown);
    }
}
