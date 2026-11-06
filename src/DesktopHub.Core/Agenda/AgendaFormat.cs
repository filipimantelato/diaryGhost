using System.Globalization;

namespace DesktopHub.Core.Agenda;

/// <summary>Textos da agenda em pt-BR (usados em notificações e telas).</summary>
public static class AgendaFormat
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Time(DateTime wall) => wall.ToString("HH:mm", PtBr);

    /// <summary>"hoje", "amanhã", "ontem", "sex., 14/11" ou "14/11/2027".</summary>
    public static string Day(DateTime day, DateTime today)
    {
        var diff = (day.Date - today.Date).Days;
        return diff switch
        {
            0 => "hoje",
            1 => "amanhã",
            -1 => "ontem",
            _ when day.Year == today.Year => day.ToString("ddd, dd/MM", PtBr),
            _ => day.ToString("dd/MM/yyyy", PtBr),
        };
    }

    public static string DayTitle(DateTime day) => Capitalize(day.ToString("dddd, d 'de' MMMM", PtBr));

    public static string MonthTitle(DateTime month) => Capitalize(month.ToString("MMMM 'de' yyyy", PtBr));

    public static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], PtBr) + text[1..];

    /// <summary>"5 min", "1 h 20 min", "2 dias".</summary>
    public static string Span(TimeSpan span)
    {
        var minutes = (int)Math.Round(Math.Abs(span.TotalMinutes));
        if (minutes < 60)
        {
            return $"{minutes} min";
        }
        if (minutes < 24 * 60)
        {
            var h = minutes / 60;
            var m = minutes % 60;
            return m == 0 ? $"{h} h" : $"{h} h {m} min";
        }
        var days = (int)Math.Round(minutes / (24 * 60.0));
        return days == 1 ? "1 dia" : $"{days} dias";
    }

    /// <summary>Antecedência de um aviso: "Na hora", "10 min antes", "1 dia antes".</summary>
    public static string ReminderOffset(int minutes) => minutes == 0 ? "Na hora" : $"{Span(TimeSpan.FromMinutes(minutes))} antes";

    public static string Priority(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => "Baixa",
        TaskPriority.High => "Alta",
        TaskPriority.Urgent => "Urgente",
        _ => "Normal",
    };

    private static readonly string[] Ordinals = ["primeira", "segunda", "terceira", "quarta", "quinta"];

    private static string Weekday(DayOfWeek day) => PtBr.DateTimeFormat.GetDayName(day);

    /// <summary>Descrição legível da regra: "Toda semana na segunda e quarta, 10 vezes".</summary>
    public static string Describe(RecurrenceRule? rule, DateTime start)
    {
        if (rule is null)
        {
            return "Não se repete";
        }
        var n = rule.Interval;
        var text = rule.Frequency switch
        {
            RecurrenceFrequency.Daily when rule.ByDay.Count == 5 && !rule.ByDay.Any(d => d.Day is DayOfWeek.Saturday or DayOfWeek.Sunday) => "Todo dia útil",
            RecurrenceFrequency.Daily => n == 1 ? "Todo dia" : $"A cada {n} dias",
            RecurrenceFrequency.Weekly => (n == 1 ? "Toda semana" : $"A cada {n} semanas") + " " + WeekdayList(rule.ByDay.Count > 0 ? rule.ByDay.Select(d => d.Day) : [start.DayOfWeek]),
            RecurrenceFrequency.Monthly => (n == 1 ? "Todo mês" : $"A cada {n} meses") + " " + MonthlyDetail(rule, start),
            _ => (n == 1 ? "Todo ano" : $"A cada {n} anos") + $" em {start.ToString("d 'de' MMMM", PtBr)}",
        };
        if (rule.Count is { } count)
        {
            text += count == 1 ? ", 1 vez" : $", {count} vezes";
        }
        else if (rule.Until is { } until)
        {
            text += $", até {until.ToString("dd/MM/yyyy", PtBr)}";
        }
        return text;
    }

    private static string WeekdayList(IEnumerable<DayOfWeek> days)
    {
        var ordered = days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(Weekday).ToList();
        return "na " + (ordered.Count == 1 ? ordered[0] : string.Join(", ", ordered[..^1]) + " e " + ordered[^1]);
    }

    private static string MonthlyDetail(RecurrenceRule rule, DateTime start)
    {
        if (rule.ByMonthDay is [-1])
        {
            return "no último dia";
        }
        if (rule.ByDay is [{ Ordinal: not 0 } w])
        {
            var which = w.Ordinal == -1 ? "última" : w.Ordinal is > 0 and <= 5 ? Ordinals[w.Ordinal - 1] : $"{w.Ordinal}ª";
            return $"na {which} {Weekday(w.Day)}";
        }
        if (rule.ByMonthDay.Count > 0)
        {
            return "no dia " + string.Join(", ", rule.ByMonthDay);
        }
        return $"no dia {start.Day}";
    }

    /// <summary>Ordinal da semana do mês de uma data (1–5) e se é a última daquele dia da semana.</summary>
    public static (int Ordinal, bool IsLast) WeekOfMonth(DateTime day) =>
        ((day.Day - 1) / 7 + 1, day.AddDays(7).Month != day.Month);
}
