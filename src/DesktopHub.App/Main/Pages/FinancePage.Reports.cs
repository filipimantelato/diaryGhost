using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>Relatórios, exportação CSV e backup protegido (Fase 13).</summary>
public partial class FinancePage
{
    private enum ReportRange
    {
        ThisMonth,
        Last3,
        Last6,
        Last12,
        ThisYear,
        LastYear,
    }

    private static ReportRange s_reportRange = ReportRange.Last6;

    private (DateOnly From, DateOnly To) ReportPeriod()
    {
        var today = _finance.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return s_reportRange switch
        {
            ReportRange.ThisMonth => (monthStart, monthEnd),
            ReportRange.Last3 => (monthStart.AddMonths(-2), monthEnd),
            ReportRange.Last12 => (monthStart.AddMonths(-11), monthEnd),
            ReportRange.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            ReportRange.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            _ => (monthStart.AddMonths(-5), monthEnd),
        };
    }

    private void RenderReports()
    {
        var currency = _finance.BaseCurrency;
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(chips, "Período do relatório");
        foreach (var (label, range) in new[] { ("Este mês", ReportRange.ThisMonth), ("3 meses", ReportRange.Last3), ("6 meses", ReportRange.Last6), ("12 meses", ReportRange.Last12), ("Este ano", ReportRange.ThisYear), ("Ano passado", ReportRange.LastYear) })
        {
            var chip = Chip(label, (_, _) =>
            {
                s_reportRange = range;
                Render();
            }, s_reportRange == range);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chips.Children.Add(chip);
        }
        ListPanel.Children.Add(chips);
        var (from, to) = ReportPeriod();
        var report = _finance.Report(from, to);
        var period = $"{FinanceUi.Date(from)} a {FinanceUi.Date(to)}";

        // Resumo do período.
        ListPanel.Children.Add(SectionTitle("Resumo do período", period));
        var summary = new UniformGrid { Columns = _narrow ? 2 : 4, Margin = new Thickness(0, 0, -12, 0) };
        summary.Children.Add(Indicator("Receitas", report.Totals.Income, $"média {FinanceUi.Amount(report.AverageIncome, currency)}/mês",
            new Change(report.Totals.Income, report.PreviousTotals.Income), "período anterior", true, "Receitas do período (com pendentes), comparadas com o período anterior de mesma duração.", glyph: "", color: FinanceColors.Green));
        summary.Children.Add(Indicator("Despesas", report.Totals.Expense, $"média {FinanceUi.Amount(report.AverageExpense, currency)}/mês",
            new Change(report.Totals.Expense, report.PreviousTotals.Expense), "período anterior", false, "Despesas do período menos estornos (com pendentes). Parcelas de cartão contam na data de cada parcela.", glyph: "", color: FinanceColors.Red));
        summary.Children.Add(Indicator("Resultado", report.Totals.Net, "receitas − despesas",
            new Change(report.Totals.Net, report.PreviousTotals.Net), "período anterior", true, "Receitas menos despesas do período.", showPlus: true, glyph: "", color: FinanceColors.Purple));
        summary.Children.Add(Indicator("Contas fixas", report.RecurringMonthly, $"por mês · {FinanceUi.Amount(report.SubscriptionsMonthly, currency)} em assinaturas", null, "", false,
            "Custo mensal equivalente das contas e assinaturas recorrentes ativas.", glyph: "", color: FinanceColors.Orange));
        ListPanel.Children.Add(summary);

        // Mês a mês.
        var flows = report.Months.Select(m => new MonthFlow(m.Year, m.Month, m.Income, m.Expense)).ToList();
        var monthsPanel = new StackPanel();
        if (flows.Count > 1 && !s_tables)
        {
            monthsPanel.Children.Add(FinanceCharts.FlowBars(flows, report.Months[^1].Year, report.Months[^1].Month, currency));
        }
        monthsPanel.Children.Add(FinanceCharts.Table(["Mês", "Receitas", "Despesas", "Resultado", "No cartão", "Saldo no fim"],
            report.Months.Select(m => new[]
            {
                FinanceUi.MonthTitle(m.Year, m.Month),
                FinanceUi.Amount(m.Income, currency),
                FinanceUi.Amount(m.Expense, currency),
                FinanceUi.Amount(m.Net, currency, showPlus: true),
                FinanceUi.Amount(m.CardSpending, currency),
                FinanceUi.Amount(m.EndBalance, currency) + (m.IsForecast ? " (prev.)" : ""),
            })));
        ListPanel.Children.Add(SectionTitle("Evolução", "mês a mês"));
        ListPanel.Children.Add(Columns((Panel("Receitas, despesas e saldo", monthsPanel,
            "Receitas e despesas com pendentes; saldo do disponível no último dia de cada mês (previsto no mês atual e nos futuros).", TablesToggle(), ("", FinanceColors.Blue)), 1)));

        // Categorias com comparação.
        var previous = report.PreviousCategories.ToDictionary(c => c.CategoryId ?? Guid.Empty, c => c.Amount);
        var total = Money.Sum(report.Categories.Select(c => c.Amount));
        var categories = new StackPanel();
        if (report.Categories.Count == 0)
        {
            categories.Children.Add(Paragraph("Nenhuma despesa no período."));
        }
        foreach (var c in report.Categories.Take(12))
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(FinanceUi.ColorDot(c.Color, 8));
            var label = AgendaUi.Text(" " + c.Name, "BodyText", 13);
            label.Margin = new Thickness(6, 0, 0, 0);
            name.Children.Add(label);
            row.Children.Add(name);
            var meter = Meter.Build(c.Amount, 0, Math.Max(1, report.Categories.Max(x => x.Amount)), AgendaUi.ColorBrush(c.Color), $"{c.Name}: {FinanceUi.Amount(c.Amount, currency)}", 8);
            meter.Margin = new Thickness(8, 0, 12, 0);
            meter.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(meter, 1);
            row.Children.Add(meter);
            var before = previous.GetValueOrDefault(c.CategoryId ?? Guid.Empty);
            var delta = new Change(c.Amount, before);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var value = AgendaUi.Text($"{FinanceUi.Amount(c.Amount, currency)} · {Percent(c.Amount, total)}", "BodyText", 13);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(value);
            var change = AgendaUi.Text(before == 0 ? "novo no período" : $"{(delta.Delta > 0 ? "▲" : delta.Delta < 0 ? "▼" : "=")} {FinanceUi.Amount(delta.Delta, currency, showPlus: true)} vs. anterior", "CaptionText", 11,
                delta.Delta > 0 ? FinanceUi.Res("WarningBrush") : delta.Delta < 0 ? FinanceUi.Res("SuccessBrush") : null);
            change.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(change);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            AutomationProperties.SetName(row, $"{c.Name}: {FinanceUi.Amount(c.Amount, currency)}, {Percent(c.Amount, total)} do total; antes {FinanceUi.Amount(before, currency)}");
            categories.Children.Add(row);
        }

        // Maiores despesas e compromissos.
        var top = new StackPanel();
        if (report.TopExpenses.Count == 0)
        {
            top.Children.Add(Paragraph("Nenhuma despesa no período."));
        }
        foreach (var t in report.TopExpenses)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var v = AgendaUi.Text(FinanceUi.Amount(t.Amount, currency), "BodyText", 13);
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(AgendaUi.Text($"{t.Date.ToString("dd/MM", FinanceUi.PtBr)}  {t.Description}", "BodyText", 13));
            AutomationProperties.SetName(row, $"{t.Description}, {FinanceUi.Date(t.Date)}, {FinanceUi.Amount(t.Amount, currency)}");
            top.Children.Add(row);
        }
        top.Children.Add(Caps("Já comprometido em faturas"));
        if (report.Committed.Count == 0)
        {
            top.Children.Add(AgendaUi.Text("Nenhuma parcela futura lançada.", "CaptionText", 12));
        }
        foreach (var c in report.Committed)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var v = AgendaUi.Text(FinanceUi.Amount(c.Amount, currency), "BodyText", 13);
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(AgendaUi.Text(FinanceUi.MonthTitle(c.Year, c.Month), "BodyText", 13));
            top.Children.Add(row);
        }
        ListPanel.Children.Add(SectionTitle("Onde o dinheiro foi", "categorias e maiores gastos do período"));
        ListPanel.Children.Add(Columns(
            (Panel("Gastos por categoria", categories, "Despesas menos estornos por categoria principal, comparadas com o período anterior de mesma duração.", null, ("", FinanceColors.Pink)), 3),
            (Panel("Maiores despesas", top, "As 10 maiores despesas do período e o que já está lançado em faturas futuras de cartão.", null, ("", FinanceColors.Red)), 2)));

        // Exportação e backup.
        ListPanel.Children.Add(SectionTitle("Seus dados", "exportar, guardar e restaurar"));
        ListPanel.Children.Add(Columns((Panel("Exportar e proteger", DataPanel(from, to), "Exportar e fazer backup são sempre ações suas; nada é enviado para fora do computador.", null, ("", FinanceColors.Teal)), 1)));
        FooterText.Text = "Relatórios calculados dos seus lançamentos, na moeda base. Use \"Ver como tabela\" para ler os números sem gráfico.";
    }

    private StackPanel DataPanel(DateOnly from, DateOnly to)
    {
        var body = new StackPanel();
        var export = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Exportar lançamentos do período (CSV)…", Margin = new Thickness(0, 4, 8, 6), HorizontalAlignment = HorizontalAlignment.Left };
        export.Click += (_, _) => ExportCsv(from, to);
        body.Children.Add(export);
        body.Children.Add(Wrap(AgendaUi.Text("O CSV é texto aberto (qualquer um que tiver o arquivo lê os valores). Abre no Excel e no LibreOffice; fórmulas maliciosas são neutralizadas.", "CaptionText", 11.5)));
        if (_backup is null)
        {
            return body;
        }
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Fazer backup protegido…", Margin = new Thickness(0, 0, 8, 6) };
        save.Click += (_, _) => ExportBackup();
        var restore = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Restaurar backup…", Margin = new Thickness(0, 0, 8, 6) };
        restore.Click += (_, _) => RestoreBackup();
        row.Children.Add(save);
        row.Children.Add(restore);
        body.Children.Add(row);
        body.Children.Add(Wrap(AgendaUi.Text("Backup cifrado com a senha que você escolher (AES-256). Bom para guardar em pendrive ou nuvem. Sem a senha, ninguém abre — nem você: anote-a num lugar seguro.", "CaptionText", 11.5)));
        if (_backup.HasPendingRestore)
        {
            var pending = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var cancel = Chip("Cancelar restauração", (_, _) =>
            {
                _backup.CancelPendingRestore();
                Render();
            });
            DockPanel.SetDock(cancel, Dock.Right);
            pending.Children.Add(cancel);
            pending.Children.Add(Wrap(AgendaUi.Text("⚠ Restauração pendente: feche o Diary Ghost (Sair, na bandeja) e abra de novo para usar o backup. O banco atual será guardado na pasta de backups.", "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
            body.Children.Add(pending);
        }
        return body;
    }

    private void ExportCsv(DateOnly from, DateOnly to)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar lançamentos",
            FileName = $"lancamentos-{from:yyyy-MM-dd}-a-{to:yyyy-MM-dd}.csv",
            Filter = "CSV (*.csv)|*.csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            File.WriteAllText(dialog.FileName, _finance.ExportCsv(from, to), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            FooterText.Text = $"Exportado: {Path.GetFileName(dialog.FileName)} (arquivo sem proteção).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterText.Text = "Não foi possível salvar o arquivo.";
        }
    }

    private void ExportBackup()
    {
        var password = PasswordDialog.Ask(Window.GetWindow(this), "Backup protegido", "Escolha uma senha (mínimo 8 caracteres). Ela não é guardada: sem ela o backup não abre.", confirm: true);
        if (password is null)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salvar backup protegido",
            FileName = $"diaryghost-financas-{_finance.Today:yyyy-MM-dd}.dgfin",
            Filter = "Backup do Diary Ghost (*.dgfin)|*.dgfin",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            _backup!.Export(dialog.FileName, password);
            FooterText.Text = $"Backup protegido salvo: {Path.GetFileName(dialog.FileName)}.";
        }
        catch (FinanceValidationException ex)
        {
            FooterText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            FooterText.Text = "Não foi possível criar o backup.";
        }
    }

    private void RestoreBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restaurar backup protegido",
            Filter = "Backup do Diary Ghost (*.dgfin)|*.dgfin",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        var password = PasswordDialog.Ask(Window.GetWindow(this), "Restaurar backup", "Senha usada ao criar o backup:", confirm: false);
        if (password is null)
        {
            return;
        }
        try
        {
            var (accounts, transactions) = _backup!.StageRestore(dialog.FileName, password);
            FooterText.Text = $"Backup conferido: {accounts} conta(s), {transactions} lançamento(s). Feche e abra o app para restaurar.";
        }
        catch (FinanceValidationException ex)
        {
            FooterText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterText.Text = "Não foi possível ler o arquivo.";
        }
        Render();
    }
}

/// <summary>Janela pequena para digitar uma senha (com confirmação opcional). A senha nunca é guardada nem registrada.</summary>
internal sealed class PasswordDialog : Window
{
    private readonly PasswordBox _first = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly PasswordBox _second = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _error;
    private readonly bool _confirm;

    private PasswordDialog(Window? owner, string title, string prompt, bool confirm)
    {
        _confirm = confirm;
        Title = title;
        Owner = owner;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        var stack = new StackPanel { Margin = new Thickness(20) };
        var text = AgendaUi.Text(prompt, "BodyText", 13);
        text.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(text);
        AutomationProperties.SetName(_first, "Senha");
        stack.Children.Add(_first);
        if (confirm)
        {
            stack.Children.Add(AgendaUi.Text("Repita a senha", "CaptionText", 12));
            AutomationProperties.SetName(_second, "Repita a senha");
            stack.Children.Add(_second);
        }
        _error = AgendaUi.Text("", "CaptionText", 12, FinanceUi.Res("DangerBrush"));
        _error.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Continuar", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Cancelar", IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) => _first.Focus();
    }

    private string? Result { get; set; }

    private void Accept()
    {
        if (_first.Password.Length < ProtectedBackup.MinPasswordLength)
        {
            _error.Text = $"Use pelo menos {ProtectedBackup.MinPasswordLength} caracteres.";
            return;
        }
        if (_confirm && _first.Password != _second.Password)
        {
            _error.Text = "As senhas não são iguais.";
            return;
        }
        Result = _first.Password;
        DialogResult = true;
    }

    public static string? Ask(Window? owner, string title, string prompt, bool confirm)
    {
        var dialog = new PasswordDialog(owner, title, prompt, confirm);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}

public partial class FinancePage
{
    /// <summary>Regras "descrição contém → categoria" (usadas na importação e sob demanda).</summary>
    private void RenderRules()
    {
        var apply = Chip("Aplicar aos sem categoria", (_, _) =>
        {
            var count = _finance.ApplyRulesToUncategorized();
            FooterText.Text = count == 0 ? "Nenhum lançamento sem categoria combinou com as regras." : $"{count} lançamento(s) categorizado(s) pelas regras.";
        });
        AutomationProperties.SetName(apply, "Aplicar as regras aos lançamentos sem categoria");
        ListPanel.Children.Add(SectionHeader("Regras de categorização", apply));
        ListPanel.Children.Add(Wrap(AgendaUi.Text("Quando a descrição contém o texto, a categoria é sugerida na importação de extratos (antes do histórico). Sem diferenciar maiúsculas e acentos.", "CaptionText", 12)));

        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        var pattern = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 6), MaxLength = FinanceService.MaxNameLength };
        AutomationProperties.SetName(pattern, "Texto que a descrição contém (ex.: UBER)");
        var choices = FinanceUi.CategoryChoices(_finance, CategoryKind.Expense, "Escolha a categoria").Skip(1)
            .Concat(FinanceUi.CategoryChoices(_finance, CategoryKind.Income, "").Skip(1).Select(c => c with { Label = "↑ " + c.Label.Trim() })).ToList();
        var category = new ComboBox { ItemsSource = choices, Width = 230, Margin = new Thickness(0, 0, 8, 6), SelectedIndex = choices.Count > 0 ? 0 : -1 };
        AutomationProperties.SetName(category, "Categoria da regra");
        var save = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "+ Regra", Margin = new Thickness(0, 0, 8, 6) };
        save.Click += (_, _) =>
        {
            if ((category.SelectedItem as Choice<Guid?>)?.Value is not { } id)
            {
                FooterText.Text = "Escolha a categoria.";
                return;
            }
            Try(() => _finance.SaveRule(null, pattern.Text, id));
        };
        add.Children.Add(pattern);
        add.Children.Add(category);
        add.Children.Add(save);
        ListPanel.Children.Add(add);
        foreach (var rule in _finance.Rules)
        {
            var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 8, 4) };
            body.Children.Add(AgendaUi.Text($"“{rule.Pattern}”", "BodyText", 13));
            body.Children.Add(AgendaUi.Text("  →  " + _finance.CategoryPath(rule.CategoryId), "CaptionText", 12.5));
            var actions = new StackPanel();
            var r = rule;
            actions.Children.Add(AgendaUi.IconButton("\uE74D", $"Excluir a regra {rule.Pattern}", (_, _) => _finance.DeleteRule(r.Id)));
            ListPanel.Children.Add(ActionRow(new Border { Width = 0 }, body, null, actions, $"Regra: descrição contém {rule.Pattern}, categoria {_finance.CategoryPath(rule.CategoryId)}", () => { }));
        }
    }
}
