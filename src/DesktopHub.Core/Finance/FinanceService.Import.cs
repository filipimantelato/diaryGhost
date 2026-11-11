using System.Security.Cryptography;
using System.Text;

namespace DesktopHub.Core.Finance;

/// <summary>
/// Importação de extratos (Fase 12): pré-visualização com duplicatas marcadas, importação em lote e "desfazer o lote".
/// Linhas já importadas (mesmo identificador do banco, ou mesma impressão digital) nunca entram de novo; parecidas com
/// lançamentos digitados (mesmo valor, até 2 dias) ficam desmarcadas para o usuário decidir.
/// </summary>
public sealed partial class FinanceService
{
    public static readonly int DuplicateWindowDays = 2;

    private List<ImportBatch> _batches = [];

    public IReadOnlyList<ImportBatch> ImportBatches
    {
        get
        {
            lock (_gate)
            {
                return [.. _batches.OrderByDescending(b => b.CreatedAt)];
            }
        }
    }

    /// <summary>Lote ainda ativo com o mesmo arquivo (mesmo SHA-256), para avisar "este arquivo já foi importado".</summary>
    public ImportBatch? PreviousImport(string sha256)
    {
        lock (_gate)
        {
            return _batches.Where(b => b.UndoneAt is null && b.Sha256 == sha256).MaxBy(b => b.CreatedAt);
        }
    }

    /// <summary>
    /// Impressão digital da linha: conta, data, valor, descrição normalizada e quantas vezes a mesma combinação já
    /// apareceu antes no arquivo (dois cafés iguais no mesmo dia continuam sendo dois).
    /// </summary>
    public static string Fingerprint(Guid accountId, ImportedRow row, int repeat)
    {
        ArgumentNullException.ThrowIfNull(row);
        var text = $"{accountId:N}|{row.Date:yyyy-MM-dd}|{row.Amount}|{DescriptionKey(row.Description)}|{repeat}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>Descrição normalizada para comparar: minúsculas, sem acentos e com espaços simples.</summary>
    private static string DescriptionKey(string text) => string.Join(' ', Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public IReadOnlyList<ImportCandidate> PreviewImport(Guid accountId, IReadOnlyList<ImportedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var account = Account(accountId) ?? throw new FinanceValidationException("Escolha a conta do extrato.");
        List<FinanceTransaction> existing;
        lock (_gate)
        {
            existing = [.. _transactions.Where(t => t.Postings.Any(p => p.AccountId == accountId))];
        }
        var externalIds = existing.Where(t => t.ExternalId is not null).Select(t => t.ExternalId!).ToHashSet(StringComparer.Ordinal);
        var fingerprints = existing.Where(t => t.Fingerprint is not null).ToDictionary(t => t.Fingerprint!, t => t.Id, StringComparer.Ordinal);
        // Lançamentos digitados (não importados) que ainda podem "casar" com uma linha do extrato.
        var manual = existing.Where(t => t.ImportBatchId is null).ToList();
        var used = new HashSet<Guid>();
        var repeats = new Dictionary<(DateOnly, long, string), int>();
        var categories = SuggestCategories();
        var result = new List<ImportCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var key = (row.Date, row.Amount, DescriptionKey(row.Description));
            var repeat = repeats.GetValueOrDefault(key);
            repeats[key] = repeat + 1;
            var fingerprint = Fingerprint(accountId, row, repeat);
            // Regra do usuário primeiro; depois, a categoria usada da última vez com a mesma descrição.
            var suggestion = MatchRule(row.Description, row.Amount > 0) ?? categories.GetValueOrDefault((DescriptionKey(row.Description), row.Amount > 0));
            if (row.Date < account.OpeningDate)
            {
                result.Add(new ImportCandidate(row, fingerprint, ImportRowStatus.BeforeOpening, null, suggestion));
                continue;
            }
            if ((row.ExternalId is { } ext && externalIds.Contains(ext)) || fingerprints.ContainsKey(fingerprint))
            {
                result.Add(new ImportCandidate(row, fingerprint, ImportRowStatus.AlreadyImported, fingerprints.GetValueOrDefault(fingerprint), suggestion));
                continue;
            }
            var match = manual.Where(t => !used.Contains(t.Id) && Math.Abs(t.Date.DayNumber - row.Date.DayNumber) <= DuplicateWindowDays
                    && t.Postings.Where(p => p.AccountId == accountId).Sum(p => p.Amount) == row.Amount)
                .OrderBy(t => Math.Abs(t.Date.DayNumber - row.Date.DayNumber)).FirstOrDefault();
            if (match is not null)
            {
                used.Add(match.Id);
                result.Add(new ImportCandidate(row, fingerprint, ImportRowStatus.PossibleDuplicate, match.Id, suggestion));
                continue;
            }
            result.Add(new ImportCandidate(row, fingerprint, ImportRowStatus.New, null, suggestion));
        }
        return result;
    }

    /// <summary>Categoria mais recente usada com a mesma descrição (e o mesmo sentido: entrada ou saída).</summary>
    private Dictionary<(string, bool), Guid> SuggestCategories()
    {
        var result = new Dictionary<(string, bool), Guid>();
        foreach (var t in Transactions.Where(t => t.CategoryId is not null && t.Kind is TransactionKind.Income or TransactionKind.Expense).OrderBy(t => t.Date))
        {
            var category = Category(t.CategoryId);
            if (category is null || category.IsArchived)
            {
                continue;
            }
            result[(DescriptionKey(t.Description), t.Kind == TransactionKind.Income)] = t.CategoryId!.Value;
        }
        return result;
    }

    /// <summary>
    /// Importa as linhas escolhidas como lançamentos confirmados (entrada = receita, saída = despesa) num lote.
    /// Linhas já importadas ou antes do saldo inicial são recusadas mesmo se marcadas.
    /// </summary>
    /// <param name="categoryFor">Categoria de cada linha (nula = sem categoria).</param>
    public ImportBatch Import(Guid accountId, ImportFormat format, string fileName, string sha256, IReadOnlyList<ImportCandidate> chosen, Func<ImportCandidate, Guid?> categoryFor, int skipped)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(categoryFor);
        var account = Account(accountId) ?? throw new FinanceValidationException("Escolha a conta do extrato.");
        if (account.IsArchived)
        {
            throw new FinanceValidationException($"A conta {account.Name} está arquivada.");
        }
        if (chosen.Count == 0)
        {
            throw new FinanceValidationException("Nenhuma linha escolhida para importar.");
        }
        if (chosen.Any(c => c.Status is ImportRowStatus.AlreadyImported or ImportRowStatus.BeforeOpening))
        {
            throw new FinanceValidationException("Há linhas já importadas ou anteriores ao saldo inicial da conta.");
        }
        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(Now),
            Format = format,
            FileName = Path.GetFileName(fileName) is { Length: > 0 } name ? (name.Length > 200 ? name[..200] : name) : "extrato",
            Sha256 = sha256,
            AccountId = accountId,
            Imported = chosen.Count,
            Skipped = Math.Max(0, skipped),
            CreatedAt = Now,
        };
        var transactions = new List<FinanceTransaction>(chosen.Count);
        foreach (var c in chosen)
        {
            var income = c.Row.Amount > 0;
            var category = categoryFor(c);
            if (category is { } id && (Category(id) is not { } cat || cat.Kind != (income ? CategoryKind.Income : CategoryKind.Expense)))
            {
                category = null; // categoria do sentido errado: fica sem categoria em vez de falhar o lote
            }
            transactions.Add(new FinanceTransaction
            {
                Id = Guid.CreateVersion7(Now),
                Kind = income ? TransactionKind.Income : TransactionKind.Expense,
                Date = c.Row.Date,
                Description = c.Row.Description,
                CategoryId = category,
                Status = TransactionStatus.Cleared,
                PaymentMethod = PaymentMethod.Other,
                Notes = $"Importado de {batch.FileName}",
                ImportBatchId = batch.Id,
                ExternalId = c.Row.ExternalId,
                Fingerprint = c.Fingerprint,
                Postings = [new Posting(accountId, c.Row.Amount)],
                CreatedAt = Now,
                UpdatedAt = Now,
            });
        }
        _store.SaveImport(batch, transactions);
        lock (_gate)
        {
            _batches.Add(batch);
            _transactions.AddRange(transactions);
        }
        Changed();
        return batch;
    }

    /// <summary>Lançamentos do lote ainda existentes (editados ou não).</summary>
    public IReadOnlyList<FinanceTransaction> BatchTransactions(Guid batchId) => [.. Transactions.Where(t => t.ImportBatchId == batchId)];

    /// <summary>Desfaz o lote inteiro: remove os lançamentos dele (inclusive os editados depois). Estornos ligados ficam soltos.</summary>
    public int UndoImport(Guid batchId)
    {
        ImportBatch batch;
        lock (_gate)
        {
            batch = _batches.FirstOrDefault(b => b.Id == batchId) ?? throw new FinanceValidationException("Lote não encontrado.");
        }
        if (batch.UndoneAt is not null)
        {
            throw new FinanceValidationException("Este lote já foi desfeito.");
        }
        var ids = BatchTransactions(batchId).Select(t => t.Id).ToHashSet();
        if (ids.Any(id => ContributionOf(id) is not null))
        {
            throw new FinanceValidationException("Um lançamento deste lote está ligado a uma meta: remova a contribuição antes.");
        }
        _store.UndoImport(batchId, Now);
        lock (_gate)
        {
            _transactions.RemoveAll(t => ids.Contains(t.Id));
            for (var i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].RefundOfId is { } r && ids.Contains(r))
                {
                    _transactions[i] = _transactions[i] with { RefundOfId = null };
                }
            }
            _batches.RemoveAll(b => b.Id == batchId);
            _batches.Add(batch with { UndoneAt = Now });
        }
        Changed();
        return ids.Count;
    }
}
