using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopHub.App.Shell;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.App.Productivity;
using DesktopHub.Core.Media;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

public sealed record DashboardCard(string Glyph, string Title, string Body, string? Badge)
{
    public Visibility BadgeVisibility => Badge is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Resumo do dia. Saudação, "Em reprodução" e "Sistema" têm dados reais; os demais dizem
/// claramente que o módulo ainda não existe. Cartões reorganizáveis chegam na Fase 5.
/// </summary>
public partial class DashboardPage : UserControl
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    internal DashboardPage(SettingsService settings, WidgetController widget, StartupState startup, AppPaths paths, MediaService media, ProductivityService productivity, AgendaService agenda)
    {
        InitializeComponent();
        var now = DateTime.Now;
        GreetingText.Text = now.Hour switch
        {
            >= 5 and < 12 => "Bom dia!",
            >= 12 and < 18 => "Boa tarde!",
            _ => "Boa noite!",
        };
        var date = now.ToString("dddd, d 'de' MMMM", PtBr);
        DateText.Text = char.ToUpper(date[0], PtBr) + date[1..];

        var db = startup.Database;
        var system = $"Dados locais em {paths.Root}\nEsquema do banco: v{db?.Migration.ToVersion}\nWidget: {(widget.IsShown ? "visível" : "oculto")}{(settings.Widget.AlwaysOnTop ? ", sempre visível" : "")}";

        Cards.ItemsSource = new List<DashboardCard>
        {
            new("", "Clima", "Previsão real (Open-Meteo) para a localização que você definir.", "Não configurado · Fase 5"),
            new("", "Próximo compromisso", NextEventText(agenda), null),
            new("", "Tarefas pendentes", TasksText(agenda), null),
            new("", "Notícias importantes", "Fontes confiáveis por RSS e APIs oficiais.", "Não configurado · Fase 5"),
            new("", "Em reprodução", NowPlayingText(media), null),
            new("", "Temporizadores ativos", FocusText(productivity), null),
            new("", "Sistema", system, db?.RecoveredFromCorruption == true ? "Banco recuperado: cópia preservada em 'corrupt'" : null),
        };
    }

    private static string NextEventText(AgendaService agenda)
    {
        var now = agenda.Now;
        var today = agenda.LocalNow;
        var next = agenda.OccurrencesBetween(now, now.AddDays(14)).Where(o => !o.Event.AllDay || o.StartUtc >= now.AddDays(-1)).Take(3).ToList();
        if (next.Count == 0)
        {
            return "Nada nos próximos 14 dias.";
        }
        return string.Join("\n", next.Select(o =>
        {
            var start = ZonedTime.ToWall(o.StartUtc, agenda.LocalZone);
            var when = o.Event.AllDay ? $"{AgendaFormat.Day(start, today)}, dia inteiro"
                : o.StartUtc <= now ? $"agora (até {AgendaFormat.Time(ZonedTime.ToWall(o.EndUtc, agenda.LocalZone))})"
                : $"{AgendaFormat.Day(start, today)} às {AgendaFormat.Time(start)}";
            return $"{o.Event.Title} — {when}";
        }));
    }

    private static string TasksText(AgendaService agenda)
    {
        var open = agenda.Tasks.Where(t => !t.IsCompleted).ToList();
        if (open.Count == 0)
        {
            return "Nenhuma tarefa pendente.";
        }
        var today = agenda.LocalNow.Date;
        var overdue = open.Count(agenda.IsOverdue);
        var dueToday = open.Count(t => t.Due?.Date == today && !agenda.IsOverdue(t));
        var top = open.Where(t => t.Due is not null).OrderBy(t => agenda.DueUtc(t)).ThenByDescending(t => t.Priority).Take(2)
            .Select(t => $"• {t.Title} ({(agenda.IsOverdue(t) ? "atrasada" : AgendaFormat.Day(t.Due!.Value, today))})");
        return string.Join("\n", new[] { $"{open.Count} pendente(s) · {overdue} atrasada(s) · {dueToday} para hoje" }.Concat(top));
    }

    /// <summary>Retrato no momento em que o dashboard abre; a página Mídia e o widget mostram ao vivo.</summary>
    private static string NowPlayingText(MediaService media) => media.Active is { } m
        ? $"{m.Title ?? "Sem título informado"}{(m.Artist is null ? "" : " — " + m.Artist)}\n{m.AppName} · {(m.IsPlaying ? "tocando" : "pausado")}"
        : media.IsAvailable ? "Nada tocando agora." : "Controle de mídia indisponível neste sistema.";

    private static string FocusText(ProductivityService productivity)
    {
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
        lines.Add(lines.Count == 0 ? "Nada em andamento." : "");
        lines.Add($"Foco hoje: {ProductivityService.FormatDuration(TimeSpan.FromMinutes(Math.Round(stats.FocusToday.TotalMinutes)))} · {stats.PomodorosToday} Pomodoro(s)");
        return string.Join("\n", lines.Where(l => l.Length > 0));
    }
}