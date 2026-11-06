using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;

namespace DesktopHub.App.Main.Pages;

/// <summary>Criação rápida de lembretes (uma tarefa com prazo e aviso na hora), próximos avisos e histórico.</summary>
public partial class RemindersPage : UserControl, IDisposable
{
    private readonly AgendaService _agenda;
    private readonly ReminderScheduler _reminders;
    private readonly NotificationCenter _notifications;
    private readonly List<IDisposable> _subscriptions = [];

    internal RemindersPage(AgendaService agenda, ReminderScheduler reminders, NotificationCenter notifications, IEventBus bus)
    {
        _agenda = agenda;
        _reminders = reminders;
        _notifications = notifications;
        InitializeComponent();
        TitleBox.TextChanged += (_, _) => TitleHint.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<AgendaChanged>(_ => Render(), ui));
        _subscriptions.Add(bus.Subscribe<RemindersChanged>(_ => Render(), ui));
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Render()
    {
        var status = _notifications.ChannelStatus;
        ChannelWarning.Visibility = status is NotificationChannelStatus.Enabled or NotificationChannelStatus.Unknown && !_notifications.IsPaused ? Visibility.Collapsed : Visibility.Visible;
        ChannelWarningText.Text = _notifications.IsPaused
            ? "As notificações do Diary Ghost estão pausadas: os lembretes ficam registrados abaixo como silenciados. Reative pela bandeja ou em Configurações."
            : status == NotificationChannelStatus.DisabledByUser
                ? "As notificações do Diary Ghost estão desativadas no Windows (Configurações > Sistema > Notificações). Os lembretes ficam registrados abaixo, mas não aparecem na tela."
                : "As notificações do Windows não estão disponíveis neste momento.";

        var now = _agenda.LocalNow;
        UpcomingList.Children.Clear();
        var upcoming = _reminders.Upcoming(TimeSpan.FromDays(7));
        foreach (var t in upcoming.Take(50))
        {
            var fire = ZonedTime.ToWall(t.FireAt, _agenda.LocalZone);
            var anchor = ZonedTime.ToWall(t.Anchor, _agenda.LocalZone);
            var detail = t.Snoozed ? "adiado" : t.Source == ReminderSource.Event
                ? (t.AllDay ? $"evento de dia inteiro, {AgendaFormat.Day(anchor, now)}" : $"evento às {AgendaFormat.Time(anchor)}{(anchor.Date != fire.Date ? $" de {AgendaFormat.Day(anchor, now)}" : "")}")
                : t.MinutesBefore == 0 ? "lembrete" : $"tarefa com prazo às {AgendaFormat.Time(anchor)}";
            var row = Row($"{AgendaFormat.Capitalize(AgendaFormat.Day(fire, now))} {AgendaFormat.Time(fire)}", t.Title, detail, null);
            if (t.Snoozed)
            {
                row.Children.Add(Actions(AgendaUi.IconButton("", "Dispensar", (_, _) => _reminders.Dismiss(t.Key))));
            }
            UpcomingList.Children.Add(row);
        }
        if (upcoming.Count == 0)
        {
            UpcomingList.Children.Add(AgendaUi.Text("Nenhum aviso nos próximos 7 dias. Avisos são definidos em cada evento ou tarefa.", "CaptionText"));
        }

        RecentList.Children.Clear();
        var recent = _reminders.Recent(30).Where(r => r.State != ReminderState.Snoozed).ToList();
        foreach (var entry in recent)
        {
            var when = ZonedTime.ToWall(entry.UpdatedAt, _agenda.LocalZone);
            var (state, brush) = Describe(entry);
            var row = Row($"{AgendaFormat.Capitalize(AgendaFormat.Day(when, now))} {AgendaFormat.Time(when)}", entry.Title, state, brush);
            if (entry.State == ReminderState.Fired && entry.Anchor > _agenda.Now - TimeSpan.FromHours(12))
            {
                var snooze = AgendaUi.IconButton("", "Adiar", (s, _) => ShowSnooze((Button)s, entry.Key));
                row.Children.Add(Actions(snooze));
            }
            RecentList.Children.Add(row);
        }
        if (recent.Count == 0)
        {
            RecentList.Children.Add(AgendaUi.Text("Nenhum aviso entregue ainda.", "CaptionText"));
        }
    }

    private (string Text, Brush? Brush) Describe(ReminderLogEntry entry)
    {
        var text = entry.State switch
        {
            ReminderState.Dismissed => "dispensado",
            ReminderState.Completed => "tarefa concluída pela notificação",
            _ => entry.Outcome switch
            {
                DeliveryOutcome.Shown => "entregue",
                DeliveryOutcome.SuppressedPaused => "silenciado (notificações pausadas)",
                DeliveryOutcome.SuppressedCategoryOff => "silenciado (avisos da agenda desligados)",
                DeliveryOutcome.BlockedByWindows => "bloqueado pelo Windows",
                DeliveryOutcome.RateLimited => "descartado (muitos avisos seguidos)",
                DeliveryOutcome.Duplicate => "duplicado",
                DeliveryOutcome.Failed => "falhou ao exibir",
                null => "entregando…",
                _ => entry.Outcome.ToString()!.ToLowerInvariant(),
            },
        };
        if (entry.Late)
        {
            text += " · atrasado";
        }
        var brush = entry.Outcome is DeliveryOutcome.Shown or null ? null : Res("WarningBrush");
        return (text, brush);
    }

    private static Grid Row(string when, string title, string detail, Brush? detailBrush)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var whenText = AgendaUi.Text(when, "CaptionText");
        whenText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(whenText);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(AgendaUi.Text(title));
        text.Children.Add(AgendaUi.Text(detail, "CaptionText", 11.5, detailBrush));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static StackPanel Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons)
        {
            panel.Children.Add(b);
        }
        Grid.SetColumn(panel, 2);
        return panel;
    }

    private void ShowSnooze(Button anchor, string key)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var minutes in ReminderScheduler.SnoozeMinutes)
        {
            var item = new MenuItem { Header = minutes < 60 ? $"Adiar {minutes} min" : "Adiar 1 hora" };
            item.Click += (_, _) => _reminders.Snooze(key, TimeSpan.FromMinutes(minutes));
            menu.Items.Add(item);
        }
        var dismiss = new MenuItem { Header = "Dispensar" };
        dismiss.Click += (_, _) => _reminders.Dismiss(key);
        menu.Items.Add(dismiss);
        menu.IsOpen = true;
    }

    // ---------- Novo lembrete ----------

    private void Create_Click(object sender, RoutedEventArgs e) => CreateAtTime();

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateAtTime();
            e.Handled = true;
        }
    }

    private void CreateAtTime()
    {
        if (!AgendaUi.TryParseTime(TimeBox.Text, out var time))
        {
            Message("Informe o horário (ex.: 15:30) ou escolha \"daqui a\".", error: true);
            return;
        }
        var now = _agenda.LocalNow;
        var at = now.Date + time;
        if (at <= now)
        {
            at = at.AddDays(1); // horário que já passou hoje = amanhã
        }
        Create(at);
    }

    private void In_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var minutes))
        {
            var now = _agenda.LocalNow;
            Create(now.AddSeconds(-now.Second).AddMinutes(minutes));
        }
    }

    private void Create(DateTime at)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            Message("Escreva do que você quer ser lembrado.", error: true);
            TitleBox.Focus();
            return;
        }
        _agenda.SaveTask(_agenda.NewTask() with { Title = TitleBox.Text, Due = at, DueHasTime = true, ReminderMinutes = 0, Priority = TaskPriority.High });
        Message($"Lembrete criado para {AgendaFormat.Day(at, _agenda.LocalNow)} às {AgendaFormat.Time(at)}. Ele também aparece em Tarefas.", error: false);
        TitleBox.Clear();
        TimeBox.Clear();
    }

    private void Message(string text, bool error)
    {
        CreateMessage.Text = text;
        CreateMessage.Foreground = error ? Res("DangerBrush") : Res("TextSecondaryBrush");
        CreateMessage.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
