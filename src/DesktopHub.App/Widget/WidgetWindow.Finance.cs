using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Finance;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Widget;

/// <summary>
/// Painel Finanças do widget (Fase 12): disponível, gasto do mês × orçamento, próxima fatura e próximo vencimento.
/// Valores ficam ocultos ("R$ ••••") por padrão: o olho revela por 30 s ou até o widget recolher; em Configurações há
/// "Mostrar valores no widget". O modo privacidade global sempre oculta. Percentuais e barras continuam visíveis.
/// </summary>
public partial class WidgetWindow
{
    private static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(30);
    private readonly FinanceService _finance;
    private DispatcherTimer? _revealTimer;
    private bool _revealed;

    internal void ShowFinance()
    {
        if (_appliedPanel == WidgetPanel.Finance && _appliedLayout == WidgetLayout.Expanded)
        {
            RenderFinance();
        }
    }

    private bool FinanceValuesVisible => !_settings.Finance.HideValues && (_settings.Finance.WidgetShowValues || _revealed);

    private void HideFinanceValues()
    {
        _revealTimer?.Stop();
        _revealed = false;
    }

    private string Value(long minor) => FinanceValuesVisible ? Money.Format(minor, _finance.BaseCurrency) : Money.Masked(_finance.BaseCurrency);

    private void RenderFinance()
    {
        FinanceList.Children.Clear();
        if (!_finance.IsAvailable || _finance.Accounts.Count == 0)
        {
            FinanceList.Children.Add(new TextBlock
            {
                Text = _finance.IsAvailable ? "Cadastre uma conta em Finanças para ver o resumo aqui." : "Finanças indisponível no momento.",
                Style = (Style)FindResource("CaptionText"),
                TextWrapping = TextWrapping.Wrap,
            });
            RequestResize();
            return;
        }
        var today = _finance.Today;
        var overview = _finance.Overview(today.Year, today.Month);

        // Disponível + olho (só quando os valores estão ocultos e o modo privacidade global está desligado).
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var available = new StackPanel();
        available.Children.Add(new TextBlock { Text = overview.Reserved > 0 ? "Disponível (sem o reservado)" : "Disponível", FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush") });
        available.Children.Add(new TextBlock { Text = Value(overview.Available), FontSize = 20, FontWeight = FontWeights.SemiBold });
        AutomationProperties.SetName(available, $"Disponível: {Value(overview.Available)}");
        head.Children.Add(available);
        if (!_settings.Finance.HideValues && !_settings.Finance.WidgetShowValues)
        {
            var eye = AgendaUi.IconButton(_revealed ? "" : "", _revealed ? "Ocultar valores" : "Mostrar valores por 30 segundos", (_, _) => ToggleReveal(), size: 28);
            eye.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(eye, 1);
            head.Children.Add(eye);
        }
        FinanceList.Children.Add(head);

        // Gasto do mês × orçamento (barra e % sempre visíveis).
        var budget = _finance.BudgetMonth(today.Year, today.Month);
        if (budget.Lines.Count > 0)
        {
            var pct = Money.Percent(budget.TotalSpent, budget.TotalLimit) ?? 0;
            var over = budget.TotalSpent > budget.TotalLimit;
            FinanceList.Children.Add(FinanceRow("Orçamento do mês", $"{pct.ToString("0", PtBr)}% · {Value(budget.TotalSpent)}", over));
            var track = new Grid { Height = 6, Margin = new Thickness(0, 2, 0, 8) };
            track.Children.Add(new Border { Background = (Brush)FindResource("SurfaceElevatedBrush"), CornerRadius = new CornerRadius(3) });
            var fill = new Grid();
            var ratio = (double)Math.Clamp(pct / 100m, 0, 1);
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            fill.Children.Add(new Border
            {
                Background = over ? (Brush)FindResource("DangerBrush") : pct >= 80 ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("SuccessBrush"),
                CornerRadius = new CornerRadius(3),
            });
            track.Children.Add(fill);
            AutomationProperties.SetName(track, $"Orçamento do mês: {pct.ToString("0", PtBr)}% usado");
            FinanceList.Children.Add(track);
        }
        else
        {
            FinanceList.Children.Add(FinanceRow("Despesas do mês", Value(overview.Totals.Expense), false));
        }

        // Próxima fatura com saldo.
        var statement = _finance.Cards.Where(c => !c.IsArchived && c.Currency == _finance.BaseCurrency)
            .Select(c => (Card: c, Statement: _finance.StatementToPay(c.Id)))
            .Where(x => x.Statement is { Remaining: > 0 })
            .OrderBy(x => x.Statement!.Due)
            .FirstOrDefault();
        if (statement.Card is { } card && statement.Statement is { } s)
        {
            var late = s.Due < today;
            FinanceList.Children.Add(FinanceRow($"Fatura {card.Name} · {(late ? "venceu" : "vence")} {s.Due.ToString("dd/MM", PtBr)}", Value(s.Remaining), late));
        }

        // Próximo vencimento (conta, assinatura ou despesa pendente).
        var next = _finance.OpenOccurrences(today.AddDays(31)).Where(o => !o.Item.IsIncome).Select(o => (o.Date, Title: o.Item.Description, o.Amount))
            .Concat(_finance.PendingUntil(today.AddDays(31)).Where(t => t.Kind == TransactionKind.Expense).Select(t => (t.Date, Title: t.Description, t.Amount)))
            .OrderBy(x => x.Date)
            .FirstOrDefault();
        if (next.Title is not null)
        {
            var late = next.Date < today;
            var when = next.Date == today ? "hoje" : late ? $"atrasada ({next.Date.ToString("dd/MM", PtBr)})" : next.Date.ToString("dd/MM", PtBr);
            FinanceList.Children.Add(FinanceRow($"{next.Title} · {when}", Value(next.Amount), late));
        }
        RequestResize();
    }

    private Grid FinanceRow(string label, string value, bool warn)
    {
        var row = new Grid { Height = 22, Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = warn ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("TextPrimaryBrush"),
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        AutomationProperties.SetName(row, $"{label}: {value}");
        return row;
    }

    private void ToggleReveal()
    {
        if (_revealed)
        {
            HideFinanceValues();
        }
        else
        {
            _revealed = true;
            _revealTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = RevealDuration };
            _revealTimer.Tick -= OnRevealElapsed;
            _revealTimer.Tick += OnRevealElapsed;
            _revealTimer.Start();
        }
        RenderFinance();
    }

    private void OnRevealElapsed(object? sender, EventArgs e)
    {
        HideFinanceValues();
        ShowFinance();
    }
}
