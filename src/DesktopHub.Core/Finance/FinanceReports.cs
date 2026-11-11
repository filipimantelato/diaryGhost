using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DesktopHub.Core.Finance;

/// <summary>Uma linha mensal do relatório: receitas, despesas, gasto no cartão e saldo do disponível no fim do mês.</summary>
/// <param name="EndBalance">Saldo do disponível no último dia (confirmado nos meses passados; previsto no atual e futuros).</param>
public sealed record MonthReportLine(int Year, int Month, long Income, long Expense, long CardSpending, long EndBalance, bool IsForecast)
{
    public long Net => Income - Expense;
}

/// <summary>Quanto já está comprometido em faturas de cartão nos próximos meses (parcelas já lançadas).</summary>
public sealed record CommittedMonth(int Year, int Month, long Amount);

/// <summary>Relatório de um período (datas inclusivas), comparado com o período anterior de mesma duração.</summary>
public sealed record FinanceReport(
    DateOnly From,
    DateOnly To,
    PeriodTotals Totals,
    PeriodTotals PreviousTotals,
    IReadOnlyList<MonthReportLine> Months,
    IReadOnlyList<CategorySpending> Categories,
    IReadOnlyList<CategorySpending> PreviousCategories,
    IReadOnlyList<FinanceTransaction> TopExpenses,
    IReadOnlyList<CommittedMonth> Committed,
    long RecurringMonthly,
    long SubscriptionsMonthly)
{
    public int MonthCount => Months.Count;
    public long AverageExpense => Months.Count == 0 ? 0 : Money.DivideRounded(Totals.Expense, Months.Count);
    public long AverageIncome => Months.Count == 0 ? 0 : Money.DivideRounded(Totals.Income, Months.Count);
}

/// <summary>Regra de categorização: descrição que contém o texto → categoria (aplicada na importação e sob demanda).</summary>
public sealed record CategoryRule
{
    public required Guid Id { get; init; }
    /// <summary>Texto procurado na descrição (sem diferenciar maiúsculas e acentos).</summary>
    public required string Pattern { get; init; }
    public required Guid CategoryId { get; init; }
    public int SortOrder { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed partial class FinanceService
{
    public const int MaxRules = 200;

    private List<CategoryRule> _rules = [];

    // ---------- Relatórios ----------

    /// <summary>Relatório do período: totais, mês a mês, categorias, maiores despesas e o que está comprometido.</summary>
    public FinanceReport Report(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }
        var days = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(days - 1));
        var today = Today;
        var months = new List<MonthReportLine>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            var (start, end) = MonthRange(m.Year, m.Month);
            var clipStart = start < from ? from : start;
            var clipEnd = end > to ? to : end;
            var totals = Totals(clipStart, clipEnd);
            long card = 0;
            lock (_gate)
            {
                foreach (var t in _transactions)
                {
                    if (t.CardId is null || t.Date < clipStart || t.Date > clipEnd || CurrencyOf(t) != BaseCurrency)
                    {
                        continue;
                    }
                    card = t.Kind switch
                    {
                        TransactionKind.Expense => checked(card + t.Amount),
                        TransactionKind.Refund => checked(card - t.Amount),
                        _ => card,
                    };
                }
            }
            var forecast = clipEnd > today;
            var endBalance = forecast ? Overview(m.Year, m.Month).Forecast : AvailableForecastAt(clipEnd);
            months.Add(new MonthReportLine(m.Year, m.Month, totals.Income, totals.Expense, card, endBalance, forecast));
        }
        var top = Transactions.Where(t => t.Kind == TransactionKind.Expense && t.Date >= from && t.Date <= to && CurrencyOf(t) == BaseCurrency)
            .OrderByDescending(t => t.Amount).ThenByDescending(t => t.Date).Take(10).ToList();

        // Parcelas e compras já lançadas em faturas que vencem nos próximos 12 meses.
        var committed = new Dictionary<(int, int), long>();
        foreach (var card in Cards.Where(c => !c.IsArchived && c.Currency == BaseCurrency))
        {
            foreach (var s in Statements(card.Id).Where(s => s.Due > today && s.Due <= today.AddMonths(12) && s.Charges > 0))
            {
                var key = (s.Due.Year, s.Due.Month);
                committed[key] = checked(committed.GetValueOrDefault(key) + s.Charges);
            }
        }
        long recurring = 0;
        foreach (var item in Recurring.Where(r => !r.IsArchived && !r.IsIncome && CurrencyOfRecurring(r) == BaseCurrency && (r.EndDate is null || r.EndDate >= today)))
        {
            recurring = checked(recurring + RecurringSchedule.MonthlyCost(item, ExpectedAmount(item)));
        }
        return new FinanceReport(from, to, Totals(from, to), Totals(previousFrom, previousTo), months,
            SpendingByCategory(from, to), SpendingByCategory(previousFrom, previousTo), top,
            [.. committed.OrderBy(kv => kv.Key).Select(kv => new CommittedMonth(kv.Key.Item1, kv.Key.Item2, kv.Value))],
            recurring, SubscriptionTotals().Monthly);
    }

    // ---------- Exportação CSV ----------

    /// <summary>
    /// Lançamentos do período em CSV no padrão brasileiro (";" e vírgula decimal, UTF-8 com BOM para o Excel).
    /// Células de texto que começam com = + - @ ou tabulação recebem um apóstrofo (proteção contra fórmulas maliciosas).
    /// </summary>
    public string ExportCsv(DateOnly from, DateOnly to)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        sb.Append("Data;Descrição;Categoria;Conta ou cartão;Tipo;Situação;Valor;Moeda;Observações;Etiquetas\r\n");
        foreach (var t in Transactions.Where(t => t.Date >= from && t.Date <= to).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt))
        {
            var where = t.CardId is { } card ? "Cartão " + (Card(card)?.Name ?? "?")
                : t.ToAccountId is { } to2 ? $"{Account(t.AccountId)?.Name} → {Account(to2)?.Name}"
                : Account(t.AccountId)?.Name ?? "?";
            var signed = t.Kind == TransactionKind.Transfer ? t.Amount : t.Postings.Sum(p => p.Amount);
            var currency = CurrencyOf(t) ?? BaseCurrency;
            var value = Money.ToDecimal(signed, currency).ToString("0.00", culture);
            string[] cells =
            [
                t.Date.ToString("dd/MM/yyyy", culture),
                t.Description,
                t.Kind is TransactionKind.Transfer ? "" : CategoryPath(t.CategoryId),
                where,
                KindName(t),
                t.Status == TransactionStatus.Cleared ? "Confirmado" : "Pendente",
            ];
            sb.AppendJoin(';', cells.Select(CsvCell)).Append(';').Append(value).Append(';').Append(currency).Append(';')
                .Append(CsvCell(t.Notes ?? "")).Append(';').Append(CsvCell(string.Join(", ", t.Tags))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string KindName(FinanceTransaction t) => t switch
    {
        { IsCardPayment: true } => "Pagamento de fatura",
        { Kind: TransactionKind.Income } => "Receita",
        { Kind: TransactionKind.Expense, PurchaseId: not null } => $"Compra no cartão (parcela {t.InstallmentNumber})",
        { Kind: TransactionKind.Expense } => "Despesa",
        { Kind: TransactionKind.Transfer } => "Transferência",
        { Kind: TransactionKind.Refund } => "Estorno",
        _ => "Ajuste",
    };

    /// <summary>Célula de texto segura: neutraliza fórmulas e escapa aspas, separador e quebras de linha.</summary>
    public static string CsvCell(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text;
        if (t.Length > 0 && t[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            t = "'" + t;
        }
        return t.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : t;
    }

    // ---------- Regras de categorização ----------

    public IReadOnlyList<CategoryRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return [.. _rules.OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)];
            }
        }
    }

    public CategoryRule SaveRule(Guid? id, string pattern, Guid categoryId)
    {
        var text = (pattern ?? "").Trim();
        if (text.Length is < 2 or > MaxNameLength)
        {
            throw new FinanceValidationException($"O texto da regra tem de 2 a {MaxNameLength} caracteres.");
        }
        if (Category(categoryId) is not { Kind: CategoryKind.Expense or CategoryKind.Income, IsArchived: false })
        {
            throw new FinanceValidationException("Escolha uma categoria de despesa ou de receita.");
        }
        CategoryRule? existing;
        lock (_gate)
        {
            existing = id is null ? null : _rules.FirstOrDefault(r => r.Id == id);
            if (existing is null && _rules.Count >= MaxRules)
            {
                throw new FinanceValidationException($"Limite de {MaxRules} regras.");
            }
            if (_rules.Any(r => r.Id != id && DescriptionKey(r.Pattern) == DescriptionKey(text) && Category(r.CategoryId)?.Kind == Category(categoryId)?.Kind))
            {
                throw new FinanceValidationException("Já existe uma regra com esse texto.");
            }
        }
        var value = new CategoryRule
        {
            Id = existing?.Id ?? Guid.CreateVersion7(Now),
            Pattern = text,
            CategoryId = categoryId,
            SortOrder = existing?.SortOrder ?? Rules.Count,
            CreatedAt = existing?.CreatedAt ?? Now,
        };
        _store.SaveRule(value);
        lock (_gate)
        {
            _rules.RemoveAll(r => r.Id == value.Id);
            _rules.Add(value);
        }
        Changed();
        return value;
    }

    public void DeleteRule(Guid id)
    {
        _store.DeleteRule(id);
        lock (_gate)
        {
            _rules.RemoveAll(r => r.Id == id);
        }
        Changed();
    }

    /// <summary>Primeira regra (na ordem) cujo texto aparece na descrição e cuja categoria é do sentido certo.</summary>
    public Guid? MatchRule(string description, bool income)
    {
        var key = DescriptionKey(description ?? "");
        var kind = income ? CategoryKind.Income : CategoryKind.Expense;
        return Rules.FirstOrDefault(r => key.Contains(DescriptionKey(r.Pattern), StringComparison.Ordinal) && Category(r.CategoryId) is { IsArchived: false } c && c.Kind == kind)?.CategoryId;
    }

    /// <summary>Aplica as regras aos lançamentos sem categoria (receitas e despesas de conta). Devolve quantos mudaram.</summary>
    public int ApplyRulesToUncategorized()
    {
        var changed = new List<FinanceTransaction>();
        foreach (var t in Transactions.Where(t => t.CategoryId is null && t.PurchaseId is null && t.Kind is TransactionKind.Income or TransactionKind.Expense))
        {
            if (MatchRule(t.Description, t.Kind == TransactionKind.Income) is { } category)
            {
                changed.Add(t with { CategoryId = category, UpdatedAt = Now });
            }
        }
        if (changed.Count == 0)
        {
            return 0;
        }
        _store.SaveTransactions(changed);
        lock (_gate)
        {
            var byId = changed.ToDictionary(t => t.Id);
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (byId.TryGetValue(_transactions[i].Id, out var updated))
                {
                    _transactions[i] = updated;
                }
            }
        }
        Changed();
        return changed.Count;
    }
}

/// <summary>
/// Backup protegido (".dgfin"): AES-256-GCM com chave derivada da senha (PBKDF2-SHA256, 600 mil iterações, sal aleatório).
/// Formato: "DGFIN1" · iterações (4 bytes) · sal (16) · nonce (12) · tag (16) · dados cifrados. Senha esquecida = backup
/// perdido (não há porta dos fundos); arquivo alterado ou senha errada falham na verificação da tag.
/// </summary>
public static class ProtectedBackup
{
    private static readonly byte[] Magic = "DGFIN1"u8.ToArray();
    public const int Iterations = 600_000;
    public const int MinPasswordLength = 8;

    public static byte[] Encrypt(byte[] plain, string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw new FinanceValidationException($"Use uma senha com pelo menos {MinPasswordLength} caracteres.");
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }
        CryptographicOperations.ZeroMemory(key);
        var output = new byte[Magic.Length + 4 + salt.Length + nonce.Length + tag.Length + cipher.Length];
        var span = output.AsSpan();
        Magic.CopyTo(span);
        BitConverter.TryWriteBytes(span[Magic.Length..], iterations);
        salt.CopyTo(span[(Magic.Length + 4)..]);
        nonce.CopyTo(span[(Magic.Length + 20)..]);
        tag.CopyTo(span[(Magic.Length + 32)..]);
        cipher.CopyTo(span[(Magic.Length + 48)..]);
        return output;
    }

    public static byte[] Decrypt(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < Magic.Length + 48 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new FinanceValidationException("Este arquivo não é um backup protegido do Diary Ghost.");
        }
        var iterations = BitConverter.ToInt32(data, Magic.Length);
        if (iterations is < 100_000 or > 10_000_000)
        {
            throw new FinanceValidationException("Backup inválido.");
        }
        var salt = data.AsSpan(Magic.Length + 4, 16).ToArray();
        var nonce = data.AsSpan(Magic.Length + 20, 12);
        var tag = data.AsSpan(Magic.Length + 32, 16);
        var cipher = data.AsSpan(Magic.Length + 48);
        var key = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new FinanceValidationException("Senha errada ou arquivo alterado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plain;
    }
}
