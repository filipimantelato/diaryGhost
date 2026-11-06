namespace DesktopHub.Core.Agenda;

/// <summary>
/// Expande uma regra em inícios de ocorrência no <b>horário de parede</b> do evento (sem fuso), em ordem.
/// Seguindo o RFC 5545: o início (DTSTART) é sempre a primeira ocorrência; COUNT conta a partir dele e
/// inclui as exceções (EXDATE removem ocorrências depois da contagem); datas inválidas (31 de abril,
/// 29 de fevereiro em ano comum) são puladas, não ajustadas — "último dia do mês" é BYMONTHDAY=-1.
/// A conversão para UTC (horário de verão etc.) fica em <see cref="ZonedTime"/>.
/// </summary>
public static class RecurrenceExpander
{
    /// <summary>Períodos seguidos sem nenhuma ocorrência a partir dos quais a regra é considerada vazia (ex.: 30 de fevereiro).</summary>
    private const int MaxEmptyPeriods = 1000;
    private const int MaxYear = 9000;

    /// <param name="toUtc">Necessário só quando <see cref="RecurrenceRule.Until"/> está em UTC.</param>
    public static IEnumerable<DateTime> Occurrences(DateTime start, RecurrenceRule? rule,
        IReadOnlyCollection<DateTime>? exceptions = null, Func<DateTime, DateTimeOffset>? toUtc = null)
    {
        start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        var excluded = exceptions is { Count: > 0 } ? new HashSet<DateTime>(exceptions.Select(e => DateTime.SpecifyKind(e, DateTimeKind.Unspecified))) : null;
        return rule is null ? Single(start, excluded) : Expand(start, rule, excluded, toUtc);
    }

    private static IEnumerable<DateTime> Single(DateTime start, HashSet<DateTime>? excluded)
    {
        if (excluded?.Contains(start) != true)
        {
            yield return start;
        }
    }

    private static IEnumerable<DateTime> Expand(DateTime start, RecurrenceRule rule, HashSet<DateTime>? excluded, Func<DateTime, DateTimeOffset>? toUtc)
    {
        bool PastUntil(DateTime candidate) => rule.Until is { } until && (until.Kind == DateTimeKind.Utc && toUtc is not null
            ? toUtc(candidate).UtcDateTime > until
            : candidate > until);

        if (PastUntil(start))
        {
            yield break;
        }
        var emitted = 1;
        if (excluded?.Contains(start) != true)
        {
            yield return start;
        }
        if (rule.Count is { } total && emitted >= total)
        {
            yield break;
        }

        var empty = 0;
        for (var period = 0; ; period++)
        {
            var candidates = Candidates(start, rule, period, out var beyondRange);
            if (beyondRange)
            {
                yield break;
            }
            if (candidates.Count == 0)
            {
                if (++empty > MaxEmptyPeriods)
                {
                    yield break;
                }
                continue;
            }
            empty = 0;
            foreach (var candidate in candidates)
            {
                if (candidate <= start)
                {
                    continue;
                }
                if (PastUntil(candidate))
                {
                    yield break;
                }
                emitted++;
                if (excluded?.Contains(candidate) != true)
                {
                    yield return candidate;
                }
                if (rule.Count is { } n && emitted >= n)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Candidatos de um período (dia, semana, mês ou ano), já ordenados e com BYSETPOS aplicado.</summary>
    internal static List<DateTime> Candidates(DateTime start, RecurrenceRule rule, int period, out bool beyondRange)
    {
        beyondRange = false;
        var step = (long)period * rule.Interval;
        var time = start.TimeOfDay;
        List<DateTime> days;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Daily:
            {
                if (step > (long)(MaxYear - start.Year) * 365)
                {
                    beyondRange = true;
                    return [];
                }
                var day = start.Date.AddDays(step);
                days = MatchesFilters(day, rule) ? [day] : [];
                break;
            }
            case RecurrenceFrequency.Weekly:
            {
                var monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                if (step * 7 > (long)(MaxYear - start.Year) * 365)
                {
                    beyondRange = true;
                    return [];
                }
                var weekStart = monday.AddDays(step * 7);
                var weekdays = rule.ByDay.Count > 0 ? rule.ByDay.Select(d => d.Day).Distinct() : [start.DayOfWeek];
                days = [.. weekdays
                    .Select(d => weekStart.AddDays(((int)d + 6) % 7))
                    .Where(d => rule.ByMonth.Count == 0 || rule.ByMonth.Contains(d.Month))
                    .Order()];
                break;
            }
            case RecurrenceFrequency.Monthly:
            {
                var firstOfStart = new DateTime(start.Year, start.Month, 1);
                if (step > 100_000 || firstOfStart.Year + step / 12 > MaxYear)
                {
                    beyondRange = true;
                    return [];
                }
                var month = firstOfStart.AddMonths((int)step);
                days = rule.ByMonth.Count > 0 && !rule.ByMonth.Contains(month.Month) ? [] : DaysInMonth(month, start, rule);
                break;
            }
            default:
            {
                var year = start.Year + step;
                if (year > MaxYear)
                {
                    beyondRange = true;
                    return [];
                }
                days = DaysInYear((int)year, start, rule);
                break;
            }
        }
        var result = ApplySetPos(days, rule.BySetPos);
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = result[i] + time;
        }
        return result;
    }

    private static bool MatchesFilters(DateTime day, RecurrenceRule rule) =>
        (rule.ByMonth.Count == 0 || rule.ByMonth.Contains(day.Month)) &&
        (rule.ByMonthDay.Count == 0 || rule.ByMonthDay.Any(md => ResolveMonthDay(day.Year, day.Month, md) == day.Day)) &&
        (rule.ByDay.Count == 0 || rule.ByDay.Any(d => d.Day == day.DayOfWeek));

    private static List<DateTime> DaysInMonth(DateTime month, DateTime start, RecurrenceRule rule)
    {
        var count = DateTime.DaysInMonth(month.Year, month.Month);
        IEnumerable<DateTime> days;
        if (rule.ByDay.Count > 0)
        {
            var all = Enumerable.Range(1, count).Select(d => new DateTime(month.Year, month.Month, d)).ToList();
            days = rule.ByDay.SelectMany(w => Nth(all, w)).Distinct();
            if (rule.ByMonthDay.Count > 0)
            {
                var allowed = rule.ByMonthDay.Select(md => ResolveMonthDay(month.Year, month.Month, md)).ToHashSet();
                days = days.Where(d => allowed.Contains(d.Day));
            }
        }
        else if (rule.ByMonthDay.Count > 0)
        {
            days = rule.ByMonthDay
                .Select(md => ResolveMonthDay(month.Year, month.Month, md))
                .Where(d => d > 0)
                .Distinct()
                .Select(d => new DateTime(month.Year, month.Month, d));
        }
        else
        {
            days = start.Day <= count ? [new DateTime(month.Year, month.Month, start.Day)] : [];
        }
        return [.. days.Order()];
    }

    private static List<DateTime> DaysInYear(int year, DateTime start, RecurrenceRule rule)
    {
        if (rule.ByDay.Count > 0 && rule.ByMonth.Count == 0 && rule.ByMonthDay.Count == 0)
        {
            // Ordinais relativos ao ano (ex.: 20MO = 20ª segunda-feira do ano).
            var all = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365).Select(i => new DateTime(year, 1, 1).AddDays(i)).ToList();
            return [.. rule.ByDay.SelectMany(w => Nth(all, w)).Distinct().Order()];
        }
        IEnumerable<int> months = rule.ByMonth.Count > 0 ? rule.ByMonth.Distinct().Order() : [start.Month];
        return [.. months.SelectMany(m => DaysInMonth(new DateTime(year, m, 1), start, rule)).Order()];
    }

    /// <summary>Dias de <paramref name="all"/> que caem no dia da semana; ordinal 0 = todos, n = n-ésimo, -n = n-ésimo do fim.</summary>
    private static List<DateTime> Nth(List<DateTime> all, WeekdayNum weekday)
    {
        var matching = all.Where(d => d.DayOfWeek == weekday.Day).ToList();
        if (weekday.Ordinal == 0)
        {
            return matching;
        }
        var index = weekday.Ordinal > 0 ? weekday.Ordinal - 1 : matching.Count + weekday.Ordinal;
        return index >= 0 && index < matching.Count ? [matching[index]] : [];
    }

    /// <summary>Converte BYMONTHDAY (positivo ou negativo) em dia do mês; 0 se não existir naquele mês.</summary>
    private static int ResolveMonthDay(int year, int month, int monthDay)
    {
        var count = DateTime.DaysInMonth(year, month);
        var day = monthDay > 0 ? monthDay : count + monthDay + 1;
        return day >= 1 && day <= count ? day : 0;
    }

    private static List<DateTime> ApplySetPos(List<DateTime> days, IReadOnlyList<int> positions)
    {
        if (positions.Count == 0 || days.Count == 0)
        {
            return days;
        }
        return [.. positions
            .Select(p => p > 0 ? p - 1 : days.Count + p)
            .Where(i => i >= 0 && i < days.Count)
            .Select(i => days[i])
            .Distinct()
            .Order()];
    }
}

/// <summary>Conversão de horário de parede para UTC com as regras do RFC 5545 para horário de verão.</summary>
public static class ZonedTime
{
    /// <summary>
    /// Horário que não existe (o relógio pulou, ex.: 02:30 no início do horário de verão) usa o deslocamento
    /// de antes do salto — cai 1 h depois no relógio (03:30). Horário ambíguo (o relógio voltou) usa a
    /// primeira ocorrência.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(wall))
        {
            offset = zone.GetUtcOffset(wall.AddHours(-6));
        }
        else if (zone.IsAmbiguousTime(wall))
        {
            offset = zone.GetAmbiguousTimeOffsets(wall).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(wall);
        }
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    public static DateTime ToWall(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified);
}

/// <summary>Resolve IDs de fuso (do Windows ou IANA), com o fuso local como último recurso.</summary>
public static class TimeZones
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static TimeZoneInfo Find(string? id, TimeZoneInfo fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (string.IsNullOrWhiteSpace(id) || id == fallback.Id)
        {
            return fallback;
        }
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }
        try
        {
            return Cache[id] = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return fallback;
        }
    }
}
