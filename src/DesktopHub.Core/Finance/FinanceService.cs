using System.Globalization;
using System.Text;
using DesktopHub.Core.Events;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Finance;

/// <summary>
/// Contas, categorias e lançamentos. Os dados ficam em memória (dezenas de milhares de linhas cabem com folga)
/// e toda mudança é gravada primeiro — se o banco falhar, a memória não muda. Saldos e totais são sempre
/// calculados a partir das partidas (ADR-030). Os logs registram só contagens, nunca valores ou descrições.
/// </summary>
public sealed partial class FinanceService
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 200;
    public const int MaxNotesLength = 4000;
    public const int MaxTags = 10;
    public const int MaxTagLength = 30;

    /// <summary>Categoria de sistema dos ajustes de saldo (fora de receitas e despesas).</summary>
    public static readonly Guid AdjustmentCategoryId = new("0192f000-0000-7000-8000-00000000a001");

    private readonly IFinanceStore _store;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<FinanceService> _logger;
    private readonly Lock _gate = new();
    private List<FinancialAccount> _accounts = [];
    private List<TransactionCategory> _categories = [];
    private List<FinanceTransaction> _transactions = [];

    public FinanceService(IFinanceStore store, IEventBus bus, TimeProvider time, ILogger<FinanceService> logger)
    {
        _store = store;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>Moeda dos totais consolidados (ADR-031). Contas em outra moeda aparecem separadas.</summary>
    public string BaseCurrency { get; init; } = Money.DefaultCurrency;

    /// <summary>Falha ao abrir o banco de finanças (o resto do app continua funcionando).</summary>
    public string? UnavailableReason { get; private set; }

    public bool IsAvailable => UnavailableReason is null && _started;

    private bool _started;

    public DateTimeOffset Now => _time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, _time.LocalTimeZone).DateTime);

    public void Start()
    {
        var snapshot = _store.Load();
        lock (_gate)
        {
            _accounts = [.. snapshot.Accounts];
            _categories = [.. snapshot.Categories];
            _transactions = [.. snapshot.Transactions];
            _cards = [.. snapshot.Cards];
            _purchases = [.. snapshot.Purchases];
            _overrides = [.. snapshot.StatementOverrides];
            LoadPlanning(snapshot);
        }
        _started = true;
        EnsureDefaultCategories();
        LogLoaded(snapshot.Accounts.Count, snapshot.Categories.Count, snapshot.Transactions.Count);
    }

    public void MarkUnavailable(string reason) => UnavailableReason = reason;

    // ---------- Leitura ----------

    public IReadOnlyList<FinancialAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                return [.. _accounts.OrderBy(a => a.IsArchived).ThenBy(a => a.SortOrder).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public IReadOnlyList<TransactionCategory> Categories
    {
        get
        {
            lock (_gate)
            {
                return [.. _categories];
            }
        }
    }

    public IReadOnlyList<FinanceTransaction> Transactions
    {
        get
        {
            lock (_gate)
            {
                return [.. _transactions];
            }
        }
    }

    public FinancialAccount? Account(Guid? id) => id is null ? null : Accounts.FirstOrDefault(a => a.Id == id);

    public TransactionCategory? Category(Guid? id) => id is null ? null : Categories.FirstOrDefault(c => c.Id == id);

    public FinanceTransaction? Find(Guid id) => Transactions.FirstOrDefault(t => t.Id == id);

    /// <summary>Categorias em ordem de exibição: cada mãe seguida das filhas.</summary>
    public IReadOnlyList<TransactionCategory> OrderedCategories(CategoryKind? kind = null, bool includeArchived = false)
    {
        var all = Categories.Where(c => (kind is null || c.Kind == kind) && (includeArchived || !c.IsArchived)).ToList();
        var result = new List<TransactionCategory>();
        foreach (var parent in all.Where(c => c.ParentId is null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            result.Add(parent);
            result.AddRange(all.Where(c => c.ParentId == parent.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        return result;
    }

    public string CategoryPath(Guid? id)
    {
        if (Category(id) is not { } c)
        {
            return "Sem categoria";
        }
        return Category(c.ParentId) is { } parent ? $"{parent.Name} › {c.Name}" : c.Name;
    }

    // ---------- Saldos e totais ----------

    /// <summary>
    /// Saldo da conta ao fim do dia <paramref name="asOf"/>: abertura + partidas até a data. Pendentes só
    /// entram com <paramref name="includePending"/> (saldo previsto).
    /// </summary>
    public long Balance(Guid accountId, DateOnly asOf, bool includePending = false)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == accountId) ?? throw new FinanceValidationException("Conta não encontrada.");
            return BalanceCore(account, asOf, includePending);
        }
    }

    private long BalanceCore(FinancialAccount account, DateOnly asOf, bool includePending)
    {
        if (asOf < account.OpeningDate)
        {
            return 0;
        }
        var total = account.OpeningBalance;
        foreach (var t in _transactions)
        {
            if (t.Date > asOf || (!includePending && t.Status == TransactionStatus.Pending))
            {
                continue;
            }
            foreach (var p in t.Postings)
            {
                if (p.AccountId == account.Id)
                {
                    total = checked(total + p.Amount);
                }
            }
        }
        return total;
    }

    /// <summary>Saldo atual (confirmado até hoje) de cada conta.</summary>
    public IReadOnlyDictionary<Guid, long> CurrentBalances()
    {
        var today = Today;
        lock (_gate)
        {
            return _accounts.ToDictionary(a => a.Id, a => BalanceCore(a, today, includePending: false));
        }
    }

    /// <summary>
    /// Saldo em contas: soma dos saldos confirmados até hoje das contas ativas na moeda base que entram no
    /// disponível (por padrão, todas menos investimentos).
    /// </summary>
    public long AccountsBalance()
    {
        var today = Today;
        lock (_gate)
        {
            return Money.Sum(_accounts.Where(a => !a.IsArchived && a.IncludeInAvailable && a.Currency == BaseCurrency)
                .Select(a => BalanceCore(a, today, includePending: false)));
        }
    }

    /// <summary>
    /// Patrimônio financeiro: soma de todas as contas ativas na moeda base (confirmados até hoje) menos a dívida
    /// dos cartões, incluindo parcelas futuras de compras já feitas.
    /// </summary>
    public long NetWorth() => NetWorthAt(Today);

    private long NetWorthAt(DateOnly date)
    {
        lock (_gate)
        {
            var accounts = Money.Sum(_accounts.Where(a => !a.IsArchived && a.Currency == BaseCurrency).Select(a => BalanceCore(a, date, includePending: false)));
            var cards = _cards.Where(c => c.Currency == BaseCurrency).Select(c => c.Id).ToHashSet();
            var purchaseDates = _purchases.ToDictionary(p => p.Id, p => p.Date);
            long cardBalance = 0;
            foreach (var t in _transactions)
            {
                // Parcelas contam desde a data da compra (dívida assumida); o resto, pela própria data.
                var effective = t.PurchaseId is { } pid && purchaseDates.TryGetValue(pid, out var d) ? d : t.Date;
                if (effective > date)
                {
                    continue;
                }
                foreach (var p in t.Postings)
                {
                    if (p.CardId is { } card && cards.Contains(card))
                    {
                        cardBalance = checked(cardBalance + p.Amount);
                    }
                }
            }
            return checked(accounts + cardBalance);
        }
    }

    /// <summary>Moeda do lançamento: a da conta ou a do cartão.</summary>
    private string? CurrencyOf(FinanceTransaction t)
    {
        if (t.CardId is { } cardId)
        {
            return _cards.FirstOrDefault(c => c.Id == cardId)?.Currency;
        }
        return _accounts.FirstOrDefault(a => a.Id == t.AccountId)?.Currency;
    }

    /// <summary>
    /// Receitas e despesas do período (datas inclusivas), na moeda base. Estornos reduzem a despesa;
    /// transferências, pagamentos de fatura e ajustes não entram. Compras no cartão contam na data de cada
    /// parcela (competência), nunca no pagamento da fatura.
    /// </summary>
    public PeriodTotals Totals(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            long incomeCleared = 0, incomePending = 0, expenseCleared = 0, expensePending = 0;
            foreach (var t in _transactions)
            {
                if (t.Date < from || t.Date > to || CurrencyOf(t) != BaseCurrency)
                {
                    continue;
                }
                var cleared = t.Status == TransactionStatus.Cleared;
                switch (t.Kind)
                {
                    case TransactionKind.Income:
                        if (cleared) { incomeCleared = checked(incomeCleared + t.Amount); } else { incomePending = checked(incomePending + t.Amount); }
                        break;
                    case TransactionKind.Expense:
                        if (cleared) { expenseCleared = checked(expenseCleared + t.Amount); } else { expensePending = checked(expensePending + t.Amount); }
                        break;
                    case TransactionKind.Refund:
                        if (cleared) { expenseCleared = checked(expenseCleared - t.Amount); } else { expensePending = checked(expensePending - t.Amount); }
                        break;
                }
            }
            return new PeriodTotals(incomeCleared, incomePending, expenseCleared, expensePending);
        }
    }

    public static (DateOnly From, DateOnly To) MonthRange(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    /// <summary>Lançamentos filtrados, do mais recente para o mais antigo.</summary>
    public IReadOnlyList<FinanceTransaction> Query(TransactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        HashSet<Guid>? categories = null;
        if (filter.CategoryId is { } cat)
        {
            categories = [cat, .. Categories.Where(c => c.ParentId == cat).Select(c => c.Id)];
        }
        var terms = string.IsNullOrWhiteSpace(filter.Text) ? [] : Fold(filter.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. Transactions.Where(t =>
                (filter.From is null || t.Date >= filter.From) &&
                (filter.To is null || t.Date <= filter.To) &&
                (filter.AccountId is null || t.Touches(filter.AccountId.Value)) &&
                (categories is null || (t.CategoryId is { } c && categories.Contains(c))) &&
                (filter.Kind is null || t.Kind == filter.Kind) &&
                (filter.Status is null || t.Status == filter.Status) &&
                (filter.MinAmount is null || t.Amount >= filter.MinAmount) &&
                (filter.MaxAmount is null || t.Amount <= filter.MaxAmount) &&
                (terms.Length == 0 || terms.All(term => SearchText(t).Contains(term, StringComparison.Ordinal))))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt)];
    }

    private string SearchText(FinanceTransaction t) =>
        Fold($"{t.Description} {t.Notes} {string.Join(' ', t.Tags)} {CategoryPath(t.CategoryId)}");

    /// <summary>Minúsculas e sem acentos, para busca ("Café" encontra "cafe").</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // ---------- Contas ----------

    public FinancialAccount NewAccount() => new()
    {
        Id = Guid.CreateVersion7(Now),
        Name = "",
        OpeningDate = Today,
        Currency = BaseCurrency,
        SortOrder = Accounts.Count,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public string? Validate(FinancialAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Name))
        {
            return "Dê um nome à conta.";
        }
        if (account.Name.Trim().Length > MaxNameLength || (account.Institution?.Trim().Length ?? 0) > MaxNameLength)
        {
            return $"Nome e instituição têm no máximo {MaxNameLength} caracteres.";
        }
        if (!Money.IsKnownCurrency(account.Currency))
        {
            return "Moeda não suportada.";
        }
        if (Math.Abs(account.OpeningBalance) > Money.MaxAbsMinor)
        {
            return "Saldo inicial fora do limite.";
        }
        var existing = Account(account.Id);
        var used = Transactions.Where(t => t.Touches(account.Id)).ToList();
        if (used.Count > 0 && used.Min(t => t.Date) < account.OpeningDate)
        {
            return $"Há lançamentos antes de {used.Min(t => t.Date).ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"))}; a data do saldo inicial não pode ser posterior a eles.";
        }
        if (existing is not null && existing.Currency != account.Currency && used.Count > 0)
        {
            return "A moeda de uma conta com lançamentos não pode mudar.";
        }
        return null;
    }

    public FinancialAccount SaveAccount(FinancialAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (Validate(account) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var value = account with
        {
            Name = account.Name.Trim(),
            Institution = string.IsNullOrWhiteSpace(account.Institution) ? null : account.Institution.Trim(),
            UpdatedAt = Now,
        };
        _store.SaveAccount(value);
        lock (_gate)
        {
            _accounts.RemoveAll(a => a.Id == value.Id);
            _accounts.Add(value);
        }
        Changed();
        return value;
    }

    public void SetArchived(Guid accountId, bool archived)
    {
        var account = Account(accountId) ?? throw new FinanceValidationException("Conta não encontrada.");
        SaveAccount(account with { ArchivedAt = archived ? Now : null });
    }

    /// <summary>Só contas sem lançamentos podem ser excluídas; as demais são arquivadas (o histórico fica).</summary>
    public bool CanDeleteAccount(Guid id) => !Transactions.Any(t => t.Touches(id)) && !Recurring.Any(r => r.AccountId == id)
        && !Goals.Any(g => g.AccountId == id) && !AllContributions().Any(c => c.AccountId == id);

    public void DeleteAccount(Guid id)
    {
        if (!CanDeleteAccount(id))
        {
            throw new FinanceValidationException("A conta tem lançamentos, recorrências ou metas. Arquive-a para escondê-la sem perder o histórico.");
        }
        _store.DeleteAccount(id);
        lock (_gate)
        {
            _accounts.RemoveAll(a => a.Id == id);
        }
        Changed();
    }

    // ---------- Categorias ----------

    public TransactionCategory NewCategory(CategoryKind kind, Guid? parentId = null) => new()
    {
        Id = Guid.CreateVersion7(Now),
        Name = "",
        Kind = kind,
        ParentId = parentId,
        Color = Category(parentId)?.Color ?? "#8A8A8A",
        SortOrder = Categories.Count,
    };

    public string? Validate(TransactionCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var existing = Category(category.Id);
        if (existing?.Kind == CategoryKind.System || category.Kind == CategoryKind.System)
        {
            return "Categorias do sistema não podem ser alteradas.";
        }
        if (string.IsNullOrWhiteSpace(category.Name))
        {
            return "Dê um nome à categoria.";
        }
        if (category.Name.Trim().Length > MaxNameLength)
        {
            return $"O nome tem no máximo {MaxNameLength} caracteres.";
        }
        if (category.ParentId is { } parentId)
        {
            if (parentId == category.Id)
            {
                return "Uma categoria não pode ser subcategoria de si mesma.";
            }
            if (Category(parentId) is not { } parent)
            {
                return "Categoria principal não encontrada.";
            }
            if (parent.ParentId is not null)
            {
                return "Subcategorias têm um nível só (não há subcategoria de subcategoria).";
            }
            if (parent.Kind != category.Kind)
            {
                return "A subcategoria precisa ser do mesmo tipo (receita ou despesa) da principal.";
            }
            if (Categories.Any(c => c.ParentId == category.Id))
            {
                return "Esta categoria tem subcategorias; ela não pode virar subcategoria.";
            }
        }
        if (existing is not null && existing.Kind != category.Kind && Transactions.Any(t => t.CategoryId == category.Id))
        {
            return "O tipo de uma categoria já usada não pode mudar.";
        }
        var siblings = Categories.Where(c => c.Id != category.Id && c.ParentId == category.ParentId && c.Kind == category.Kind);
        if (siblings.Any(c => string.Equals(Fold(c.Name.Trim()), Fold(category.Name.Trim()), StringComparison.Ordinal)))
        {
            return "Já existe uma categoria com esse nome aqui.";
        }
        return null;
    }

    public TransactionCategory SaveCategory(TransactionCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (Validate(category) is { } error)
        {
            throw new FinanceValidationException(error);
        }
        var value = category with { Name = category.Name.Trim() };
        _store.SaveCategory(value);
        lock (_gate)
        {
            _categories.RemoveAll(c => c.Id == value.Id);
            _categories.Add(value);
        }
        Changed();
        return value;
    }

    public void SetArchived(TransactionCategory category, bool archived)
    {
        ArgumentNullException.ThrowIfNull(category);
        SaveCategory(category with { ArchivedAt = archived ? Now : null });
        if (archived)
        {
            foreach (var child in Categories.Where(c => c.ParentId == category.Id && !c.IsArchived).ToList())
            {
                SaveCategory(child with { ArchivedAt = Now });
            }
        }
    }

    /// <summary>Excluir só categorias sem uso e sem subcategorias; as demais são arquivadas.</summary>
    public bool CanDeleteCategory(Guid id) =>
        Category(id) is { Kind: not CategoryKind.System } && !Categories.Any(c => c.ParentId == id) && !Transactions.Any(t => t.CategoryId == id)
        && !Recurring.Any(r => r.CategoryId == id);

    public void DeleteCategory(Guid id)
    {
        if (!CanDeleteCategory(id))
        {
            throw new FinanceValidationException("A categoria está em uso ou tem subcategorias. Arquive-a para escondê-la.");
        }
        _store.DeleteCategory(id);
        lock (_gate)
        {
            _categories.RemoveAll(c => c.Id == id);
        }
        Changed();
    }

    private void EnsureDefaultCategories()
    {
        if (Category(AdjustmentCategoryId) is null)
        {
            var system = new TransactionCategory { Id = AdjustmentCategoryId, Name = "Ajuste de saldo", Kind = CategoryKind.System, Color = "#8A8A8A", SortOrder = 9999 };
            _store.SaveCategory(system);
            lock (_gate)
            {
                _categories.Add(system);
            }
        }
        if (Categories.Any(c => c.Kind != CategoryKind.System))
        {
            return;
        }
        var order = 0;
        void Add(CategoryKind kind, string name, string color, params string[] children)
        {
            var parent = new TransactionCategory { Id = Guid.CreateVersion7(Now), Name = name, Kind = kind, Color = color, SortOrder = order++ };
            _store.SaveCategory(parent);
            lock (_gate)
            {
                _categories.Add(parent);
            }
            foreach (var child in children)
            {
                var sub = new TransactionCategory { Id = Guid.CreateVersion7(Now), Name = child, Kind = kind, Color = color, ParentId = parent.Id, SortOrder = order++ };
                _store.SaveCategory(sub);
                lock (_gate)
                {
                    _categories.Add(sub);
                }
            }
        }
        Add(CategoryKind.Expense, "Alimentação", "#C9A94E", "Mercado", "Restaurantes", "Delivery");
        Add(CategoryKind.Expense, "Transporte", "#6B9AC4", "Combustível", "Aplicativos e táxi", "Transporte público");
        Add(CategoryKind.Expense, "Moradia", "#8FB573", "Aluguel", "Energia", "Água", "Internet");
        Add(CategoryKind.Expense, "Saúde", "#C47A6B", "Farmácia", "Consultas");
        Add(CategoryKind.Expense, "Educação", "#5FB3A8");
        Add(CategoryKind.Expense, "Lazer", "#C48FB0");
        Add(CategoryKind.Expense, "Jogos", "#9A86C4");
        Add(CategoryKind.Expense, "Tecnologia", "#7FA3B8");
        Add(CategoryKind.Expense, "Assinaturas", "#B89A6B");
        Add(CategoryKind.Expense, "Compras", "#A88AC4");
        Add(CategoryKind.Expense, "Outros", "#8A8A8A");
        Add(CategoryKind.Income, "Salário", "#8FB573");
        Add(CategoryKind.Income, "Trabalhos extras", "#5FB3A8");
        Add(CategoryKind.Income, "Rendimentos", "#6B9AC4");
        Add(CategoryKind.Income, "Outras receitas", "#8A8A8A");
    }

    // ---------- Lançamentos ----------

    public TransactionDraft NewDraft(TransactionKind kind = TransactionKind.Expense) => new()
    {
        Kind = kind,
        Date = Today,
        AccountId = Accounts.FirstOrDefault(a => !a.IsArchived)?.Id ?? Guid.Empty,
    };

    public static TransactionDraft DraftFrom(FinanceTransaction t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new()
        {
            Id = t.Id,
            Kind = t.Kind,
            Date = t.Date,
            Description = t.Description,
            Amount = t.Kind == TransactionKind.Adjustment ? t.Postings[0].Amount : t.Amount,
            AccountId = t.AccountId,
            ToAccountId = t.ToAccountId,
            CardId = t.CardId,
            StatementClosing = t.IsCardPayment ? t.StatementClosing : null,
            CategoryId = t.CategoryId,
            Status = t.Status,
            PaymentMethod = t.PaymentMethod,
            Notes = t.Notes,
            Tags = t.Tags,
            RefundOfId = t.RefundOfId,
            RecurringId = t.RecurringId,
            OccurrenceDate = t.OccurrenceDate,
        };
    }

    /// <summary>Cópia para um novo lançamento com a data de hoje (só é gravada ao salvar).</summary>
    public TransactionDraft Duplicate(Guid id)
    {
        var t = Find(id) ?? throw new FinanceValidationException("Lançamento não encontrado.");
        if (t.PurchaseId is not null)
        {
            throw new FinanceValidationException("Parcelas de cartão são duplicadas pela compra.");
        }
        return DraftFrom(t) with { Id = null, Date = Today, RefundOfId = null, StatementClosing = null, RecurringId = null, OccurrenceDate = null };
    }

    /// <summary>Monta o lançamento (partidas com sinal) ou devolve o erro de validação.</summary>
    public FinanceTransaction? TryBuild(TransactionDraft draft, out string? error)
    {
        ArgumentNullException.ThrowIfNull(draft);
        error = Check(draft);
        if (error is not null)
        {
            return null;
        }
        var existing = draft.Id is { } id ? Find(id) : null;
        var amount = draft.Amount;
        IReadOnlyList<Posting> postings = draft.Kind switch
        {
            // Estorno no cartão: reduz a dívida na fatura do dia do estorno.
            TransactionKind.Refund when draft.CardId is { } refundCard => [Posting.ForCard(refundCard, amount, StatementCalendar.ClosingFor(Card(refundCard)!, draft.Date))],
            // Pagamento de fatura: sai da conta, abate a dívida do cartão na fatura escolhida.
            TransactionKind.Transfer when draft.CardId is { } payCard =>
                [new(draft.AccountId, -amount), Posting.ForCard(payCard, amount, draft.StatementClosing ?? StatementCalendar.ClosingFor(Card(payCard)!, draft.Date))],
            TransactionKind.Income or TransactionKind.Refund => [new(draft.AccountId, amount)],
            TransactionKind.Expense => [new(draft.AccountId, -amount)],
            TransactionKind.Adjustment => [new(draft.AccountId, amount)],
            TransactionKind.Transfer => [new(draft.AccountId, -amount), new(draft.ToAccountId!.Value, amount)],
            _ => throw new FinanceValidationException("Tipo de lançamento desconhecido."),
        };
        var category = draft.Kind switch
        {
            TransactionKind.Transfer => null,
            TransactionKind.Adjustment => AdjustmentCategoryId,
            TransactionKind.Refund when draft.CategoryId is null && draft.RefundOfId is { } of => Find(of)?.CategoryId,
            _ => draft.CategoryId,
        };
        if (draft.Kind == TransactionKind.Transfer && draft.CardId is not null)
        {
            category = null;
        }
        return new FinanceTransaction
        {
            Id = existing?.Id ?? draft.Id ?? Guid.CreateVersion7(Now),
            Kind = draft.Kind,
            Date = draft.Date,
            Description = draft.Description.Trim(),
            CategoryId = category,
            Status = draft.Status,
            PaymentMethod = draft.Kind is TransactionKind.Transfer or TransactionKind.Adjustment ? PaymentMethod.None : draft.PaymentMethod,
            Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
            Tags = NormalizeTags(draft.Tags),
            RefundOfId = draft.Kind == TransactionKind.Refund ? draft.RefundOfId : null,
            RecurringId = draft.Kind is TransactionKind.Income or TransactionKind.Expense ? draft.RecurringId : null,
            OccurrenceDate = draft.Kind is TransactionKind.Income or TransactionKind.Expense ? draft.OccurrenceDate : null,
            Postings = postings,
            CreatedAt = existing?.CreatedAt ?? Now,
            UpdatedAt = Now,
        };
    }

    public string? Validate(TransactionDraft draft) => Check(draft);

    private string? Check(TransactionDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Description))
        {
            return "Escreva uma descrição.";
        }
        if (d.Description.Trim().Length > MaxDescriptionLength)
        {
            return $"A descrição tem no máximo {MaxDescriptionLength} caracteres.";
        }
        if ((d.Notes?.Length ?? 0) > MaxNotesLength)
        {
            return $"As observações têm no máximo {MaxNotesLength} caracteres.";
        }
        if (d.Tags.Count(t => !string.IsNullOrWhiteSpace(t)) > MaxTags || d.Tags.Any(t => t.Trim().Length > MaxTagLength))
        {
            return $"Use até {MaxTags} etiquetas de até {MaxTagLength} caracteres.";
        }
        if (d.Kind == TransactionKind.Adjustment ? d.Amount == 0 : d.Amount <= 0)
        {
            return d.Kind == TransactionKind.Adjustment ? "O ajuste não muda o saldo (diferença zero)." : "Informe um valor maior que zero.";
        }
        if (Math.Abs(d.Amount) > Money.MaxAbsMinor)
        {
            return "Valor fora do limite.";
        }
        var existing = d.Id is { } id ? Find(id) : null;
        if (d.Id is not null && existing is null)
        {
            return "Lançamento não encontrado (talvez tenha sido excluído).";
        }
        if (existing is not null && existing.Kind != d.Kind && (existing.Kind == TransactionKind.Transfer || d.Kind == TransactionKind.Transfer))
        {
            return "Uma transferência não pode virar outro tipo de lançamento (exclua e crie de novo).";
        }
        if (existing?.PurchaseId is not null)
        {
            return "Esta é uma parcela de compra no cartão: edite a compra.";
        }
        if (existing is not null && ContributionOf(existing.Id) is { } contribution)
        {
            return $"Esta transferência é uma contribuição para a meta {Goal(contribution.GoalId)?.Name}: altere pela meta.";
        }
        if (d.RecurringId is { } recurringId)
        {
            if (RecurringItem(recurringId) is not { } recurring)
            {
                return "Recorrência não encontrada.";
            }
            if (d.OccurrenceDate is not { } occurrence)
            {
                return "Falta a data da ocorrência.";
            }
            if (d.Kind != (recurring.IsIncome ? TransactionKind.Income : TransactionKind.Expense))
            {
                return recurring.IsIncome ? "A ocorrência de uma receita recorrente é uma receita." : "A ocorrência de uma conta recorrente é uma despesa.";
            }
            if (Transactions.Any(t => t.RecurringId == recurringId && t.OccurrenceDate == occurrence && t.Id != d.Id))
            {
                return "Essa ocorrência já foi confirmada.";
            }
        }
        if (existing is not null && (existing.CardId is null) != (d.CardId is null))
        {
            return "Lançamentos de cartão e de conta não podem ser trocados entre si (exclua e crie de novo).";
        }
        if (d.CardId is { } cardId)
        {
            if (d.Kind is not (TransactionKind.Refund or TransactionKind.Transfer))
            {
                return "Compras no cartão são registradas em \"Compra no cartão\".";
            }
            if (Card(cardId) is not { } card)
            {
                return "Cartão não encontrado.";
            }
            if (card.IsArchived && existing?.CardId != cardId)
            {
                return $"O cartão {card.Name} está arquivado.";
            }
            if (d.Kind == TransactionKind.Transfer)
            {
                if (AccountError(d.AccountId, d.Date, existing) is { } payError)
                {
                    return payError;
                }
                if (Account(d.AccountId)!.Currency != card.Currency)
                {
                    return "A conta precisa ser da mesma moeda do cartão.";
                }
                if (d.StatementClosing is { } closing && StatementCalendar.ClosingFor(card, closing.AddDays(card.ClosingDayGoesNext ? -1 : 0)) != closing)
                {
                    return "Fatura inválida para este cartão.";
                }
            }
        }
        else if (AccountError(d.AccountId, d.Date, existing) is { } accountError)
        {
            return accountError;
        }
        if (d.Kind == TransactionKind.Transfer && d.CardId is null)
        {
            if (d.ToAccountId is not { } to)
            {
                return "Escolha a conta de destino.";
            }
            if (to == d.AccountId)
            {
                return "Origem e destino precisam ser contas diferentes.";
            }
            if (AccountError(to, d.Date, existing) is { } toError)
            {
                return toError;
            }
            if (Account(to)!.Currency != Account(d.AccountId)!.Currency)
            {
                return "Transferência entre moedas diferentes não é suportada (sem conversão de câmbio).";
            }
        }

        if (d.Kind is TransactionKind.Income or TransactionKind.Expense or TransactionKind.Refund && d.CategoryId is { } categoryId)
        {
            if (Category(categoryId) is not { } category)
            {
                return "Categoria não encontrada.";
            }
            var expected = d.Kind == TransactionKind.Income ? CategoryKind.Income : CategoryKind.Expense;
            if (category.Kind != expected)
            {
                return d.Kind == TransactionKind.Income ? "Escolha uma categoria de receita." : "Escolha uma categoria de despesa.";
            }
            if (category.IsArchived && existing?.CategoryId != categoryId)
            {
                return "Essa categoria está arquivada.";
            }
        }
        if (d.Kind == TransactionKind.Adjustment && string.IsNullOrWhiteSpace(d.Notes))
        {
            return "Explique o ajuste nas observações (ex.: \"conferido com o extrato\").";
        }
        if (d.Kind == TransactionKind.Refund && d.RefundOfId is { } refundOf)
        {
            if (Find(refundOf) is not { Kind: TransactionKind.Expense } original)
            {
                return "O estorno precisa apontar para uma despesa.";
            }
            if (original.CardId != d.CardId)
            {
                return original.CardId is null ? "Essa despesa foi paga por conta, não no cartão." : "Estorno de compra no cartão precisa ser lançado no mesmo cartão.";
            }
            // Compra parcelada: o limite é o total da compra, somando estornos ligados a qualquer parcela.
            var related = original.PurchaseId is { } pid ? Transactions.Where(t => t.PurchaseId == pid).Select(t => t.Id).ToHashSet() : [refundOf];
            var limit = original.PurchaseId is { } purchaseId && Purchase(purchaseId) is { } purchase ? purchase.Total : original.Amount;
            var alreadyRefunded = Money.Sum(Transactions.Where(t => t.RefundOfId is { } r && related.Contains(r) && t.Id != d.Id).Select(t => t.Amount));
            if (alreadyRefunded + d.Amount > limit)
            {
                return $"O estorno passa do valor da despesa (restam {Money.Format(limit - alreadyRefunded, CurrencyOf(original) ?? BaseCurrency)}).";
            }
        }
        return null;
    }

    private string? AccountError(Guid accountId, DateOnly date, FinanceTransaction? existing)
    {
        if (Account(accountId) is not { } account)
        {
            return Accounts.Count == 0 ? "Cadastre uma conta primeiro." : "Escolha a conta.";
        }
        if (account.IsArchived && existing?.Touches(accountId) != true)
        {
            return $"A conta {account.Name} está arquivada.";
        }
        if (date < account.OpeningDate)
        {
            return $"A data é anterior ao saldo inicial de {account.Name} ({account.OpeningDate.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"))}).";
        }
        return null;
    }

    private static string[] NormalizeTags(IEnumerable<string> tags) =>
        [.. tags.Select(t => t.Trim().Replace(",", " ", StringComparison.Ordinal)).Where(t => t.Length > 0)
            .DistinctBy(t => Fold(t)).Take(MaxTags)];

    public FinanceTransaction Save(TransactionDraft draft)
    {
        var value = TryBuild(draft, out var error) ?? throw new FinanceValidationException(error ?? "Lançamento inválido.");
        _store.SaveTransaction(value);
        lock (_gate)
        {
            _transactions.RemoveAll(t => t.Id == value.Id);
            _transactions.Add(value);
        }
        Changed();
        return value;
    }

    /// <summary>
    /// Ajuste para o saldo da conta (confirmado, ao fim de <paramref name="date"/>) ficar igual a
    /// <paramref name="target"/>. Devolve null se já está igual.
    /// </summary>
    public FinanceTransaction? AdjustBalanceTo(Guid accountId, DateOnly date, long target, string notes)
    {
        var current = Balance(accountId, date);
        var diff = checked(target - current);
        if (diff == 0)
        {
            return null;
        }
        return Save(new TransactionDraft
        {
            Kind = TransactionKind.Adjustment,
            Date = date,
            Description = "Ajuste de saldo",
            Amount = diff,
            AccountId = accountId,
            Notes = notes,
        });
    }

    /// <summary>
    /// Exclui e devolve o lançamento (para "desfazer"). Estornos que apontavam para ele ficam soltos
    /// (o banco faz o mesmo com <c>ON DELETE SET NULL</c>); a lista deles volta junto para religar.
    /// </summary>
    public DeletedTransaction Delete(Guid id)
    {
        var value = Find(id) ?? throw new FinanceValidationException("Lançamento não encontrado.");
        if (value.PurchaseId is not null)
        {
            throw new FinanceValidationException("Esta é uma parcela de compra no cartão: exclua a compra (ou registre um estorno).");
        }
        if (ContributionOf(id) is { } contribution)
        {
            throw new FinanceValidationException($"Esta transferência é uma contribuição para a meta {Goal(contribution.GoalId)?.Name}: exclua pela meta.");
        }
        _store.DeleteTransaction(id, Now);
        var unlinked = new List<Guid>();
        lock (_gate)
        {
            _transactions.RemoveAll(t => t.Id == id);
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].RefundOfId == id)
                {
                    unlinked.Add(_transactions[i].Id);
                    _transactions[i] = _transactions[i] with { RefundOfId = null };
                }
            }
        }
        Changed();
        return new DeletedTransaction(value, unlinked);
    }

    /// <summary>Desfaz uma exclusão recente: grava o lançamento como era e religa os estornos que apontavam para ele.</summary>
    public void Restore(DeletedTransaction deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        var value = deleted.Transaction;
        if (value.Postings.Any(p => p.CardId is { } c ? Card(c) is null : Account(p.AccountId) is null))
        {
            throw new FinanceValidationException("A conta deste lançamento não existe mais.");
        }
        var restored = value.RefundOfId is { } of && Find(of) is null ? value with { RefundOfId = null } : value;
        if (restored.RecurringId is { } rid && (RecurringItem(rid) is null || Transactions.Any(t => t.RecurringId == rid && t.OccurrenceDate == restored.OccurrenceDate && t.Id != restored.Id)))
        {
            restored = restored with { RecurringId = null, OccurrenceDate = null };
        }
        var relinked = deleted.UnlinkedRefunds.Select(Find).OfType<FinanceTransaction>().Where(r => r.RefundOfId is null)
            .Select(r => r with { RefundOfId = restored.Id }).ToList();
        _store.SaveTransaction(restored);
        relinked.ForEach(_store.SaveTransaction);
        lock (_gate)
        {
            _transactions.RemoveAll(t => t.Id == restored.Id || relinked.Any(r => r.Id == t.Id));
            _transactions.Add(restored);
            _transactions.AddRange(relinked);
        }
        Changed();
    }

    private void Changed() => _bus.Publish(new FinanceChanged());

    [LoggerMessage(Level = LogLevel.Information, Message = "Finanças carregadas: {Accounts} contas, {Categories} categorias, {Transactions} lançamentos")]
    private partial void LogLoaded(int accounts, int categories, int transactions);
}
