using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.App.Shell;
using DesktopHub.Core.Activity;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;
using DesktopHub.Infrastructure.Activity;
using Microsoft.Win32;

namespace DesktopHub.App.Main.Pages;

/// <summary>Claude Code (hooks + canal local), agendas iCal assinadas, importação/exportação .ics.</summary>
public partial class IntegrationsPage : UserControl, IDisposable
{
    private readonly ShellServices _s;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _syncing;

    internal IntegrationsPage(ShellServices services)
    {
        _s = services;
        InitializeComponent();
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(services.Bus.Subscribe<ActivityReceived>(_ => Render(), ui));
        _subscriptions.Add(services.Bus.Subscribe<CalendarSubscriptionsChanged>(_ => Render(), ui));
        _subscriptions.Add(services.Bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Integrations)
            {
                Render();
            }
        }, ui));
        SubCategory.ItemsSource = AgendaUi.CategoryChoices(services.Agenda);
        SubCategory.SelectedIndex = 0;
        var reminders = AgendaUi.ReminderChoices();
        SubReminder.ItemsSource = reminders;
        SubReminder.SelectedIndex = AgendaUi.ReminderIndex(reminders, 10);
        ManualSnippet.Text = Snippet();
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>Comando do hook: o próprio executável em modo --hook (barras normais funcionam no bash e no cmd).</summary>
    private string HookCommand()
    {
        var exe = (Environment.ProcessPath ?? "DiaryGhost.exe").Replace('\\', '/');
        var command = $"\"{exe}\" {ClaudeHooksInstaller.Marker}";
        if (_s.Startup.Options.DataDir is { } dataDir)
        {
            command += $" --data-dir \"{Path.GetFullPath(dataDir).Replace('\\', '/')}\"";
        }
        return command;
    }

    private string Snippet()
    {
        var hooks = new JsonObject();
        foreach (var evt in ClaudeHooksInstaller.Events)
        {
            hooks[evt] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand(), ["timeout"] = 5 }),
            });
        }
        return new JsonObject { ["hooks"] = hooks }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private void Render()
    {
        var activity = _s.Activity;
        bool installed;
        string hooksText;
        try
        {
            installed = activity.Hooks.IsInstalled();
            hooksText = installed ? $"instalados em {activity.Hooks.SettingsPath}" : "não instalados";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            installed = false;
            hooksText = $"não foi possível ler {activity.Hooks.SettingsPath} ({ex.Message})";
        }
        var channel = activity.IsListening ? "escutando" : _s.Settings.Integrations.ClaudeCodeEnabled ? $"indisponível ({activity.ChannelError ?? "iniciando"})" : "desligado";
        ClaudeStatus.Text = $"Hooks: {hooksText}\nCanal local: {channel}";
        InstallButton.Content = installed ? "Reinstalar hooks" : "Instalar hooks";
        UninstallButton.IsEnabled = installed;

        _syncing = true;
        var s = _s.Settings.Integrations;
        ListenBox.IsChecked = s.ClaudeCodeEnabled;
        NotifyDoneBox.IsChecked = s.NotifyClaudeFinished;
        NotifyWaitBox.IsChecked = s.NotifyClaudeWaiting;
        _syncing = false;

        RecentList.Children.Clear();
        foreach (var e in activity.Recent.Take(10))
        {
            var when = e.ReceivedAt.ToLocalTime();
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(AgendaUi.Text($"{AgendaFormat.Day(when.DateTime, DateTime.Today)} {when:HH:mm:ss}", "CaptionText"));
            var text = AgendaUi.Text(ActivityText.Describe(e), "BodyText", 13);
            text.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            RecentList.Children.Add(row);
        }
        if (RecentList.Children.Count == 0)
        {
            RecentList.Children.Add(AgendaUi.Text("Nenhum evento ainda. Use \"Enviar evento de teste\" para conferir o canal.", "CaptionText"));
        }

        SubscriptionList.Children.Clear();
        foreach (var sub in _s.Subscriptions.Subscriptions)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(AgendaUi.IconButton("", $"Sincronizar {sub.Name} agora", (_, _) => _ = _s.Subscriptions.SyncAsync(force: true)));
            actions.Children.Add(AgendaUi.IconButton("", $"Remover a agenda {sub.Name}", (_, _) => _s.Subscriptions.Remove(sub.Id)));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var info = new StackPanel();
            info.Children.Add(AgendaUi.Text($"{sub.Name} · {_s.Subscriptions.MaskedUrl(sub.Id)}", "BodyText", 13));
            var status = sub.LastSuccessAt is { } ok
                ? $"{sub.EventCount} eventos · sincronizada {AgendaFormat.Day(ok.ToLocalTime().DateTime, DateTime.Today)} às {ok.ToLocalTime():HH:mm}"
                : "Ainda não sincronizada";
            info.Children.Add(AgendaUi.Text(status, "CaptionText", 11.5));
            if (sub.LastError is { } error)
            {
                var err = AgendaUi.Text(error, "CaptionText", 11.5, Res("WarningBrush"));
                err.TextWrapping = TextWrapping.Wrap;
                info.Children.Add(err);
            }
            row.Children.Add(info);
            SubscriptionList.Children.Add(row);
        }
        if (SubscriptionList.Children.Count == 0)
        {
            SubscriptionList.Children.Add(AgendaUi.Text("Nenhuma agenda assinada.", "CaptionText"));
        }
    }

    // ---------- Claude Code ----------

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var path = _s.Activity.Hooks.SettingsPath;
        var answer = MessageBox.Show(
            $"O Diary Ghost vai acrescentar hooks em:\n{path}\n\nEventos: {string.Join(", ", ClaudeHooksInstaller.Events)}\nComando: {HookCommand()}\n\n" +
            "Só as entradas do Diary Ghost são alteradas; o resto do arquivo é preservado e uma cópia de segurança é criada antes. " +
            "Sessões do Claude Code já abertas passam a usar os hooks depois de reiniciadas.\n\nContinuar?",
            "Instalar hooks do Claude Code", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var backup = _s.Activity.Hooks.Install(HookCommand());
            _s.Settings.UpdateIntegrations(i => i with { ClaudeCodeEnabled = true });
            Message(backup is null ? "Hooks instalados." : $"Hooks instalados. Cópia de segurança: {backup}", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Message($"Não foi possível alterar o arquivo: {ex.Message}. Nada foi modificado; use a configuração manual abaixo.", error: true);
        }
        Render();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _s.Activity.Hooks.Uninstall();
            Message("Hooks removidos (cópia de segurança criada antes).", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Message($"Não foi possível alterar o arquivo: {ex.Message}", error: true);
        }
        Render();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!_s.Settings.Integrations.ClaudeCodeEnabled)
        {
            Message("Ligue \"Receber eventos do Claude Code\" para testar o canal.", error: true);
            return;
        }
        // Passa pelo canal de verdade (o mesmo que o hook usa), não por um atalho interno.
        var name = ActivityPipe.Name(_s.Startup.Options.DataDir);
        var sent = Task.Run(() => ActivityPipe.Send(name, ActivityContract.TestMessage(), 1500));
        sent.ContinueWith(t => Dispatcher.BeginInvoke(() =>
            Message(t.Result ? "Evento de teste enviado pelo canal local." : "O canal não respondeu. Veja o estado acima.", error: !t.Result)), TaskScheduler.Default);
    }

    private void Listen_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _s.Settings.UpdateIntegrations(i => i with { ClaudeCodeEnabled = ListenBox.IsChecked == true });
            // O canal leva um instante para abrir.
            Dispatcher.BeginInvoke(Render, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _s.Settings.UpdateIntegrations(i => i with { NotifyClaudeFinished = NotifyDoneBox.IsChecked == true, NotifyClaudeWaiting = NotifyWaitBox.IsChecked == true });
        }
    }

    private void Message(string text, bool error)
    {
        ClaudeMessage.Text = text;
        ClaudeMessage.Foreground = error ? Res("DangerBrush") : Res("TextSecondaryBrush");
        ClaudeMessage.Visibility = Visibility.Visible;
    }

    // ---------- Agendas ----------

    private async void AddSubscription_Click(object sender, RoutedEventArgs e)
    {
        AddSubButton.IsEnabled = false;
        SubMessage.Visibility = Visibility.Visible;
        SubMessage.Foreground = Res("TextSecondaryBrush");
        SubMessage.Text = "Baixando e conferindo a agenda…";
        try
        {
            var error = await _s.Subscriptions.AddAsync(SubName.Text, SubUrl.Text,
                (SubCategory.SelectedItem as Choice<Guid?>)?.Value, (SubReminder.SelectedItem as Choice<int?>)?.Value);
            SubMessage.Text = error ?? "Agenda assinada. Os eventos já aparecem no Calendário.";
            SubMessage.Foreground = error is null ? Res("SuccessBrush") : Res("DangerBrush");
            if (error is null)
            {
                SubName.Clear();
                SubUrl.Clear();
            }
        }
        finally
        {
            AddSubButton.IsEnabled = true;
        }
    }

    // ---------- .ics ----------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Calendário (*.ics)|*.ics", Title = "Importar eventos" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > ICalendar.MaxBytes)
            {
                Ics("Arquivo grande demais (máx. 10 MB).", error: true);
                return;
            }
            var import = ICalendar.Parse(File.ReadAllText(dialog.FileName), _s.Agenda.LocalZone, _s.Agenda.Now);
            var added = _s.Agenda.ImportEvents(import.Events);
            var skipped = import.Events.Count - added;
            Ics($"{added} eventos importados{(skipped > 0 ? $", {skipped} já existiam ou eram inválidos" : "")}." +
                (import.Warnings.Count > 0 ? " Avisos: " + string.Join(" ", import.Warnings.Take(3)) : ""), error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Ics($"Não foi possível importar: {ex.Message}", error: true);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Calendário (*.ics)|*.ics", FileName = "diary-ghost-agenda.ics", Title = "Exportar agenda local" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var events = _s.Agenda.Events.Where(ev => !ev.IsReadOnly).ToList();
            File.WriteAllText(dialog.FileName, ICalendar.Write(events, _s.Agenda.LocalZone, _s.Agenda.Now), new System.Text.UTF8Encoding(false));
            Ics($"{events.Count} eventos exportados para {dialog.FileName}.", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ics($"Não foi possível exportar: {ex.Message}", error: true);
        }
    }

    private void Ics(string text, bool error)
    {
        IcsMessage.Text = text;
        IcsMessage.Foreground = error ? Res("DangerBrush") : Res("TextSecondaryBrush");
        IcsMessage.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}

/// <summary>Textos de atividade em pt-BR (página, widget).</summary>
internal static class ActivityText
{
    public static string State(ActivityEvent e) => e.Kind switch
    {
        ActivityKind.SessionStarted => "sessão iniciada",
        ActivityKind.Working => "trabalhando",
        ActivityKind.Waiting => "aguardando você",
        ActivityKind.Finished => "resposta pronta",
        ActivityKind.SessionEnded => "sessão encerrada",
        ActivityKind.Test => "teste do canal",
        _ => "evento",
    };

    public static string Glyph(ActivityKind kind) => kind switch
    {
        ActivityKind.Working => "",
        ActivityKind.Waiting => "",
        ActivityKind.Finished => "",
        ActivityKind.SessionEnded => "",
        _ => "",
    };

    public static string Describe(ActivityEvent e)
    {
        var who = e.Source == "claude-code" ? "Claude Code" : "Diary Ghost";
        var where = e.Project is null ? "" : $" · {e.Project}";
        var message = e.Message is null ? "" : $" — {e.Message}";
        return $"{who}: {State(e)}{where}{message}";
    }
}
