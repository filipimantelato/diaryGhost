using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopHub.Core.Finance;

public enum ImportFormat
{
    Csv,
    Ofx,
}

/// <summary>Uma linha lida do arquivo: valor com sinal do ponto de vista da conta (+ entrou, − saiu).</summary>
public sealed record ImportedRow(int Line, DateOnly Date, long Amount, string Description, string? ExternalId);

/// <summary>Como ler um CSV: separador, colunas (índices a partir de 0) e formatos.</summary>
public sealed record CsvLayout
{
    public char Delimiter { get; init; } = ';';
    public bool HasHeader { get; init; } = true;
    public int DateColumn { get; init; }
    public int DescriptionColumn { get; init; } = 1;
    /// <summary>Coluna única de valor com sinal; ou débito/crédito separados.</summary>
    public int? AmountColumn { get; init; } = 2;
    public int? DebitColumn { get; init; }
    public int? CreditColumn { get; init; }
    public string DateFormat { get; init; } = "dd/MM/yyyy";
    public bool DecimalComma { get; init; } = true;
    /// <summary>Extratos que trazem saídas como positivas (inverte o sinal).</summary>
    public bool InvertSign { get; init; }
}

/// <summary>Resultado da leitura: linhas válidas, erros por linha (sem conteúdo sensível além do número) e metadados.</summary>
public sealed record ParsedStatement(ImportFormat Format, IReadOnlyList<ImportedRow> Rows, IReadOnlyList<string> Errors)
{
    /// <summary>OFX: moeda declarada (CURDEF).</summary>
    public string? Currency { get; init; }

    /// <summary>OFX: últimos 4 dígitos da conta, só para conferência.</summary>
    public string? AccountHint { get; init; }
}

public enum ImportRowStatus
{
    New,
    /// <summary>Já importado antes (mesmo identificador do banco ou mesma impressão digital).</summary>
    AlreadyImported,
    /// <summary>Lançamento parecido já existe (mesmo valor, até 2 dias de diferença): decidir.</summary>
    PossibleDuplicate,
    /// <summary>Antes do saldo inicial da conta: não pode entrar.</summary>
    BeforeOpening,
}

/// <param name="Fingerprint">Impressão digital estável da linha (conta, data, valor, descrição e repetição no arquivo).</param>
/// <param name="MatchId">Lançamento existente que motivou a marcação.</param>
public sealed record ImportCandidate(ImportedRow Row, string Fingerprint, ImportRowStatus Status, Guid? MatchId, Guid? SuggestedCategoryId)
{
    public bool IncludedByDefault => Status == ImportRowStatus.New;
}

/// <summary>Lote importado (pode ser desfeito inteiro).</summary>
public sealed record ImportBatch
{
    public required Guid Id { get; init; }
    public required ImportFormat Format { get; init; }
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public required Guid AccountId { get; init; }
    public required int Imported { get; init; }
    public required int Skipped { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UndoneAt { get; init; }
}

/// <summary>
/// Leitura de extratos (Fase 12). Tudo é tratado como dado: nada é executado, há limite de tamanho e de linhas, e OFX
/// com DTD ou entidades é recusado (sem XXE). Valores são lidos em centavos exatos; mais casas decimais que a moeda
/// permite invalidam a linha (nunca arredonda em silêncio).
/// </summary>
public static partial class StatementParser
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxRows = 20_000;

    public static string Sha256(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>Texto do arquivo: UTF-8 (com ou sem BOM); se não for UTF-8 válido, Latin-1 (comum em bancos brasileiros).</summary>
    public static string Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaxBytes)
        {
            throw new FinanceValidationException("Arquivo grande demais (máximo 20 MB).");
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    public static bool LooksLikeOfx(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) || head.Contains("<OFX>", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Valores e datas ----------

    /// <summary>Valor com separador decimal conhecido ("1.234,56" com vírgula; "1,234.56" ou "-12.50" com ponto).</summary>
    public static bool TryParseAmount(string? text, bool decimalComma, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var t = text.Trim().Replace("R$", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1];
        }
        if (t.EndsWith('-'))
        {
            negative = !negative;
            t = t[..^1];
        }
        if (t.StartsWith('-') || t.StartsWith('−'))
        {
            negative = !negative;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }
        var (thousands, dec) = decimalComma ? ('.', ',') : (',', '.');
        t = t.Replace(thousands.ToString(), "", StringComparison.Ordinal).Replace(dec, '.');
        if (t.Length == 0 || t.Count(c => c == '.') > 1 || t.Any(c => !char.IsAsciiDigit(c) && c != '.'))
        {
            return false;
        }
        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        var cents = value * 100m;
        if (cents != decimal.Truncate(cents) || cents > Money.MaxAbsMinor)
        {
            return false;
        }
        minor = (long)cents * (negative ? -1 : 1);
        return true;
    }

    private static readonly char[] Delimiters = [';', ',', '\t', '|'];

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy"];

    public static bool TryParseDate(string? text, string format, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // ---------- CSV ----------

    /// <summary>Divide o texto em linhas e campos (aspas duplas, separador dentro de aspas, "" escapado).</summary>
    public static List<string[]> SplitCsv(string text, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                fields.Add(field.ToString());
                field.Clear();
                if (fields.Any(f => f.Length > 0))
                {
                    rows.Add([.. fields]);
                }
                fields.Clear();
                if (rows.Count > MaxRows + 1)
                {
                    throw new FinanceValidationException($"Arquivo com linhas demais (máximo {MaxRows}).");
                }
            }
            else
            {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());
        if (fields.Any(f => f.Length > 0))
        {
            rows.Add([.. fields]);
        }
        return rows;
    }

    /// <summary>Palpite de layout: separador mais frequente, cabeçalho, colunas de data, descrição e valor, formatos.</summary>
    public static CsvLayout Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sample = string.Join('\n', text.Split('\n').Take(30));
        var delimiter = Delimiters.MaxBy(d => sample.Count(c => c == d));
        var rows = SplitCsv(sample, delimiter);
        if (rows.Count == 0)
        {
            return new CsvLayout { Delimiter = delimiter };
        }
        var width = rows.Max(r => r.Length);
        var header = rows[0];
        var body = rows.Skip(1).Take(20).ToList();
        string? dateFormat = null;
        var dateColumn = -1;
        for (var c = 0; c < width && dateColumn < 0; c++)
        {
            foreach (var f in DateFormats)
            {
                var values = body.Where(r => c < r.Length).Select(r => r[c]).ToList();
                if (values.Count > 0 && values.Count(v => TryParseDate(v, f, out _)) >= Math.Max(1, values.Count * 8 / 10))
                {
                    dateColumn = c;
                    dateFormat = f;
                    break;
                }
            }
        }
        var hasHeader = dateColumn < 0 || !TryParseDate(header.ElementAtOrDefault(dateColumn), dateFormat ?? "dd/MM/yyyy", out _);
        var decimalComma = sample.Count(c => c == ',') >= sample.Count(c => c == '.') || delimiter == ',';
        if (delimiter == ',')
        {
            decimalComma = false;
        }
        int? FindHeader(params string[] names)
        {
            if (!hasHeader)
            {
                return null;
            }
            for (var c = 0; c < header.Length; c++)
            {
                var h = FinanceService.Fold(header[c]);
                if (names.Any(n => h.Contains(n, StringComparison.Ordinal)))
                {
                    return c;
                }
            }
            return null;
        }
        var debit = FindHeader("debito", "saida");
        var credit = FindHeader("credito", "entrada");
        var amount = FindHeader("valor", "amount", "quantia", "montante");
        if (amount is null && (debit is null || credit is null))
        {
            // Primeira coluna (fora a data) em que quase todas as linhas são valores.
            for (var c = 0; c < width; c++)
            {
                if (c != dateColumn && body.Count > 0 && body.Count(r => c < r.Length && TryParseAmount(r[c], decimalComma, out _)) >= Math.Max(1, body.Count * 8 / 10))
                {
                    amount = c;
                    break;
                }
            }
        }
        var description = FindHeader("descri", "historico", "lancamento", "memo", "estabelecimento", "detalhe")
            ?? Enumerable.Range(0, width).Where(c => c != dateColumn && c != amount && c != debit && c != credit)
                .OrderByDescending(c => body.Sum(r => c < r.Length ? r[c].Length : 0)).Cast<int?>().FirstOrDefault()
            ?? 1;
        return new CsvLayout
        {
            Delimiter = delimiter,
            HasHeader = hasHeader,
            DateColumn = Math.Max(0, dateColumn),
            DescriptionColumn = description,
            AmountColumn = amount is not null || debit is null || credit is null ? amount ?? 2 : null,
            DebitColumn = amount is null ? debit : null,
            CreditColumn = amount is null ? credit : null,
            DateFormat = dateFormat ?? "dd/MM/yyyy",
            DecimalComma = decimalComma,
        };
    }

    public static ParsedStatement ParseCsv(string text, CsvLayout layout)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(layout);
        var rows = SplitCsv(text, layout.Delimiter);
        var result = new List<ImportedRow>();
        var errors = new List<string>();
        for (var i = layout.HasHeader ? 1 : 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 1;
            string Cell(int c) => c < r.Length ? r[c].Trim() : "";
            if (!TryParseDate(Cell(layout.DateColumn), layout.DateFormat, out var date))
            {
                errors.Add($"Linha {line}: data inválida.");
                continue;
            }
            long amount;
            if (layout.AmountColumn is { } a)
            {
                if (!TryParseAmount(Cell(a), layout.DecimalComma, out amount))
                {
                    errors.Add($"Linha {line}: valor inválido.");
                    continue;
                }
            }
            else
            {
                var hasDebit = TryParseAmount(Cell(layout.DebitColumn ?? -1), layout.DecimalComma, out var debit);
                var hasCredit = TryParseAmount(Cell(layout.CreditColumn ?? -1), layout.DecimalComma, out var credit);
                if (!hasDebit && !hasCredit)
                {
                    errors.Add($"Linha {line}: sem valor de débito nem de crédito.");
                    continue;
                }
                amount = (hasCredit ? Math.Abs(credit) : 0) - (hasDebit ? Math.Abs(debit) : 0);
            }
            if (layout.InvertSign)
            {
                amount = -amount;
            }
            if (amount == 0)
            {
                continue; // linhas de saldo/total sem movimento
            }
            var description = Clean(Cell(layout.DescriptionColumn));
            result.Add(new ImportedRow(line, date, amount, description.Length == 0 ? "(sem descrição)" : description, null));
        }
        return new ParsedStatement(ImportFormat.Csv, result, errors);
    }

    private static string Clean(string text)
    {
        var t = WhiteSpace().Replace(text, " ").Trim();
        return t.Length > FinanceService.MaxDescriptionLength ? t[..FinanceService.MaxDescriptionLength] : t;
    }

    // ---------- OFX ----------

    /// <summary>OFX 1.x (SGML, tags sem fechamento) e 2.x (XML). DTD ou entidades → recusado.</summary>
    public static ParsedStatement ParseOfx(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new FinanceValidationException("Arquivo OFX com DTD ou entidades: recusado por segurança.");
        }
        var currency = Field(text, "CURDEF");
        var account = Field(text, "ACCTID");
        var rows = new List<ImportedRow>();
        var errors = new List<string>();
        var index = 0;
        foreach (Match block in Transaction().Matches(text))
        {
            index++;
            if (index > MaxRows)
            {
                throw new FinanceValidationException($"Arquivo com lançamentos demais (máximo {MaxRows}).");
            }
            var body = block.Groups[1].Value;
            var posted = Field(body, "DTPOSTED");
            var amountText = Field(body, "TRNAMT");
            if (posted is null || posted.Length < 8 || !DateOnly.TryParseExact(posted[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                errors.Add($"Lançamento {index}: data inválida.");
                continue;
            }
            // OFX usa ponto decimal, mas há bancos brasileiros que exportam com vírgula.
            var decimalComma = amountText is not null && amountText.Contains(',', StringComparison.Ordinal) && !amountText.Contains('.', StringComparison.Ordinal);
            if (!TryParseAmount(amountText, decimalComma, out var amount))
            {
                errors.Add($"Lançamento {index}: valor inválido.");
                continue;
            }
            if (amount == 0)
            {
                continue;
            }
            var memo = Field(body, "MEMO");
            var name = Field(body, "NAME");
            var description = Clean(WebUtility.HtmlDecode(string.Join(" · ", new[] { name, memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())));
            var fitId = Field(body, "FITID");
            rows.Add(new ImportedRow(index, date, amount, description.Length == 0 ? "(sem descrição)" : description, string.IsNullOrWhiteSpace(fitId) ? null : fitId.Trim()));
        }
        if (index == 0)
        {
            errors.Add("Nenhum lançamento (STMTTRN) encontrado no arquivo.");
        }
        return new ParsedStatement(ImportFormat.Ofx, rows, errors)
        {
            Currency = currency?.Trim().ToUpperInvariant(),
            AccountHint = account is { Length: >= 4 } a ? a.Trim()[^4..] : null,
        };
    }

    private static string? Field(string body, string tag)
    {
        var m = Regex.Match(body, $"<{tag}>([^<\\r\\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Transaction();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();
}
