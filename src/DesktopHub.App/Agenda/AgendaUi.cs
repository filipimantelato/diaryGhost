using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopHub.Core.Agenda;

namespace DesktopHub.App.Agenda;

/// <summary>Item de lista suspensa: o texto exibido e o valor por trás.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>DataContext de um botão com o estilo Chip (destaca o selecionado).</summary>
public sealed record ChipState(bool IsSelected);

/// <summary>Utilidades de interface da agenda: cores, entrada de data/hora e opções de recorrência e aviso.</summary>
internal static class AgendaUi
{
    public static readonly CultureInfo PtBr = AgendaFormat.PtBr;
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    public static readonly string[] CategoryPalette = ["#C9A94E", "#6B9AC4", "#8FB573", "#C47A6B", "#9A86C4", "#5FB3A8", "#C48FB0", "#8A8A8A"];

    public static SolidColorBrush ColorBrush(string? hex, byte alpha = 255)
    {
        var key = $"{hex}/{alpha}";
        if (Brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex ?? "#8A8A8A");
        }
        catch (FormatException)
        {
            color = Color.FromRgb(0x8A, 0x8A, 0x8A);
        }
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return Brushes[key] = brush;
    }

    public static SolidColorBrush CategoryBrush(AgendaService agenda, Guid? categoryId, byte alpha = 255) =>
        ColorBrush(agenda.Category(categoryId)?.Color ?? "#8A8A8A", alpha);

    // ---------- Entrada ----------

    /// <summary>Aceita "dd/mm/aaaa", "d/m" (ano atual), "hoje", "amanhã". Datas inválidas → false.</summary>
    public static bool TryParseDate(string? text, DateTime today, out DateTime date)
    {
        date = default;
        var t = (text ?? "").Trim().ToLower(PtBr);
        switch (t)
        {
            case "hoje":
                date = today.Date;
                return true;
            case "amanhã" or "amanha":
                date = today.Date.AddDays(1);
                return true;
        }
        if (DateTime.TryParseExact(t, ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "yyyy-M-d"], PtBr, DateTimeStyles.None, out date))
        {
            return date.Year is >= 1900 and <= 2200;
        }
        if (DateTime.TryParseExact(t, ["d/M", "d-M"], PtBr, DateTimeStyles.None, out var noYear))
        {
            date = new DateTime(today.Year, noYear.Month, noYear.Day);
            return true;
        }
        return false;
    }

    /// <summary>Aceita "14:30", "14h30", "14h", "9" e "1430".</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var t = (text ?? "").Trim().ToLower(PtBr).Replace('h', ':').Replace('.', ':');
        if (t.EndsWith(':'))
        {
            t += "00";
        }
        if (t.Length is 3 or 4 && t.All(char.IsDigit))
        {
            t = t[..^2] + ":" + t[^2..];
        }
        if (int.TryParse(t, NumberStyles.None, PtBr, out var hour) && hour is >= 0 and <= 23)
        {
            time = TimeSpan.FromHours(hour);
            return true;
        }
        if (TimeSpan.TryParseExact(t, ["h\\:mm", "hh\\:mm"], PtBr, out time))
        {
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }
        return false;
    }

    public static string DateText(DateTime d) => d.ToString("dd/MM/yyyy", PtBr);

    public static string TimeText(TimeSpan t) => DateTime.Today.Add(t).ToString("HH:mm", PtBr);

    // ---------- Opções ----------

    public const string CustomRuleLabel = "Personalizado (RRULE)…";

    /// <summary>Recorrências comuns para uma data de início (como nos calendários conhecidos).</summary>
    public static List<Choice<RecurrenceRule?>> RecurrencePresets(DateTime start)
    {
        var weekday = PtBr.DateTimeFormat.GetDayName(start.DayOfWeek);
        var (ordinal, isLast) = AgendaFormat.WeekOfMonth(start);
        var list = new List<Choice<RecurrenceRule?>>
        {
            new("Não se repete", null),
            new("Todo dia", RecurrenceRule.Daily()),
            new("Todo dia útil (seg. a sex.)", RecurrenceRule.Weekly(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday) with { Frequency = RecurrenceFrequency.Daily }),
            new($"Toda semana na {weekday}", RecurrenceRule.Weekly(start.DayOfWeek)),
            new($"A cada 2 semanas na {weekday}", RecurrenceRule.Weekly(start.DayOfWeek) with { Interval = 2 }),
            new($"Todo mês no dia {start.Day}", RecurrenceRule.Monthly()),
        };
        if (ordinal <= 4)
        {
            var rule = RecurrenceRule.Monthly() with { ByDay = [new WeekdayNum(start.DayOfWeek, ordinal)] };
            list.Add(new(AgendaFormat.Describe(rule, start), rule));
        }
        if (isLast)
        {
            var rule = RecurrenceRule.Monthly() with { ByDay = [new WeekdayNum(start.DayOfWeek, -1)] };
            list.Add(new(AgendaFormat.Describe(rule, start), rule));
        }
        if (start.Day == DateTime.DaysInMonth(start.Year, start.Month))
        {
            list.Add(new("Todo mês no último dia", RecurrenceRule.Monthly() with { ByMonthDay = [-1] }));
        }
        list.Add(new($"Todo ano em {start.ToString("d 'de' MMMM", PtBr)}", RecurrenceRule.Yearly()));
        list.Add(new(CustomRuleLabel, null));
        return list;
    }

    /// <summary>Índice do preset equivalente à regra (ignorando fim da série), ou o "Personalizado".</summary>
    public static int MatchPreset(List<Choice<RecurrenceRule?>> presets, RecurrenceRule? rule)
    {
        if (rule is null)
        {
            return 0;
        }
        var core = (rule with { Count = null, Until = null }).ToString();
        var index = presets.FindIndex(p => p.Value is { } v && v.ToString() == core);
        return index >= 0 ? index : presets.Count - 1;
    }

    public static readonly int[] ReminderOptionsMinutes = [0, 5, 10, 15, 30, 60, 120, 24 * 60, 2 * 24 * 60, 7 * 24 * 60];

    public static List<Choice<int?>> ReminderChoices(string none = "Sem aviso") =>
        [new(none, null), .. ReminderOptionsMinutes.Select(m => new Choice<int?>(AgendaFormat.ReminderOffset(m), m))];

    public static int ReminderIndex(List<Choice<int?>> choices, int? minutes)
    {
        var index = choices.FindIndex(c => c.Value == minutes);
        if (index < 0 && minutes is { } m)
        {
            choices.Add(new(AgendaFormat.ReminderOffset(m), m)); // valor fora da lista (ex.: importado)
            return choices.Count - 1;
        }
        return Math.Max(0, index);
    }

    public static List<Choice<TaskPriority>> PriorityChoices() =>
        [.. Enum.GetValues<TaskPriority>().Select(p => new Choice<TaskPriority>(AgendaFormat.Priority(p), p))];

    public static List<Choice<Guid?>> CategoryChoices(AgendaService agenda) =>
        [new("Sem categoria", null), .. agenda.Categories.Select(c => new Choice<Guid?>(c.Name, c.Id))];

    public static Brush PriorityBrush(TaskPriority p) => p switch
    {
        TaskPriority.Urgent => (Brush)Application.Current.FindResource("DangerBrush"),
        TaskPriority.High => (Brush)Application.Current.FindResource("WarningBrush"),
        TaskPriority.Low => (Brush)Application.Current.FindResource("TextTertiaryBrush"),
        _ => (Brush)Application.Current.FindResource("TextSecondaryBrush"),
    };

    // ---------- Construção de elementos ----------

    public static TextBlock Text(string text, string style = "BodyText", double? size = null, Brush? foreground = null)
    {
        var block = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource(style), TextTrimming = TextTrimming.CharacterEllipsis };
        if (size is { } s)
        {
            block.FontSize = s;
        }
        if (foreground is not null)
        {
            block.Foreground = foreground;
        }
        return block;
    }

    public static Button IconButton(string glyph, string name, RoutedEventHandler click, object? tag = null, double size = 28)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Content = glyph,
            Width = size,
            Height = size,
            FontSize = 12,
            ToolTip = name,
            Tag = tag,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += click;
        return button;
    }

    /// <summary>Período do dia como texto: "09:00–10:00", "Dia inteiro", "até 11:00" (vindo do dia anterior).</summary>
    public static string TimeRange(EventOccurrence o, DateTime day, TimeZoneInfo zone)
    {
        if (o.Event.AllDay)
        {
            return "Dia inteiro";
        }
        var start = ZonedTime.ToWall(o.StartUtc, zone);
        var end = ZonedTime.ToWall(o.EndUtc, zone);
        if (start.Date < day.Date && end.Date > day.Date)
        {
            return "Dia inteiro";
        }
        if (start.Date < day.Date)
        {
            return $"até {AgendaFormat.Time(end)}";
        }
        if (o.EndUtc == o.StartUtc)
        {
            return AgendaFormat.Time(start);
        }
        return end.Date > day.Date ? $"{AgendaFormat.Time(start)}–…" : $"{AgendaFormat.Time(start)}–{AgendaFormat.Time(end)}";
    }
}
