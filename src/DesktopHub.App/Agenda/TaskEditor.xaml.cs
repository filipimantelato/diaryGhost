using System.Windows;
using System.Windows.Controls;
using DesktopHub.Core.Agenda;

namespace DesktopHub.App.Agenda;

/// <summary>Criar/editar tarefa (prazo opcional, prioridade, categoria, aviso e repetição).</summary>
public partial class TaskEditor : UserControl
{
    private readonly AgendaService _agenda;
    private readonly TaskItem _original;
    private readonly bool _isNew;
    private readonly List<Choice<int?>> _reminders = AgendaUi.ReminderChoices();
    private List<Choice<RecurrenceRule?>> _presets = [];

    public event Action? Done;

    internal TaskEditor(AgendaService agenda, TaskItem value, bool isNew)
    {
        _agenda = agenda;
        _original = value;
        _isNew = isNew;
        InitializeComponent();
        Load();
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.CaretIndex = TitleBox.Text.Length;
        };
    }

    private void Load()
    {
        var t = _original;
        HeaderText.Text = _isNew ? "Nova tarefa" : "Editar tarefa";
        TitleBox.Text = t.Title;
        DateBox.Text = t.Due is { } due ? AgendaUi.DateText(due) : "";
        TimeBox.Text = t.Due is { } d && t.DueHasTime ? AgendaUi.TimeText(d.TimeOfDay) : "";
        var priorities = AgendaUi.PriorityChoices();
        PriorityBox.ItemsSource = priorities;
        PriorityBox.SelectedIndex = priorities.FindIndex(p => p.Value == t.Priority);
        var categories = AgendaUi.CategoryChoices(_agenda);
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedIndex = Math.Max(0, categories.FindIndex(c => c.Value == t.CategoryId));
        ReminderBox.ItemsSource = _reminders;
        ReminderBox.SelectedIndex = AgendaUi.ReminderIndex(_reminders, t.ReminderMinutes);
        BuildPresets(t.Due ?? _agenda.LocalNow.Date);
        RepeatBox.SelectedIndex = AgendaUi.MatchPreset(_presets, t.Recurrence);
        if (t.Recurrence is { } rule && RepeatBox.SelectedIndex == _presets.Count - 1)
        {
            CustomRuleBox.Text = rule.ToString();
        }
        NotesBox.Text = t.Notes ?? "";
        DeleteButton.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;
        UpdateVisibility();
    }

    private void BuildPresets(DateTime date)
    {
        var index = RepeatBox.SelectedIndex;
        _presets = AgendaUi.RecurrencePresets(date);
        RepeatBox.ItemsSource = _presets;
        if (index >= 0)
        {
            RepeatBox.SelectedIndex = Math.Min(index, _presets.Count - 1);
        }
    }

    private void UpdateVisibility()
    {
        CustomRuleBox.Visibility = RepeatBox.SelectedItem is Choice<RecurrenceRule?> { Label: AgendaUi.CustomRuleLabel } ? Visibility.Visible : Visibility.Collapsed;
        RepeatHint.Visibility = RepeatBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Repeat_Changed(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void Date_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AgendaUi.TryParseDate(DateBox.Text, _agenda.LocalNow, out var date))
        {
            DateBox.Text = AgendaUi.DateText(date);
            BuildPresets(date);
        }
    }

    private void QuickDate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }
        if (tag == "none")
        {
            DateBox.Text = "";
            TimeBox.Text = "";
            ReminderBox.SelectedIndex = 0;
            RepeatBox.SelectedIndex = 0;
            return;
        }
        var date = _agenda.LocalNow.Date.AddDays(int.Parse(tag, AgendaUi.PtBr));
        DateBox.Text = AgendaUi.DateText(date);
        BuildPresets(date);
    }

    private TaskItem? Build(out string? error)
    {
        error = null;
        DateTime? due = null;
        var hasTime = false;
        if (!string.IsNullOrWhiteSpace(DateBox.Text))
        {
            if (!AgendaUi.TryParseDate(DateBox.Text, _agenda.LocalNow, out var date))
            {
                error = "Data inválida. Use dd/mm/aaaa.";
                return null;
            }
            due = date.Date;
            if (!string.IsNullOrWhiteSpace(TimeBox.Text))
            {
                if (!AgendaUi.TryParseTime(TimeBox.Text, out var time))
                {
                    error = "Hora inválida. Use hh:mm.";
                    return null;
                }
                due = date.Date + time;
                hasTime = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(TimeBox.Text))
        {
            error = "Informe a data do prazo para usar uma hora.";
            return null;
        }

        RecurrenceRule? rule = null;
        if (RepeatBox.SelectedIndex > 0 && RepeatBox.SelectedItem is Choice<RecurrenceRule?> choice)
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
        }

        var result = _original with
        {
            Title = TitleBox.Text,
            Due = due,
            DueHasTime = hasTime,
            Priority = (PriorityBox.SelectedItem as Choice<TaskPriority>)?.Value ?? TaskPriority.Normal,
            CategoryId = (CategoryBox.SelectedItem as Choice<Guid?>)?.Value,
            ReminderMinutes = (ReminderBox.SelectedItem as Choice<int?>)?.Value,
            Recurrence = rule,
            // Mudou a regra ou o prazo: a série recomeça a partir do novo prazo.
            RecurrenceStart = rule is not null && rule.ToString() == _original.Recurrence?.ToString() && due == _original.Due ? _original.RecurrenceStart : null,
            TimeZoneId = hasTime ? _agenda.LocalZone.Id : _original.TimeZoneId,
            Notes = NotesBox.Text,
        };
        error = AgendaService.Validate(result);
        return error is null ? result : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Build(out var error) is not { } value)
        {
            ErrorText.Text = error ?? "";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _agenda.SaveTask(value);
        Done?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Done?.Invoke();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        _agenda.DeleteTask(_original.Id);
        Done?.Invoke();
    }
}
