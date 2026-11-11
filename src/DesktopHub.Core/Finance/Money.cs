using System.Globalization;

namespace DesktopHub.Core.Finance;

/// <summary>
/// Valores monetários como inteiros em unidades mínimas da moeda (centavos no real). Nunca ponto flutuante:
/// soma e subtração são exatas e verificadas contra estouro; <c>decimal</c> só aparece na formatação.
/// </summary>
public static class Money
{
    public const string DefaultCurrency = "BRL";

    /// <summary>Maior valor absoluto aceito num lançamento: 100 bilhões em unidades da moeda (com 2 casas).</summary>
    public const long MaxAbsMinor = 10_000_000_000_000;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly Dictionary<string, (int Digits, string Symbol)> Currencies = new(StringComparer.Ordinal)
    {
        ["BRL"] = (2, "R$"),
        ["USD"] = (2, "US$"),
        ["EUR"] = (2, "€"),
        ["GBP"] = (2, "£"),
        ["ARS"] = (2, "AR$"),
        ["JPY"] = (0, "¥"),
    };

    public static IReadOnlyCollection<string> KnownCurrencies => Currencies.Keys;

    public static bool IsKnownCurrency(string? code) => code is not null && Currencies.ContainsKey(code);

    public static int Digits(string currency) => Currencies.TryGetValue(currency, out var c) ? c.Digits : 2;

    public static string Symbol(string currency) => Currencies.TryGetValue(currency, out var c) ? c.Symbol : currency;

    public static long Add(long a, long b) => checked(a + b);

    public static long Sum(IEnumerable<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        long total = 0;
        foreach (var v in values)
        {
            total = checked(total + v);
        }
        return total;
    }

    /// <summary>Valor em unidades da moeda (ex.: 1234 centavos → 12,34m), exato.</summary>
    public static decimal ToDecimal(long minor, string currency = DefaultCurrency)
    {
        var digits = Digits(currency);
        decimal scale = 1;
        for (var i = 0; i < digits; i++)
        {
            scale *= 10;
        }
        return minor / scale;
    }

    /// <summary>"R$ 1.234,56", "−R$ 10,00" (sinal tipográfico), "+R$ 5,00" com <paramref name="showPlus"/>.</summary>
    public static string Format(long minor, string currency = DefaultCurrency, bool showPlus = false, bool symbol = true)
    {
        var digits = Digits(currency);
        var number = Math.Abs(ToDecimal(minor, currency)).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), PtBr);
        var text = symbol ? $"{Symbol(currency)} {number}" : number;
        if (minor < 0)
        {
            return "−" + text;
        }
        return showPlus && minor > 0 ? "+" + text : text;
    }

    /// <summary>Texto mascarado do modo privacidade (não revela nem a ordem de grandeza).</summary>
    public static string Masked(string currency = DefaultCurrency) => $"{Symbol(currency)} ••••";

    /// <summary>
    /// Lê um valor digitado ou importado: "1.234,56", "1234,56", "1234.56", "R$ 10", "-10,5", "(10,00)".
    /// Um único separador seguido de 1–2 dígitos é o decimal; os demais são de milhar. Mais casas que a
    /// moeda permite → falha (não arredonda em silêncio).
    /// </summary>
    public static bool TryParse(string? text, out long minor, string currency = DefaultCurrency)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var t = text.Trim();
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1].Trim();
        }
        t = t.Replace(Symbol(currency), "", StringComparison.Ordinal).Replace(currency, "", StringComparison.OrdinalIgnoreCase)
             .Replace("R$", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (t.StartsWith('-') || t.StartsWith('−'))
        {
            negative = !negative;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }
        if (t.Length == 0 || t.Any(c => !char.IsAsciiDigit(c) && c is not ('.' or ',')))
        {
            return false;
        }

        var lastSep = t.LastIndexOfAny(['.', ',']);
        string integerPart;
        var fraction = "";
        if (lastSep >= 0)
        {
            var after = t[(lastSep + 1)..];
            var sepChar = t[lastSep];
            var sameSepCount = t.Count(c => c == sepChar);
            var otherSepPresent = t.Any(c => c is '.' or ',' && c != sepChar);
            // "1.234" (um ponto, 3 dígitos depois, sem outro separador) é milhar; "1,5" e "1.50" são decimais.
            var isDecimal = otherSepPresent || (sameSepCount == 1 && after.Length != 3) || (sameSepCount == 1 && sepChar == ',');
            if (isDecimal)
            {
                integerPart = t[..lastSep];
                fraction = after;
            }
            else
            {
                integerPart = t;
            }
        }
        else
        {
            integerPart = t;
        }

        var digits = Digits(currency);
        if (fraction.Length > digits || fraction.Any(c => !char.IsAsciiDigit(c)))
        {
            return false;
        }
        // Separadores de milhar: grupos de 3 dígitos.
        var groups = integerPart.Split('.', ',');
        if (groups.Length > 1 && (groups[0].Length is 0 or > 3 || groups.Skip(1).Any(g => g.Length != 3)))
        {
            return false;
        }
        var integerDigits = string.Concat(groups);
        if (integerDigits.Length == 0)
        {
            integerDigits = "0";
        }
        if (integerDigits.Length > 15)
        {
            return false;
        }
        var value = long.Parse(integerDigits, NumberStyles.None, CultureInfo.InvariantCulture);
        for (var i = 0; i < digits; i++)
        {
            value *= 10;
        }
        if (digits > 0)
        {
            var frac = fraction.PadRight(digits, '0');
            value += long.Parse(frac, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        if (value > MaxAbsMinor)
        {
            return false;
        }
        minor = negative ? -value : value;
        return true;
    }

    /// <summary>
    /// Divide <paramref name="total"/> em <paramref name="parts"/> partes inteiras cuja soma é exatamente o total;
    /// o resto vai para a primeira parte (prática dos emissores brasileiros em parcelamentos).
    /// </summary>
    public static long[] Split(long total, int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        var sign = Math.Sign(total);
        var abs = Math.Abs(total);
        var each = abs / parts;
        var rest = abs - (each * parts);
        var result = new long[parts];
        for (var i = 0; i < parts; i++)
        {
            result[i] = sign * (each + (i == 0 ? rest : 0));
        }
        return result;
    }

    /// <summary>Divisão inteira arredondada (meio centavo longe do zero), em aritmética exata.</summary>
    public static long DivideRounded(long value, long divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException();
        }
        return (long)Math.Round((decimal)value / divisor, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Percentual para exibição (arredondado longe do zero), ou null se a base é zero.</summary>
    public static decimal? Percent(long part, long whole) =>
        whole == 0 ? null : Math.Round((decimal)part * 100m / whole, 1, MidpointRounding.AwayFromZero);
}
