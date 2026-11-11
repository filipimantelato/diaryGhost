using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopHub.App.Agenda;
using DesktopHub.App.Productivity;
using DesktopHub.App.Shell;
using DesktopHub.Core.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using DesktopHub.Core.Media;
using DesktopHub.Core.News;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Weather;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Resumo do dia com dados reais. Cartões reorganizáveis (ordem), redimensionáveis (normal/largo) e
/// ocultáveis — a disposição fica salva. Atualiza quando os módulos publicam mudanças, não por relógio.
/// </summary>
public partial class DashboardPage : UserControl, IDisposable
{
    private const double CardWidth = 300;
    private const double Gap = 16;

    private readonly ShellServices _s;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _customizing;
    private bool _renderPending;

    internal DashboardPage(ShellServices services)
    {
        _s = services;
        InitializeComponent();
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(services.Bus.Subscribe<WeatherUpdated>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<NewsUpdated>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<AgendaChanged>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<ProductivityChanged>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<ActiveMediaChanged>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<FinanceChanged>(_ => RequestRender(), ui));
        _subscriptions.Add(services.Bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section is SettingsSections.Dashboard or SettingsSections.Weather or SettingsSections.News or SettingsSections.Finance)
            {
                RequestRender();
            }
        }, ui));
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>Agrupa rajadas de eventos (ex.: busca de notícias) num único redesenho.</summary>
    private void RequestRender()
    {
        if (_renderPending)
        {
            return;
        }
        _renderPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderPending = false;
            Render();
        }, DispatcherPriority.Background);
    }

    private void Render()
    {
        var now = DateTime.Now;
        GreetingText.Text = now.Hour switch
        {
            >= 5 and < 12 => "Bom dia!",
            >= 12 and < 18 => "Boa tarde!",
            _ => "Boa noite!",
        };
        DateText.Text = AgendaFormat.DayTitle(now);
        CustomizeButton.Content = _customizing ? "Concluir" : "Personalizar";
        ResetButton.Visibility = CustomizeHint.Visibility = _customizing ? Visibility.Visible : Visibility.Collapsed;

        var cards = _s.Settings.Dashboard.Cards;
        CardsPanel.Children.Clear();
        var visible = cards.Where(c => !c.Hidden).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            CardsPanel.Children.Add(Card(visible[i], i, visible.Count));
        }

        HiddenPanel.Children.Clear();
        var hidden = cards.Where(c => c.Hidden).ToList();
        HiddenPanel.Visibility = _customizing && hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hidden.Count > 0)
        {
            HiddenPanel.Children.Add(new TextBlock { Text = "Ocultos:", Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        }
        foreach (var card in hidden)
        {
            var (glyph, title) = Header(card.Kind);
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = $"+ {title}", Margin = new Thickness(0, 0, 6, 6) };
            AutomationProperties.SetName(chip, $"Mostrar o cartão {title}");
            chip.Click += (_, _) => Update(card.Kind, c => c with { Hidden = false });
            HiddenPanel.Children.Add(chip);
            _ = glyph;
        }
    }

    private static (string Glyph, string Title) Header(DashboardCardKind kind) => kind switch
    {
        DashboardCardKind.Weather => ("", "Clima"),
        DashboardCardKind.NextEvent => ("", "Próximos compromissos"),
        DashboardCardKind.Tasks => ("", "Tarefas pendentes"),
        DashboardCardKind.News => ("", "Notícias"),
        DashboardCardKind.Media => ("", "Em reprodução"),
        DashboardCardKind.Focus => ("", "Temporizadores"),
        DashboardCardKind.Finance => ("", "Finanças"),
        _ => ("", "Sistema"),
    };

    private Border Card(DashboardCardSettings settings, int index, int count)
    {
        var (glyph, title) = Header(settings.Kind);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (_customizing)
        {
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -6, -6, 0) };
            var left = AgendaUi.IconButton("", $"Mover {title} para antes", (_, _) => Move(settings.Kind, -1), size: 26);
            left.IsEnabled = index > 0;
            var right = AgendaUi.IconButton("", $"Mover {title} para depois", (_, _) => Move(settings.Kind, 1), size: 26);
            right.IsEnabled = index < count - 1;
            tools.Children.Add(left);
            tools.Children.Add(right);
            tools.Children.Add(AgendaUi.IconButton(settings.Wide ? "" : "", settings.Wide ? $"Deixar {title} normal" : $"Deixar {title} largo",
                (_, _) => Update(settings.Kind, c => c with { Wide = !c.Wide }), size: 26));
            tools.Children.Add(AgendaUi.IconButton("", $"Ocultar {title}", (_, _) => Update(settings.Kind, c => c with { Hidden = true }), size: 26));
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
        }
        head.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("IconText"), FontSize = 15, Foreground = Res("TextSecondaryBrush") });
        head.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SubtitleText"), Margin = new Thickness(10, 0, 0, 0) });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(CardContent(settings.Kind, settings.Wide));
        var border = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = settings.Wide ? CardWidth * 2 + Gap : CardWidth,
            MinHeight = 150,
            Margin = new Thickness(0, 0, Gap, Gap),
            Child = body,
        };
        if (_customizing)
        {
            border.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        AutomationProperties.SetName(border, $"Cartão {title}");
        return border;
    }

    private void Move(DashboardCardKind kind, int direction) => _s.Settings.UpdateDashboard(d =>
    {
        var list = d.Cards.ToList();
        var visible = list.Where(c => !c.Hidden).ToList();
        var from = visible.FindIndex(c => c.Kind == kind);
        var to = from + direction;
        if (from < 0 || to < 0 || to >= visible.Count)
        {
            return d;
        }
        // Troca com o vizinho visível, preservando a posição dos ocultos.
        var a = list.FindIndex(c => c.Kind == visible[from].Kind);
        var b = list.FindIndex(c => c.Kind == visible[to].Kind);
        (list[a], list[b]) = (list[b], list[a]);
        return d with { Cards = list };
    });

    private void Update(DashboardCardKind kind, Func<DashboardCardSettings, DashboardCardSettings> change) =>
        _s.Settings.UpdateDashboard(d => d with { Cards = [.. d.Cards.Select(c => c.Kind == kind ? change(c) : c)] });

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        _customizing = !_customizing;
        Render();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) =>
        _s.Settings.UpdateDashboard(d => d with { Cards = DashboardSettings.Default });

    // ---------- Conteúdo dos cartões ----------

    private StackPanel CardContent(DashboardCardKind kind, bool wide) => kind switch
    {
        DashboardCardKind.Weather => WeatherContent(wide),
        DashboardCardKind.NextEvent => Lines(NextEventLines(wide ? 6 : 3)),
        DashboardCardKind.Tasks => Lines(TaskLines(wide ? 6 : 3)),
        DashboardCardKind.News => NewsContent(wide ? 6 : 3),
        DashboardCardKind.Media => Lines([NowPlayingText()]),
        DashboardCardKind.Focus => Lines([FocusText()]),
        DashboardCardKind.Finance => FinanceContent(wide),
        _ => Lines([SystemText()]),
    };

    private StackPanel Lines(IEnumerable<string> lines)
    {
        var panel = new StackPanel();
        foreach (var line in lines)
        {
            var text = AgendaUi.Text(line, "BodyText", foreground: Res("TextSecondaryBrush"));
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 0, 0, 2);
            panel.Children.Add(text);
        }
        return panel;
    }

    private Button LinkButton(string label, Action action)
    {
        var button = new Button { Style = (Style)FindResource("SecondaryButton"), Content = label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Resumo financeiro: oculto por padrão no dashboard e mascarado no modo privacidade.</summary>
    private StackPanel FinanceContent(bool wide)
    {
        var finance = _s.Finance;
        var panel = new StackPanel();
        if (!finance.IsAvailable)
        {
            panel.Children.Add(Lines([finance.UnavailableReason ?? "Finanças indisponível."]));
            return panel;
        }
        if (finance.Accounts.Count == 0)
        {
            panel.Children.Add(Lines(["Cadastre uma conta para ver saldo, previsto e despesas do mês aqui."]));
            panel.Children.Add(LinkButton("Abrir Finanças", () => _s.Bus.Publish(new OpenMainRequested("finance"))));
            return panel;
        }
        var today = finance.Today;
        var o = finance.Overview(today.Year, today.Month);
        var currency = finance.BaseCurrency;
        var big = AgendaUi.Text(FinanceUi.Amount(o.AccountsBalance, currency), "TitleText", 26);
        panel.Children.Add(big);
        panel.Children.Add(AgendaUi.Text("saldo em contas hoje", "CaptionText"));
        var lines = new List<string>
        {
            $"Previsto em {FinanceUi.Date(o.End)}: {FinanceUi.Amount(o.Forecast, currency)}",
            $"Despesas do mês: {FinanceUi.Amount(o.Totals.Expense, currency)}" + (o.Totals.ExpensePending > 0 ? $" ({FinanceUi.Amount(o.Totals.ExpensePending, currency)} pendentes)" : ""),
        };
        var next = finance.PendingUntil(today.AddDays(wide ? 30 : 14)).Where(t => t.Date >= today).Take(wide ? 3 : 1);
        lines.AddRange(next.Select(t => $"{t.Date.ToString("dd/MM", FinanceUi.PtBr)} · {t.Description} · {FinanceUi.Amount(FinanceUi.SignedFor(t, null), currency, showPlus: true)}"));
        if (o.OverduePending > 0)
        {
            lines.Add($"⚠ {o.OverduePending} pendente(s) atrasado(s)");
        }
        var details = Lines(lines);
        details.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(details);
        panel.Children.Add(LinkButton("Abrir Finanças", () => _s.Bus.Publish(new OpenMainRequested("finance"))));
        return panel;
    }

    private StackPanel WeatherContent(bool wide)
    {
        var weather = _s.Weather;
        var panel = new StackPanel();
        if (weather.Location is null)
        {
            panel.Children.Add(Lines(["Escolha sua cidade para ver a previsão real (Open-Meteo). Nada é consultado antes disso."]));
            panel.Children.Add(LinkButton("Escolher cidade", () => _s.Bus.Publish(new OpenMainRequested("settings"))));
            return panel;
        }
        if (weather.Snapshot is not { } w)
        {
            panel.Children.Add(Lines([weather.LastError ?? $"Buscando a previsão para {weather.Location.Name}…"]));
            return panel;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = WeatherCodes.Glyph(w.Current.Code, w.Current.IsDay), Style = (Style)FindResource("IconText"), FontSize = 30, Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 0, 12, 0) });
        row.Children.Add(new TextBlock { Text = $"{Math.Round(w.Current.Temperature):0}°", FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont") });
        var desc = new StackPanel { Margin = new Thickness(12, 4, 0, 0) };
        desc.Children.Add(AgendaUi.Text(WeatherCodes.Describe(w.Current.Code), "BodyText"));
        desc.Children.Add(AgendaUi.Text(w.Location.Name, "CaptionText"));
        row.Children.Add(desc);
        panel.Children.Add(row);

        var today = w.Daily.Count > 0 ? w.Daily[0] : null;
        var details = $"Sensação {Math.Round(w.Current.ApparentTemperature):0}° · umidade {w.Current.Humidity}% · vento {Math.Round(w.Current.WindKmh):0} km/h";
        if (today is not null)
        {
            details += $"\nHoje: máx. {Math.Round(today.Max):0}° · mín. {Math.Round(today.Min):0}°" + (today.PrecipitationProbability is { } p ? $" · chuva {p}%" : "");
        }
        var detailText = AgendaUi.Text(details, "CaptionText");
        detailText.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(detailText);

        if (wide && w.Daily.Count > 1)
        {
            var days = new UniformGrid { Columns = Math.Min(6, w.Daily.Count - 1), Margin = new Thickness(0, 10, 0, 0) };
            foreach (var d in w.Daily.Skip(1).Take(6))
            {
                var cell = new StackPanel();
                cell.Children.Add(AgendaUi.Text(d.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd", AgendaUi.PtBr).TrimEnd('.'), "CaptionText"));
                cell.Children.Add(new TextBlock { Text = WeatherCodes.Glyph(d.Code, true), Style = (Style)FindResource("IconText"), FontSize = 16, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = WeatherCodes.Describe(d.Code) });
                cell.Children.Add(AgendaUi.Text($"{Math.Round(d.Max):0}° / {Math.Round(d.Min):0}°", "CaptionText"));
                days.Children.Add(cell);
            }
            panel.Children.Add(days);
        }

        var fetched = w.FetchedAt.ToLocalTime();
        var footer = weather.LastError is not null || weather.IsStale
            ? $"Sem atualização recente — dados de {AgendaFormat.Day(fetched.DateTime, DateTime.Today)} às {fetched:HH:mm}"
            : $"{w.Provider} · atualizado às {fetched:HH:mm}";
        var footerText = AgendaUi.Text(footer, "CaptionText", 11, weather.LastError is not null || weather.IsStale ? Res("WarningBrush") : Res("TextTertiaryBrush"));
        footerText.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(footerText);
        return panel;
    }

    private StackPanel NewsContent(int max)
    {
        var news = _s.News;
        var panel = new StackPanel();
        if (!news.IsEnabled)
        {
            panel.Children.Add(Lines(["Notícias de fontes confiáveis (feeds oficiais). Desativado até você ativar."]));
            panel.Children.Add(LinkButton("Abrir Notícias", () => _s.Bus.Publish(new OpenMainRequested("news"))));
            return panel;
        }
        var topics = _s.Settings.News.FollowedTopics.Select(NewsTitles.Key).ToList();
        var sources = news.Sources.Where(s => s.Enabled).ToDictionary(s => s.Id);
        var leads = news.Articles.Where(a => sources.ContainsKey(a.SourceId) && !a.IsRead)
            .GroupBy(a => a.ClusterId).Select(g => (Lead: g.First(), Count: g.Count()))
            .OrderByDescending(x => topics.Any(t => NewsTitles.Matches(x.Lead.TitleKey, t)))
            .ThenByDescending(x => x.Count > 1)
            .ThenByDescending(x => x.Lead.SortDate)
            .Take(max).ToList();
        if (leads.Count == 0)
        {
            panel.Children.Add(Lines([news.Articles.Count == 0 ? "Buscando as primeiras notícias…" : "Nada novo por enquanto."]));
        }
        foreach (var (lead, count) in leads)
        {
            var source = sources[lead.SourceId];
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var title = AgendaUi.Text(lead.Title, "BodyText");
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxHeight = 40;
            item.Children.Add(title);
            var when = (lead.PublishedAt ?? lead.FetchedAt).ToLocalTime().DateTime;
            var whenText = when.Date == DateTime.Today ? AgendaFormat.Time(when) : $"{AgendaFormat.Day(when, DateTime.Today)} {AgendaFormat.Time(when)}";
            item.Children.Add(AgendaUi.Text($"{source.Name} · {whenText}{(count > 1 ? $" · +{count - 1} fontes" : "")}", "CaptionText", 11));
            item.MouseLeftButtonUp += (_, _) => Open(lead);
            item.ToolTip = "Abrir no navegador";
            AutomationProperties.SetName(item, $"{lead.Title}, {source.Name}");
            panel.Children.Add(item);
        }
        if (news.LastSuccessAt is { } last)
        {
            panel.Children.Add(AgendaUi.Text($"Consultado às {last.ToLocalTime():HH:mm}", "CaptionText", 11, Res("TextTertiaryBrush")));
        }
        return panel;
    }

    private void Open(NewsArticle article)
    {
        if (NewsUrl.TryParseHttp(article.Url, null, out var url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                _s.News.SetState(article.Id, read: true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private IEnumerable<string> NextEventLines(int max)
    {
        var agenda = _s.Agenda;
        var now = agenda.Now;
        var today = agenda.LocalNow;
        var next = agenda.OccurrencesBetween(now, now.AddDays(14)).Take(max).ToList();
        if (next.Count == 0)
        {
            return ["Nada nos próximos 14 dias."];
        }
        return next.Select(o =>
        {
            var start = ZonedTime.ToWall(o.StartUtc, agenda.LocalZone);
            var when = o.Event.AllDay ? $"{AgendaFormat.Day(start, today)}, dia inteiro"
                : o.StartUtc <= now ? $"agora (até {AgendaFormat.Time(ZonedTime.ToWall(o.EndUtc, agenda.LocalZone))})"
                : $"{AgendaFormat.Day(start, today)} às {AgendaFormat.Time(start)}";
            return $"{o.Event.Title} — {when}";
        });
    }

    private IEnumerable<string> TaskLines(int max)
    {
        var agenda = _s.Agenda;
        var open = agenda.Tasks.Where(t => !t.IsCompleted).ToList();
        if (open.Count == 0)
        {
            return ["Nenhuma tarefa pendente."];
        }
        var today = agenda.LocalNow.Date;
        var overdue = open.Count(agenda.IsOverdue);
        var dueToday = open.Count(t => t.Due?.Date == today && !agenda.IsOverdue(t));
        var top = open.Where(t => t.Due is not null).OrderBy(t => agenda.DueUtc(t)).ThenByDescending(t => t.Priority).Take(max - 1)
            .Select(t => $"• {t.Title} ({(agenda.IsOverdue(t) ? "atrasada" : AgendaFormat.Day(t.Due!.Value, today))})");
        return new[] { $"{open.Count} pendente(s) · {overdue} atrasada(s) · {dueToday} para hoje" }.Concat(top);
    }

    private string NowPlayingText() => _s.Media.Active is { } m
        ? $"{m.Title ?? "Sem título informado"}{(m.Artist is null ? "" : " — " + m.Artist)}\n{m.AppName} · {(m.IsPlaying ? "tocando" : "pausado")}"
        : _s.Media.IsAvailable ? "Nada tocando agora." : "Controle de mídia indisponível neste sistema.";

    private string FocusText()
    {
        var productivity = _s.Productivity;
        var s = productivity.Snapshot;
        var now = productivity.Now;
        var lines = new List<string>();
        if (s.Pomodoro.Countdown.HasStarted)
        {
            lines.Add($"Pomodoro: {FocusView.Countdown(s.Pomodoro.Countdown.RemainingAt(now))} · {FocusView.PomodoroStatus(s.Pomodoro, now)}");
        }
        if (s.Timer is { HasStarted: true } t && !t.IsFinishedAt(now))
        {
            lines.Add($"Temporizador: {FocusView.Countdown(t.RemainingAt(now))} restantes{(t.IsRunning ? "" : " (pausado)")}");
        }
        if (!s.Stopwatch.IsIdle)
        {
            lines.Add($"Cronômetro: {FocusView.Elapsed(s.Stopwatch.ElapsedAt(now))}{(s.Stopwatch.IsRunning ? "" : " (pausado)")}");
        }
        var stats = productivity.Stats();
        if (lines.Count == 0)
        {
            lines.Add("Nada em andamento.");
        }
        lines.Add($"Foco hoje: {ProductivityService.FormatDuration(TimeSpan.FromMinutes(Math.Round(stats.FocusToday.TotalMinutes)))} · {stats.PomodorosToday} Pomodoro(s)");
        return string.Join("\n", lines);
    }

    private string SystemText()
    {
        var db = _s.Startup.Database;
        var text = $"Dados locais em {_s.Paths.Root}\nEsquema do banco: v{db?.Migration.ToVersion}\nWidget: {(_s.Widget.IsShown ? "visível" : "oculto")}{(_s.Settings.Widget.AlwaysOnTop ? ", sempre visível" : "")}";
        return db?.RecoveredFromCorruption == true ? text + "\nBanco recuperado: cópia preservada em 'corrupt'" : text;
    }

    public void Dispose()
    {
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
