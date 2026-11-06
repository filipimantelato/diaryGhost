using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopHub.Core.Agenda;

namespace DesktopHub.App.Agenda;

/// <summary>
/// Criar/editar evento. Ao editar uma ocorrência de uma série, os campos mostram aquela ocorrência:
/// "Salvar" aplica a mudança de data/hora como deslocamento à série inteira; "Salvar só esta" tira a
/// ocorrência da série (exceção) e cria um evento avulso com os valores digitados.
/// </summary>
public partial class EventEditor : UserControl
{
    private static readonly CultureInfo PtBr = AgendaUi.PtBr;
    private readonly AgendaService _agenda;
    private readonly CalendarEvent _original;
    private readonly bool _isNew;
    private readonly DateTime? _occurrence;
    private List<Choice<RecurrenceRule?>> _presets = [];
    private readonly List<Choice<int?>> _reminder1 = AgendaUi.ReminderChoices();
    private readonly List<Choice<int?>> _reminder2 = AgendaUi.ReminderChoices("Sem segundo aviso");
    private bool _loading;

    /// <summary>Concluído (salvou, excluiu ou cancelou).</summary>
    public event Action? Done;

    internal EventEditor(AgendaService agenda, CalendarEvent value, bool isNew, DateTime? occurrence = null)
    {
        _agenda = agenda;
        _original = value;
        _isNew = isNew;
        _occurrence = value.Recurrence is null ? null : occurrence;
        InitializeComponent();
        Load();
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private DateTime ShownStart => _occurrence ?? _original.Start;

    private void Load()
    {
        _loading = true;
        var e = _original;
        HeaderText.Text = _isNew ? "Novo evento" : "Editar evento";
        TitleBox.Text = e.Title;
        AllDayBox.IsChecked = e.AllDay;
        DateBox.Text = AgendaUi.DateText(ShownStart);
        StartBox.Text = AgendaUi.TimeText(ShownStart.TimeOfDay);
        EndBox.Text = AgendaUi.TimeText((ShownStart + e.Duration).TimeOfDay);
        DaysBox.Text = Math.Max(1, (int)Math.Round(e.Duration.TotalDays)).ToString(PtBr);

        BuildPresets(ShownStart);
        RepeatBox.SelectedIndex = AgendaUi.MatchPreset(_presets, e.Recurrence);
        if (e.Recurrence is { } rule && RepeatBox.SelectedIndex == _presets.Count - 1)
        {
            CustomRuleBox.Text = (rule with { Count = null, Until = null }).ToString();
        }
        EndModeBox.ItemsSource = new[] { "Nunca termina", "Termina depois de", "Termina em" };
        EndModeBox.SelectedIndex = e.Recurrence?.Count is not null ? 1 : e.Recurrence?.Until is not null ? 2 : 0;
        EndValueBox.Text = e.Recurrence?.Count is { } count ? count.ToString(PtBr)
            : e.Recurrence?.Until is { } until ? AgendaUi.DateText(until) : "";

        var categories = AgendaUi.CategoryChoices(_agenda);
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedIndex = Math.Max(0, categories.FindIndex(c => c.Value == e.CategoryId));

        Reminder1Box.ItemsSource = _reminder1;
        Reminder2Box.ItemsSource = _reminder2;
        Reminder1Box.SelectedIndex = AgendaUi.ReminderIndex(_reminder1, e.ReminderMinutes.Count > 0 ? e.ReminderMinutes[0] : null);
        Reminder2Box.SelectedIndex = AgendaUi.ReminderIndex(_reminder2, e.ReminderMinutes.Count > 1 ? e.ReminderMinutes[1] : null);

        LocationBox.Text = e.Location ?? "";
        NotesBox.Text = e.Notes ?? "";
        DeleteButton.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;
        SaveOneButton.Visibility = _occurrence is not null ? Visibility.Visible : Visibility.Collapsed;
        SeriesHint.Visibility = _occurrence is not null ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
        UpdateVisibility();
    }

    private void BuildPresets(DateTime start)
    {
        var previous = RepeatBox.SelectedItem as Choice<RecurrenceRule?>;
        var previousIndex = RepeatBox.SelectedIndex;
        _presets = AgendaUi.RecurrencePresets(start);
        RepeatBox.ItemsSource = _presets;
        if (previous is not null)
        {
            // Mantém o tipo de repetição escolhido quando a data muda (ex.: "toda semana" segue o novo dia).
            RepeatBox.SelectedIndex = previous.Label == AgendaUi.CustomRuleLabel ? _presets.Count - 1 : Math.Min(previousIndex, _presets.Count - 2);
        }
    }

    private void UpdateVisibility()
    {
        var allDay = AllDayBox.IsChecked == true;
        StartPanel.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        EndPanel.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        DaysPanel.Visibility = allDay ? Visibility.Visible : Visibility.Collapsed;
        AllDayReminderHint.Visibility = allDay ? Visibility.Visible : Visibility.Collapsed;
        var custom = RepeatBox.SelectedItem is Choice<RecurrenceRule?> { Label: AgendaUi.CustomRuleLabel };
        CustomRuleBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var repeats = RepeatBox.SelectedIndex > 0;
        EndRow.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
        EndValueBox.Visibility = EndModeBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        EndValueBox.ToolTip = EndModeBox.SelectedIndex == 1 ? "Número de vezes (1–999)" : "Data final (dd/mm/aaaa), inclusive";
    }

    private void AllDay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            UpdateVisibility();
        }
    }

    private void Repeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateVisibility();
        }
    }

    private void EndMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateVisibility();
        }
    }

    private void Date_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AgendaUi.TryParseDate(DateBox.Text, _agenda.LocalNow, out var date))
        {
            DateBox.Text = AgendaUi.DateText(date);
            BuildPresets(date);
        }
    }

    /// <summary>Monta o evento a partir dos campos; null com mensagem quando algo é inválido.</summary>
    private CalendarEvent? Build(out string? error)
    {
        error = null;
        var today = _agenda.LocalNow;
        if (!AgendaUi.TryParseDate(DateBox.Text, today, out var date))
        {
            error = "Data inválida. Use dd/mm/aaaa.";
            return null;
        }
        var allDay = AllDayBox.IsChecked == true;
        DateTime start;
        TimeSpan duration;
        if (allDay)
        {
            if (!int.TryParse(DaysBox.Text.Trim(), NumberStyles.None, PtBr, out var days) || days is < 1 or > 31)
            {
                error = "Duração em dias deve ser de 1 a 31.";
                return null;
            }
            start = date.Date;
            duration = TimeSpan.FromDays(days);
        }
        else
        {
            if (!AgendaUi.TryParseTime(StartBox.Text, out var startTime) || !AgendaUi.TryParseTime(EndBox.Text, out var endTime))
            {
                error = "Horário inválido. Use hh:mm (ex.: 14:30).";
                return null;
            }
            start = date.Date + startTime;
            // Fim antes do início = termina no dia seguinte (ex.: 22:00–01:00).
            duration = endTime >= startTime ? endTime - startTime : endTime + TimeSpan.FromDays(1) - startTime;
        }

        RecurrenceRule? rule = null;
        if (RepeatBox.SelectedItem is Choice<RecurrenceRule?> choice && RepeatBox.SelectedIndex > 0)
        {
            if (choice.Label == AgendaUi.CustomRuleLabel)
            {
                if (!RecurrenceRule.TryParse(CustomRuleBox.Text, out rule, out var ruleError))
                {
                    error = $"Regra inválida: {ruleError}";
                    return null;
                }
            }
            else
            {
                rule = choice.Value;
            }
            switch (EndModeBox.SelectedIndex)
            {
                case 1 when int.TryParse(EndValueBox.Text.Trim(), NumberStyles.None, PtBr, out var count) && count is >= 1 and <= 999:
                    rule = rule! with { Count = count, Until = null };
                    break;
                case 1:
                    error = "Número de vezes deve ser de 1 a 999.";
                    return null;
                case 2 when AgendaUi.TryParseDate(EndValueBox.Text, today, out var until) && until.Date >= date.Date:
                    rule = rule! with { Count = null, Until = until.Date.AddDays(1).AddSeconds(-1) };
                    break;
                case 2:
                    error = "Data final inválida ou anterior ao início.";
                    return null;
                default:
                    rule = rule! with { Count = null, Until = null };
                    break;
            }
        }

        int?[] reminders = [(Reminder1Box.SelectedItem as Choice<int?>)?.Value, (Reminder2Box.SelectedItem as Choice<int?>)?.Value];
        var result = _original with
        {
            Title = TitleBox.Text,
            AllDay = allDay,
            Start = start,
            Duration = duration,
            Recurrence = rule,
            CategoryId = (CategoryBox.SelectedItem as Choice<Guid?>)?.Value,
            ReminderMinutes = [.. reminders.Where(m => m is not null).Select(m => m!.Value).Distinct()],
            Location = LocationBox.Text,
            Notes = NotesBox.Text,
        };
        error = AgendaService.Validate(result);
        return error is null ? result : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Build(out var error) is not { } value)
        {
            ShowError(error);
            return;
        }
        if (_occurrence is { } occurrence)
        {
            // Série: aplica o deslocamento da ocorrência editada ao início da série.
            var delta = value.Start - occurrence;
            value = value with { Start = _original.Start + delta, Exceptions = [.. _original.Exceptions.Select(x => x + delta)] };
        }
        _agenda.SaveEvent(value);
        Done?.Invoke();
    }

    private void SaveOne_Click(object sender, RoutedEventArgs e)
    {
        if (_occurrence is not { } occurrence || Build(out var error) is not { } value)
        {
            ShowError("Confira os campos.");
            return;
        }
        _agenda.DeleteOccurrence(_original.Id, occurrence);
        _agenda.SaveEvent(value with
        {
            Id = Guid.CreateVersion7(_agenda.Now),
            Recurrence = null,
            Exceptions = [],
            CreatedAt = default,
        });
        Done?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Done?.Invoke();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_original.Recurrence is null || _occurrence is null)
        {
            _agenda.DeleteEvent(_original.Id);
            Done?.Invoke();
            return;
        }
        var occurrence = _occurrence.Value;
        var menu = new ContextMenu { PlacementTarget = DeleteButton, Placement = PlacementMode.Top };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                action();
                Done?.Invoke();
            };
            menu.Items.Add(item);
        }
        Add("Excluir só esta ocorrência", () => _agenda.DeleteOccurrence(_original.Id, occurrence));
        Add("Excluir esta e as próximas", () => _agenda.EndSeriesBefore(_original.Id, occurrence));
        Add("Excluir a série inteira", () => _agenda.DeleteEvent(_original.Id));
        menu.IsOpen = true;
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }
}
