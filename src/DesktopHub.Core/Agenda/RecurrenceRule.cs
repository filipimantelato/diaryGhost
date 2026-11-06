using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace DesktopHub.Core.Agenda;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>Dia da semana com ordinal opcional (RFC 5545 BYDAY): 0 = todos; 2 = segundo; -1 = último.</summary>
public readonly record struct WeekdayNum(DayOfWeek Day, int Ordinal = 0);

/// <summary>
/// Regra de recorrência — subconjunto do RRULE do RFC 5545: FREQ (DAILY/WEEKLY/MONTHLY/YEARLY), INTERVAL,
/// BYDAY (com ordinal em MONTHLY/YEARLY), BYMONTHDAY (negativos contam do fim do mês), BYMONTH, BYSETPOS,
/// COUNT e UNTIL. A semana começa na segunda (WKST=MO, padrão do RFC).
/// <para>
/// <see cref="Until"/> é inclusivo e, quando não é UTC (<see cref="DateTimeKind.Utc"/>), está no horário de
/// parede do evento — o mesmo referencial do início.
/// </para>
/// </summary>
public sealed record RecurrenceRule
{
    public const int MaxInterval = 999;

    public RecurrenceFrequency Frequency { get; init; }
    public int Interval { get; init; } = 1;
    public IReadOnlyList<WeekdayNum> ByDay { get; init; } = [];
    public IReadOnlyList<int> ByMonthDay { get; init; } = [];
    public IReadOnlyList<int> ByMonth { get; init; } = [];
    public IReadOnlyList<int> BySetPos { get; init; } = [];
    public int? Count { get; init; }
    public DateTime? Until { get; init; }

    public static RecurrenceRule Daily(int interval = 1) => new() { Frequency = RecurrenceFrequency.Daily, Interval = interval };

    public static RecurrenceRule Weekly(params DayOfWeek[] days) => new()
    {
        Frequency = RecurrenceFrequency.Weekly,
        ByDay = [.. days.Select(d => new WeekdayNum(d))],
    };

    public static RecurrenceRule Monthly() => new() { Frequency = RecurrenceFrequency.Monthly };

    public static RecurrenceRule Yearly() => new() { Frequency = RecurrenceFrequency.Yearly };

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public override string ToString()
    {
        var sb = new StringBuilder("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Interval != 1)
        {
            sb.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));
        }
        if (ByDay.Count > 0)
        {
            sb.Append(";BYDAY=").AppendJoin(',', ByDay.Select(d =>
                (d.Ordinal == 0 ? "" : d.Ordinal.ToString(CultureInfo.InvariantCulture)) + DayCodes[(int)d.Day]));
        }
        AppendList(sb, "BYMONTHDAY", ByMonthDay);
        AppendList(sb, "BYMONTH", ByMonth);
        AppendList(sb, "BYSETPOS", BySetPos);
        if (Count is { } count)
        {
            sb.Append(";COUNT=").Append(count.ToString(CultureInfo.InvariantCulture));
        }
        if (Until is { } until)
        {
            sb.Append(";UNTIL=").Append(until.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            if (until.Kind == DateTimeKind.Utc)
            {
                sb.Append('Z');
            }
        }
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string name, IReadOnlyList<int> values)
    {
        if (values.Count > 0)
        {
            sb.Append(';').Append(name).Append('=').AppendJoin(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Interpreta um RRULE (com ou sem o prefixo "RRULE:"). Entrada inválida ou não suportada → false.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RecurrenceRule? rule, out string? error)
    {
        rule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Regra vazia.";
            return false;
        }
        var body = text.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            body = body[6..];
        }
        var result = new RecurrenceRule();
        RecurrenceFrequency? frequency = null;
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                error = $"Parte inválida: '{part}'.";
                return false;
            }
            var name = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim();
            switch (name)
            {
                case "FREQ":
                    if (!Enum.TryParse<RecurrenceFrequency>(value, ignoreCase: true, out var f) || !Enum.IsDefined(f) || int.TryParse(value, out _))
                    {
                        error = $"Frequência não suportada: '{value}'.";
                        return false;
                    }
                    frequency = f;
                    break;
                case "INTERVAL":
                    if (!TryInt(value, 1, MaxInterval, out var interval))
                    {
                        error = "INTERVAL deve estar entre 1 e 999.";
                        return false;
                    }
                    result = result with { Interval = interval };
                    break;
                case "COUNT":
                    if (!TryInt(value, 1, 10_000, out var count))
                    {
                        error = "COUNT deve estar entre 1 e 10000.";
                        return false;
                    }
                    result = result with { Count = count };
                    break;
                case "UNTIL":
                    if (!TryParseUntil(value, out var until))
                    {
                        error = $"UNTIL inválido: '{value}'.";
                        return false;
                    }
                    result = result with { Until = until };
                    break;
                case "BYDAY":
                    var days = new List<WeekdayNum>();
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!TryParseWeekday(token.Trim(), out var day))
                        {
                            error = $"BYDAY inválido: '{token}'.";
                            return false;
                        }
                        days.Add(day);
                    }
                    result = result with { ByDay = days };
                    break;
                case "BYMONTHDAY":
                    if (!TryIntList(value, -31, 31, out var monthDays))
                    {
                        error = "BYMONTHDAY deve estar entre -31 e 31 (exceto 0).";
                        return false;
                    }
                    result = result with { ByMonthDay = monthDays };
                    break;
                case "BYMONTH":
                    if (!TryIntList(value, 1, 12, out var months))
                    {
                        error = "BYMONTH deve estar entre 1 e 12.";
                        return false;
                    }
                    result = result with { ByMonth = months };
                    break;
                case "BYSETPOS":
                    if (!TryIntList(value, -366, 366, out var positions))
                    {
                        error = "BYSETPOS inválido.";
                        return false;
                    }
                    result = result with { BySetPos = positions };
                    break;
                case "WKST":
                    if (!value.Equals("MO", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "Só WKST=MO é suportado.";
                        return false;
                    }
                    break;
                default:
                    error = $"{name} não é suportado.";
                    return false;
            }
        }
        if (frequency is null)
        {
            error = "FREQ é obrigatório.";
            return false;
        }
        if (result.Count is not null && result.Until is not null)
        {
            error = "COUNT e UNTIL não podem ser usados juntos.";
            return false;
        }
        if (frequency is RecurrenceFrequency.Daily or RecurrenceFrequency.Weekly && result.ByDay.Any(d => d.Ordinal != 0))
        {
            error = "Ordinais em BYDAY só valem para recorrência mensal ou anual.";
            return false;
        }
        rule = result with { Frequency = frequency.Value };
        return true;
    }

    public static RecurrenceRule Parse(string text) =>
        TryParse(text, out var rule, out var error) ? rule : throw new FormatException(error);

    private static bool TryInt(string value, int min, int max, out int result) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;

    private static bool TryIntList(string value, int min, int max, out List<int> result)
    {
        result = [];
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryInt(token.Trim(), min, max, out var n) || n == 0)
            {
                return false;
            }
            result.Add(n);
        }
        return result.Count > 0;
    }

    private static bool TryParseWeekday(string token, out WeekdayNum day)
    {
        day = default;
        if (token.Length < 2)
        {
            return false;
        }
        var index = Array.FindIndex(DayCodes, c => token.EndsWith(c, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }
        var prefix = token[..^2];
        var ordinal = 0;
        if (prefix.Length > 0 && (!TryInt(prefix, -53, 53, out ordinal) || ordinal == 0))
        {
            return false;
        }
        day = new WeekdayNum((DayOfWeek)index, ordinal);
        return true;
    }

    private static bool TryParseUntil(string value, out DateTime until)
    {
        var utc = value.EndsWith('Z');
        var core = utc ? value[..^1] : value;
        if (DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out until))
        {
            until = DateTime.SpecifyKind(until, utc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
            return true;
        }
        if (!utc && DateTime.TryParseExact(core, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out until))
        {
            // Só a data: inclui o dia inteiro.
            until = DateTime.SpecifyKind(until.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }
}
