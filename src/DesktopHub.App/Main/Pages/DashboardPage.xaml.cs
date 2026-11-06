using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopHub.App.Shell;
using DesktopHub.App.Widget;
using DesktopHub.Core;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

public sealed record DashboardCard(string Glyph, string Title, string Body, string? Badge)
{
    public Visibility BadgeVisibility => Badge is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Resumo do dia. Na Fase 1 só o cartão "Sistema" e a saudação têm dados reais; os demais dizem
/// claramente que o módulo ainda não existe. Cartões reorganizáveis chegam na Fase 5.
/// </summary>
public partial class DashboardPage : UserControl
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    internal DashboardPage(SettingsService settings, WidgetController widget, StartupState startup, AppPaths paths)
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
            new("", "Próximo compromisso", "Calendário local e contas sincronizadas.", "Não configurado · Fase 4"),
            new("", "Tarefas pendentes", "Tarefas e lembretes persistentes.", "Não configurado · Fase 4"),
            new("", "Notícias importantes", "Fontes confiáveis por RSS e APIs oficiais.", "Não configurado · Fase 5"),
            new("", "Em reprodução", "Sessões de mídia do Windows (Spotify, navegadores…).", "Não configurado · Fase 2"),
            new("", "Temporizadores ativos", "Cronômetro, temporizador e Pomodoro.", "Não configurado · Fase 3"),
            new("", "Sistema", system, db?.RecoveredFromCorruption == true ? "Banco recuperado: cópia preservada em 'corrupt'" : null),
        };
    }
}
