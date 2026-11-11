using DesktopHub.Core.Events;

namespace DesktopHub.Core.Finance;

public enum AccountKind
{
    Checking,
    Savings,
    DigitalWallet,
    Cash,
    Investment,
    Other,
}

/// <summary>
/// Conta ou carteira. O saldo nunca é armazenado: é o saldo de abertura (no início de
/// <see cref="OpeningDate"/>) mais a soma das partidas da conta.
/// </summary>
public sealed record FinancialAccount
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public AccountKind Kind { get; init; } = AccountKind.Checking;
    public string? Institution { get; init; }
    public string Currency { get; init; } = Money.DefaultCurrency;
    public long OpeningBalance { get; init; }
    public required DateOnly OpeningDate { get; init; }
    public string Color { get; init; } = "#9A86C4";
    /// <summary>Entra no "saldo disponível" (investimentos, por padrão, não).</summary>
    public bool IncludeInAvailable { get; init; } = true;
    public DateTimeOffset? ArchivedAt { get; init; }
    public int SortOrder { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsArchived => ArchivedAt is not null;
}

public enum CategoryKind
{
    Expense,
    Income,
    /// <summary>Categorias do próprio app (ex.: ajuste de saldo), fora de receitas e despesas.</summary>
    System,
}

/// <summary>Categoria com no máximo um nível de subcategoria.</summary>
public sealed record TransactionCategory
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public CategoryKind Kind { get; init; } = CategoryKind.Expense;
    public Guid? ParentId { get; init; }
    public string Color { get; init; } = "#8A8A8A";
    public DateTimeOffset? ArchivedAt { get; init; }
    public int SortOrder { get; init; }

    public bool IsArchived => ArchivedAt is not null;
}

public enum TransactionKind
{
    Income,
    Expense,
    Transfer,
    /// <summary>Estorno/reembolso: entra na conta e reduz a despesa da categoria.</summary>
    Refund,
    /// <summary>Correção de saldo: não é receita nem despesa.</summary>
    Adjustment,
}

public enum TransactionStatus
{
    /// <summary>Lançado, ainda não compensado (ex.: boleto agendado). Entra no previsto, não no saldo atual.</summary>
    Pending,
    Cleared,
}

public enum PaymentMethod
{
    None,
    Pix,
    Debit,
    Cash,
    BankSlip,
    BankTransfer,
    Credit,
    Other,
}

/// <summary>
/// Efeito de uma transação numa conta ou num cartão. Sinal do ponto de vista do patrimônio: + entra, − sai
/// (no cartão, − é dívida a mais). Partida de cartão: <see cref="AccountId"/> vazio, <see cref="CardId"/> e a
/// fatura (<see cref="StatementClosing"/>) preenchidos.
/// </summary>
public sealed record Posting(Guid AccountId, long Amount)
{
    public Guid? CardId { get; init; }

    /// <summary>Data de fechamento da fatura a que a partida de cartão pertence (gravada; não muda com o ciclo).</summary>
    public DateOnly? StatementClosing { get; init; }

    public bool IsCard => CardId is not null;

    public static Posting ForCard(Guid cardId, long amount, DateOnly statementClosing) =>
        new(Guid.Empty, amount) { CardId = cardId, StatementClosing = statementClosing };
}

/// <summary>Um fato financeiro com 1 partida (receita, despesa, estorno, ajuste) ou 2 (transferência, soma zero).</summary>
public sealed record FinanceTransaction
{
    public required Guid Id { get; init; }
    public required TransactionKind Kind { get; init; }
    public required DateOnly Date { get; init; }
    public required string Description { get; init; }
    public Guid? CategoryId { get; init; }
    public TransactionStatus Status { get; init; } = TransactionStatus.Cleared;
    public PaymentMethod PaymentMethod { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Guid? RefundOfId { get; init; }
    /// <summary>Parcela de uma compra no cartão (a compra guarda total e número de parcelas).</summary>
    public Guid? PurchaseId { get; init; }
    public int? InstallmentNumber { get; init; }
    /// <summary>Ocorrência confirmada de uma recorrência (par único com <see cref="OccurrenceDate"/>).</summary>
    public Guid? RecurringId { get; init; }
    public DateOnly? OccurrenceDate { get; init; }
    /// <summary>Importado de um extrato (lote, identificador do banco e impressão digital da linha).</summary>
    public Guid? ImportBatchId { get; init; }
    public string? ExternalId { get; init; }
    public string? Fingerprint { get; init; }
    public required IReadOnlyList<Posting> Postings { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Valor do lançamento, sempre positivo (para transferências, o valor transferido).</summary>
    public long Amount => Postings.Count == 0 ? 0 : Postings.Max(p => Math.Abs(p.Amount));

    /// <summary>Conta de origem (saída) ou a única conta; vazio quando a única partida é de cartão.</summary>
    public Guid AccountId => Postings.Count == 2 ? Postings.First(p => p.Amount < 0).AccountId : Postings[0].AccountId;

    /// <summary>Conta de destino de uma transferência entre contas (não de pagamento de fatura).</summary>
    public Guid? ToAccountId => Postings.Count == 2 && Postings.First(p => p.Amount > 0) is { IsCard: false } to ? to.AccountId : null;

    /// <summary>Cartão envolvido (compra, estorno ou pagamento de fatura).</summary>
    public Guid? CardId => Postings.FirstOrDefault(p => p.IsCard)?.CardId;

    /// <summary>Fatura da partida de cartão.</summary>
    public DateOnly? StatementClosing => Postings.FirstOrDefault(p => p.IsCard)?.StatementClosing;

    /// <summary>Pagamento de fatura: transferência da conta para o cartão (não é despesa).</summary>
    public bool IsCardPayment => Kind == TransactionKind.Transfer && CardId is not null;

    public bool Touches(Guid id) => Postings.Any(p => p.AccountId == id || p.CardId == id);
}

/// <summary>
/// O que o usuário preenche. O serviço transforma em partidas com o sinal certo, de modo que a tela nunca
/// decide sinais. Para ajuste, <see cref="Amount"/> é com sinal (diferença a aplicar).
/// </summary>
public sealed record TransactionDraft
{
    public Guid? Id { get; init; }
    /// <summary>Estorno no cartão (com <see cref="TransactionKind.Refund"/>) ou cartão destino de um pagamento de fatura.</summary>
    public Guid? CardId { get; init; }
    /// <summary>Pagamento de fatura: qual fatura (data de fechamento).</summary>
    public DateOnly? StatementClosing { get; init; }
    public TransactionKind Kind { get; init; } = TransactionKind.Expense;
    public required DateOnly Date { get; init; }
    public string Description { get; init; } = "";
    public long Amount { get; init; }
    public Guid AccountId { get; init; }
    public Guid? ToAccountId { get; init; }
    public Guid? CategoryId { get; init; }
    public TransactionStatus Status { get; init; } = TransactionStatus.Cleared;
    public PaymentMethod PaymentMethod { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Guid? RefundOfId { get; init; }
    /// <summary>Confirmação de uma ocorrência de recorrência.</summary>
    public Guid? RecurringId { get; init; }
    public DateOnly? OccurrenceDate { get; init; }
}

/// <summary>Filtros da lista de lançamentos. Nulo = sem filtro.</summary>
public sealed record TransactionFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public Guid? AccountId { get; init; }
    /// <summary>Inclui as subcategorias da categoria escolhida.</summary>
    public Guid? CategoryId { get; init; }
    public TransactionKind? Kind { get; init; }
    public TransactionStatus? Status { get; init; }
    /// <summary>Faixa sobre o valor absoluto.</summary>
    public long? MinAmount { get; init; }
    public long? MaxAmount { get; init; }
    public string? Text { get; init; }
}

/// <summary>Totais de um período, só na moeda base (outras moedas não são convertidas).</summary>
public sealed record PeriodTotals(long IncomeCleared, long IncomePending, long ExpenseCleared, long ExpensePending)
{
    public long Income => Money.Add(IncomeCleared, IncomePending);
    public long Expense => Money.Add(ExpenseCleared, ExpensePending);
    public long Net => Income - Expense;
}

/// <summary>Lançamento excluído e os estornos que deixaram de apontar para ele (para "desfazer").</summary>
public sealed record DeletedTransaction(FinanceTransaction Transaction, IReadOnlyList<Guid> UnlinkedRefunds);

public sealed record FinanceSnapshot(
    IReadOnlyList<FinancialAccount> Accounts,
    IReadOnlyList<TransactionCategory> Categories,
    IReadOnlyList<FinanceTransaction> Transactions)
{
    public IReadOnlyList<CreditCard> Cards { get; init; } = [];
    public IReadOnlyList<CardPurchase> Purchases { get; init; } = [];
    public IReadOnlyList<StatementOverride> StatementOverrides { get; init; } = [];
    public IReadOnlyList<RecurringItem> Recurring { get; init; } = [];
    public IReadOnlyList<RecurringSkip> RecurringSkips { get; init; } = [];
    public IReadOnlyList<Budget> Budgets { get; init; } = [];
    public IReadOnlyList<FinancialGoal> Goals { get; init; } = [];
    public IReadOnlyList<GoalContribution> Contributions { get; init; } = [];
    public IReadOnlyList<SavedScenario> Scenarios { get; init; } = [];
    public IReadOnlyList<ImportBatch> ImportBatches { get; init; } = [];
    public IReadOnlyList<CategoryRule> Rules { get; init; } = [];
}

/// <summary>Persistência do módulo. Cada método é atômico (uma transação SQLite).</summary>
public interface IFinanceStore
{
    FinanceSnapshot Load();

    /// <summary>Cartão e suas regras de ciclo (substituídas por completo).</summary>
    void SaveCard(CreditCard card);

    void DeleteCard(Guid id);

    /// <summary>Compra e suas parcelas, juntas: as parcelas antigas da compra são substituídas.</summary>
    void SavePurchase(CardPurchase purchase, IReadOnlyList<FinanceTransaction> installments);

    /// <summary>Remove a compra e todas as parcelas.</summary>
    void DeletePurchase(Guid id, DateTimeOffset at);

    void SaveStatementOverride(StatementOverride value);

    /// <summary>Várias transações de uma vez (ex.: parcelas futuras remanejadas após mudar o ciclo do cartão).</summary>
    void SaveTransactions(IReadOnlyList<FinanceTransaction> transactions);

    void SaveAccount(FinancialAccount account);

    void DeleteAccount(Guid id);

    void SaveCategory(TransactionCategory category);

    void DeleteCategory(Guid id);

    void SaveTransaction(FinanceTransaction transaction);

    void DeleteTransaction(Guid id, DateTimeOffset at);

    void SaveRecurring(RecurringItem item);

    /// <summary>Remove o modelo; lançamentos já confirmados ficam (sem o vínculo).</summary>
    void DeleteRecurring(Guid id, DateTimeOffset at);

    void SetRecurringSkip(RecurringSkip skip, bool skipped);

    void SaveBudget(Budget budget);

    void DeleteBudget(Guid id);

    void SaveGoal(FinancialGoal goal);

    /// <summary>Remove a meta e as contribuições; transferências já feitas ficam como lançamentos comuns.</summary>
    void DeleteGoal(Guid id, DateTimeOffset at);

    /// <summary>Contribuição e, se houver, a transferência ligada a ela (juntas).</summary>
    void SaveContribution(GoalContribution contribution, FinanceTransaction? transfer);

    /// <summary>Remove a contribuição e a transferência ligada a ela, se houver.</summary>
    void DeleteContribution(Guid id, DateTimeOffset at);

    void SaveScenario(SavedScenario scenario);

    void DeleteScenario(Guid id);

    /// <summary>Lote e seus lançamentos, juntos.</summary>
    void SaveImport(ImportBatch batch, IReadOnlyList<FinanceTransaction> transactions);

    /// <summary>Marca o lote como desfeito e remove os lançamentos dele.</summary>
    void UndoImport(Guid batchId, DateTimeOffset at);

    void SaveRule(CategoryRule rule);

    void DeleteRule(Guid id);
}

/// <summary>Algo mudou no módulo de finanças (telas recalculam; nada é enviado para fora).</summary>
public sealed record FinanceChanged : IAppEvent;

public sealed class FinanceValidationException(string message) : Exception(message);
