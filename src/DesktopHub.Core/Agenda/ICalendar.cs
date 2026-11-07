using System.Globalization;
using System.Text;
using System.Xml;

namespace DesktopHub.Core.Agenda;

/// <summary>Resultado da leitura de um .ics: eventos prontos para o modelo do app e avisos sobre o que foi simplificado.</summary>
public sealed record IcsImport(IReadOnlyList<CalendarEvent> Events, IReadOnlyList<string> Warnings, string? CalendarName);

/// <summary>
/// Leitura e escrita de iCalendar (RFC 5545), só de VEVENT. Datas em UTC viram horário de parede no fuso
/// local; TZID conhecido pelo Windows (IANA ou nome do Windows) é mantido; TZID desconhecido cai no fuso local
/// (e gera um aviso). RRULE fora do subconjunto suportado importa só a primeira ocorrência (com aviso).
/// Ocorrências alteradas (RECURRENCE-ID) viram exceção na série + evento avulso; canceladas são removidas.
/// </summary>
public static class ICalendar
{
    public const int MaxEvents = 20_000;
    public const int MaxBytes = 10 * 1024 * 1024;

    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    // ---------- Leitura ----------

    /// <param name="keepAfter">Eventos que terminaram antes disto (e séries encerradas antes disto) são ignorados.</param>
    public static IcsImport Parse(string text, TimeZoneInfo localZone, DateTimeOffset now, DateTimeOffset? keepAfter = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(localZone);
        if (text.Length > MaxBytes)
        {
            throw new FormatException("Arquivo de agenda grande demais (máx. 10 MB).");
        }
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("O conteúdo não é um calendário iCalendar (.ics).");
        }
        var warnings = new List<string>();
        var masters = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
        var singles = new List<CalendarEvent>();
        var overrides = new List<(string Uid, DateTime RecurrenceId, CalendarEvent? Replacement)>();
        string? calendarName = null;
        List<Prop>? current = null;
        var depth = 0;

        foreach (var line in Unfold(text))
        {
            var prop = ParseLine(line);
            if (prop is null)
            {
                continue;
            }
            if (prop.Name == "BEGIN")
            {
                if (prop.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && current is null)
                {
                    current = [];
                    depth = 0;
                }
                else if (current is not null)
                {
                    depth++; // VALARM etc. dentro do evento: ignorados
                }
                continue;
            }
            if (prop.Name == "END" && current is not null)
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                if (masters.Count + singles.Count >= MaxEvents)
                {
                    warnings.Add($"Só os primeiros {MaxEvents} eventos foram importados.");
                    break;
                }
                ReadEvent(current, localZone, now, warnings, masters, singles, overrides);
                current = null;
                continue;
            }
            if (current is not null && depth == 0)
            {
                current.Add(prop);
            }
            else if (current is null && prop.Name == "X-WR-CALNAME")
            {
                calendarName = Clip(Unescape(prop.Value), 80);
            }
        }

        foreach (var (uid, recurrenceId, replacement) in overrides)
        {
            if (masters.TryGetValue(uid, out var master))
            {
                masters[uid] = master with { Exceptions = [.. master.Exceptions.Append(recurrenceId).Distinct()] };
            }
            if (replacement is not null)
            {
                singles.Add(replacement);
            }
        }

        var cutoff = keepAfter ?? DateTimeOffset.MinValue;
        var events = masters.Values.Concat(singles)
            .Where(e => IsRelevant(e, localZone, cutoff))
            .ToList();
        return new IcsImport(events, warnings.Distinct().Take(20).ToList(), calendarName);
    }

    private static bool IsRelevant(CalendarEvent e, TimeZoneInfo localZone, DateTimeOffset cutoff)
    {
        if (cutoff == DateTimeOffset.MinValue)
        {
            return true;
        }
        var zone = e.AllDay ? localZone : TimeZones.Find(e.TimeZoneId, localZone);
        if (e.Recurrence is { } rule)
        {
            if (rule.Until is { } until)
            {
                var untilUtc = until.Kind == DateTimeKind.Utc ? new DateTimeOffset(until, TimeSpan.Zero) : ZonedTime.ToUtc(until, zone);
                return untilUtc >= cutoff;
            }
            return true; // COUNT ou sem fim: barato manter
        }
        return ZonedTime.ToUtc(e.Start + e.Duration, zone) >= cutoff;
    }

    private static void ReadEvent(List<Prop> props, TimeZoneInfo localZone, DateTimeOffset now, List<string> warnings,
        Dictionary<string, CalendarEvent> masters, List<CalendarEvent> singles, List<(string, DateTime, CalendarEvent?)> overrides)
    {
        Prop? Get(string name) => props.FirstOrDefault(p => p.Name == name);
        var uid = Get("UID")?.Value.Trim();
        var startProp = Get("DTSTART");
        if (startProp is null || !TryParseTime(startProp, localZone, out var start, out var zoneId, out var allDay, warnings))
        {
            return;
        }
        var cancelled = Get("STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;

        TimeSpan duration;
        if (Get("DTEND") is { } endProp && TryParseTime(endProp, localZone, out var end, out var endZone, out _, warnings))
        {
            // Fim noutro fuso: converte para o fuso do início antes de subtrair.
            if (endZone != zoneId)
            {
                var endUtc = ZonedTime.ToUtc(end, TimeZones.Find(endZone, localZone));
                end = ZonedTime.ToWall(endUtc, TimeZones.Find(zoneId, localZone));
            }
            duration = end - start;
        }
        else if (Get("DURATION") is { } durationProp && TryParseDuration(durationProp.Value, out var d))
        {
            duration = d;
        }
        else
        {
            duration = allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
        }
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(31))
        {
            duration = allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        }
        if (allDay && duration < TimeSpan.FromDays(1))
        {
            duration = TimeSpan.FromDays(1);
        }

        RecurrenceRule? rule = null;
        var notes = Clip(Unescape(Get("DESCRIPTION")?.Value), 4000);
        if (Get("RRULE") is { } rrule)
        {
            if (RecurrenceRule.TryParse(rrule.Value, out var parsed, out var error))
            {
                rule = parsed;
            }
            else
            {
                warnings.Add($"Repetição não suportada importada como evento único: {error}");
            }
        }

        var exceptions = new List<DateTime>();
        foreach (var ex in props.Where(p => p.Name == "EXDATE"))
        {
            foreach (var part in ex.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseTime(ex with { Value = part }, localZone, out var exStart, out var exZone, out _, warnings))
                {
                    exceptions.Add(exZone == zoneId ? exStart : ZonedTime.ToWall(ZonedTime.ToUtc(exStart, TimeZones.Find(exZone, localZone)), TimeZones.Find(zoneId, localZone)));
                }
            }
        }

        var reminders = new List<int>();
        var title = Clip(Unescape(Get("SUMMARY")?.Value), 200) ?? "(sem título)";
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(now),
            Title = title,
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            Duration = duration,
            AllDay = allDay,
            TimeZoneId = zoneId,
            Recurrence = rule,
            Exceptions = exceptions,
            Location = Clip(Unescape(Get("LOCATION")?.Value), 200),
            Notes = notes,
            ReminderMinutes = reminders,
            CreatedAt = now,
            UpdatedAt = now,
            ExternalUid = uid is null ? null : Clip(uid, 300),
        };

        if (Get("RECURRENCE-ID") is { } recurrence && uid is not null &&
            TryParseTime(recurrence, localZone, out var recurrenceStart, out var recurrenceZone, out _, warnings))
        {
            overrides.Add((uid, recurrenceStart, cancelled ? null : e with { Recurrence = null, Exceptions = [] }));
            _ = recurrenceZone;
            return;
        }
        if (cancelled)
        {
            return;
        }
        if (uid is not null && rule is not null)
        {
            masters[uid] = e;
        }
        else if (uid is not null && !masters.ContainsKey(uid) && singles.All(s => s.ExternalUid != uid))
        {
            singles.Add(e);
        }
        else if (uid is null)
        {
            singles.Add(e);
        }
    }

    /// <summary>DATE (dia inteiro), DATE-TIME UTC ("Z"), com TZID, ou flutuante.</summary>
    private static bool TryParseTime(Prop p, TimeZoneInfo localZone, out DateTime wall, out string zoneId, out bool allDay, List<string> warnings)
    {
        wall = default;
        zoneId = localZone.Id;
        allDay = false;
        var value = p.Value.Trim();
        if ((p.Params.TryGetValue("VALUE", out var type) && type.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || value.Length == 8)
        {
            if (!DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall))
            {
                return false;
            }
            allDay = true;
            return true;
        }
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(utc ? value[..^1] : value, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out wall))
        {
            return false;
        }
        if (utc)
        {
            wall = ZonedTime.ToWall(new DateTimeOffset(wall, TimeSpan.Zero), localZone);
            return true;
        }
        if (p.Params.TryGetValue("TZID", out var tzid))
        {
            tzid = tzid.Trim('"').TrimStart('/');
            if (TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
            {
                zoneId = zone.Id;
            }
            else
            {
                warnings.Add($"Fuso \"{tzid}\" desconhecido: usado o fuso do Windows.");
            }
        }
        return true;
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        try
        {
            duration = XmlConvert.ToTimeSpan(text.Trim());
            return true;
        }
        catch (FormatException)
        {
            duration = default;
            return false;
        }
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0)
            {
                yield return sb.ToString();
            }
            sb.Clear().Append(raw.TrimEnd('\r'));
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static Prop? ParseLine(string line)
    {
        var inQuotes = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i] == ':' && !inQuotes)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
        {
            return null;
        }
        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
            }
        }
        return new Prop(head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    private static string? Unescape(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    private static string? Clip(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }

    // ---------- Escrita ----------

    public static string Write(IEnumerable<CalendarEvent> events, TimeZoneInfo localZone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var sb = new StringBuilder();
        void Line(string text) => Fold(sb, text);
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Diary Ghost//Agenda local//PT-BR");
        Line("CALSCALE:GREGORIAN");
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in events)
        {
            var zone = e.AllDay ? localZone : TimeZones.Find(e.TimeZoneId, localZone);
            Line("BEGIN:VEVENT");
            Line("UID:" + Escape(e.ExternalUid ?? $"{e.Id:N}@diaryghost"));
            Line("DTSTAMP:" + stamp);
            Line("SUMMARY:" + Escape(e.Title));
            if (e.AllDay)
            {
                Line("DTSTART;VALUE=DATE:" + e.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                Line("DTEND;VALUE=DATE:" + e.Start.Date.AddDays(Math.Max(1, (int)Math.Round(e.Duration.TotalDays))).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else
            {
                var tzid = Iana(zone);
                Line(Time("DTSTART", e.Start, zone, tzid));
                Line(Time("DTEND", e.Start + e.Duration, zone, tzid));
            }
            if (e.Recurrence is { } rule)
            {
                // Com DTSTART em fuso, o RFC exige UNTIL em UTC.
                var exported = rule.Until is { Kind: not DateTimeKind.Utc } until && !e.AllDay
                    ? rule with { Until = DateTime.SpecifyKind(ZonedTime.ToUtc(until, zone).UtcDateTime, DateTimeKind.Utc) }
                    : rule;
                Line("RRULE:" + exported);
                foreach (var ex in e.Exceptions)
                {
                    Line(e.AllDay ? "EXDATE;VALUE=DATE:" + ex.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : Time("EXDATE", ex, zone, Iana(zone)));
                }
            }
            if (e.Location is { } location)
            {
                Line("LOCATION:" + Escape(location));
            }
            if (e.Notes is { } notes)
            {
                Line("DESCRIPTION:" + Escape(notes));
            }
            foreach (var minutes in e.ReminderMinutes)
            {
                Line("BEGIN:VALARM");
                Line("ACTION:DISPLAY");
                Line("DESCRIPTION:" + Escape(e.Title));
                Line($"TRIGGER:-PT{minutes}M");
                Line("END:VALARM");
            }
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string? Iana(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;

    private static string Time(string name, DateTime wall, TimeZoneInfo zone, string? tzid) => tzid is not null
        ? $"{name};TZID={tzid}:{wall.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}"
        : $"{name}:{ZonedTime.ToUtc(wall, zone).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Linhas de até 75 octetos (UTF-8), continuação com espaço — sem partir caracteres.</summary>
    private static void Fold(StringBuilder sb, string line)
    {
        var bytes = 0;
        var limit = 75;
        foreach (var c in line)
        {
            var size = Encoding.UTF8.GetByteCount([c]);
            if (bytes + size > limit)
            {
                sb.Append("\r\n ");
                bytes = 1;
                limit = 75;
            }
            sb.Append(c);
            bytes += size;
        }
        sb.Append("\r\n");
    }
}
