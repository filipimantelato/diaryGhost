using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopHub.App.Agenda;
using DesktopHub.App.Finance;
using DesktopHub.Core.Finance;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Importar extrato (Fase 12): arquivo → mapeamento das colunas (CSV) → pré-visualização com duplicatas marcadas →
/// importar. O arquivo é só lido (nada é executado nem enviado); lotes podem ser desfeitos inteiros.
/// </summary>
public partial class FinancePage
{
    private enum ImportStep
    {
        Pick,
        Map,
        Preview,
        Done,
    }

    private const int MaxPreviewRows = 400;

    private ImportStep _importStep = ImportStep.Pick;
    private Guid? _importAccount;
    private string? _importFile;
    private string? _importSha;
    private string? _importText;
    private ImportFormat _importFormat;
    private CsvLayout? _importLayout;
    private ParsedStatement? _importParsed;
    private List<ImportCandidate> _importCandidates = [];
    private bool[] _importInclude = [];
    private Guid? _importExpenseCategory;
    private Guid? _importIncomeCategory;
    private ImportBatch? _importResult;
    private Guid? _confirmUndo;

    private void RenderImport()
    {
        switch (_importStep)
        {
            case ImportStep.Map:
                RenderImportMap();
                break;
            case ImportStep.Preview:
                RenderImportPreview();
                break;
            case ImportStep.Done:
                RenderImportDone();
                break;
            default:
                RenderImportPick();
                break;
        }
        RenderImportHistory();
        FooterText.Text = "O arquivo é lido só neste computador. Nada é executado nem enviado; senhas de banco nunca são pedidas.";
    }

    private void ResetImport()
    {
        _importStep = ImportStep.Pick;
        _importFile = _importSha = _importText = null;
        _importLayout = null;
        _importParsed = null;
        _importCandidates = [];
        _importInclude = [];
        _importResult = null;
    }

    private static TextBlock StepTitle(int number, int total, string text)
    {
        var t = AgendaUi.Text($"{number} de {total} · {text}", "CaptionText", 12);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    // ---------- 1. Arquivo ----------

    private void RenderImportPick()
    {
        var body = new StackPanel();
        body.Children.Add(StepTitle(1, 3, "Conta e arquivo"));
        body.Children.Add(Wrap(AgendaUi.Text("Baixe o extrato no site ou app do seu banco em OFX (Money/Quicken) ou CSV e escolha o arquivo. " +
            "O Diary Ghost não se conecta ao banco.", "BodyText", 13)));
        var accounts = FinanceUi.AccountChoices(_finance);
        if (accounts.Count == 0)
        {
            body.Children.Add(Paragraph("Cadastre uma conta antes de importar."));
            ListPanel.Children.Add(Columns((Panel("Importar extrato", body, "Importação de extratos bancários."), 1)));
            return;
        }
        _importAccount ??= accounts[0].Value;
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(AgendaUi.Text("Conta do extrato", "CaptionText", 12));
        var combo = new ComboBox { ItemsSource = accounts, Width = 240, Margin = new Thickness(10, 0, 12, 0) };
        combo.SelectedIndex = Math.Max(0, accounts.FindIndex(a => a.Value == _importAccount));
        AutomationProperties.SetName(combo, "Conta do extrato");
        combo.SelectionChanged += (_, _) => _importAccount = (combo.SelectedItem as Choice<Guid>)?.Value;
        row.Children.Add(combo);
        var pick = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Escolher arquivo…" };
        AutomationProperties.SetName(pick, "Escolher arquivo de extrato (CSV ou OFX)");
        pick.Click += (_, _) => PickImportFile();
        row.Children.Add(pick);
        body.Children.Add(row);
        ListPanel.Children.Add(Columns((Panel("Importar extrato", body, "CSV com vírgula decimal, ponto e vírgula e datas dd/mm/aaaa; OFX 1.x e 2.x. Até 20 MB."), 1)));
    }

    private void PickImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolher extrato",
            Filter = "Extratos (*.ofx;*.csv;*.txt)|*.ofx;*.csv;*.txt|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            if (new FileInfo(dialog.FileName).Length > StatementParser.MaxBytes)
            {
                throw new FinanceValidationException("Arquivo grande demais (máximo 20 MB).");
            }
            var bytes = File.ReadAllBytes(dialog.FileName);
            _importSha = StatementParser.Sha256(bytes);
            _importText = StatementParser.Decode(bytes);
            _importFile = Path.GetFileName(dialog.FileName);
            if (StatementParser.LooksLikeOfx(_importText))
            {
                _importFormat = ImportFormat.Ofx;
                _importParsed = StatementParser.ParseOfx(_importText);
                BuildImportPreview();
            }
            else
            {
                _importFormat = ImportFormat.Csv;
                _importLayout = StatementParser.Detect(_importText);
                _importStep = ImportStep.Map;
            }
        }
        catch (Exception ex) when (ex is FinanceValidationException or IOException or UnauthorizedAccessException)
        {
            ResetImport();
            FooterText.Text = ex is FinanceValidationException ? ex.Message : "Não foi possível ler o arquivo.";
            return;
        }
        Render();
    }

    // ---------- 2. Colunas (CSV) ----------

    private void RenderImportMap()
    {
        var layout = _importLayout!;
        var rows = StatementParser.SplitCsv(string.Join('\n', _importText!.Split('\n').Take(8)), layout.Delimiter);
        var width = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
        var names = Enumerable.Range(0, width).Select(c => layout.HasHeader && rows.Count > 0 && c < rows[0].Length && rows[0][c].Trim().Length > 0
            ? $"{c + 1}. {rows[0][c].Trim()}" : $"Coluna {c + 1}").ToList();
        var body = new StackPanel();
        body.Children.Add(StepTitle(2, 3, $"Colunas de {_importFile}"));
        body.Children.Add(Wrap(AgendaUi.Text("Confira o palpite: qual coluna é a data, a descrição e o valor. A prévia abaixo mostra as primeiras linhas do arquivo.", "BodyText", 13)));

        var grid = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        ComboBox Field<T>(string label, List<Choice<T>> items, T selected, Action<T> set)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 10), Width = 190 };
            box.Children.Add(AgendaUi.Text(label, "CaptionText", 11.5));
            var combo = new ComboBox { ItemsSource = items, Margin = new Thickness(0, 4, 0, 0) };
            combo.SelectedIndex = Math.Max(0, items.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
            AutomationProperties.SetName(combo, label);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is Choice<T> c)
                {
                    set(c.Value);
                    Render();
                }
            };
            box.Children.Add(combo);
            grid.Children.Add(box);
            return combo;
        }
        List<Choice<int>> columns = [.. names.Select((n, i) => new Choice<int>(n, i))];
        List<Choice<int?>> optional = [new("—", null), .. names.Select((n, i) => new Choice<int?>(n, i))];
        Field("Separador", [new("Ponto e vírgula (;)", ';'), new("Vírgula (,)", ','), new("Tabulação", '\t'), new("Barra (|)", '|')], layout.Delimiter, v => _importLayout = layout with { Delimiter = v });
        Field("Data", columns, layout.DateColumn, v => _importLayout = layout with { DateColumn = v });
        Field("Formato da data", [new("dd/mm/aaaa", "dd/MM/yyyy"), new("dd/mm/aa", "dd/MM/yy"), new("aaaa-mm-dd", "yyyy-MM-dd"), new("dd-mm-aaaa", "dd-MM-yyyy"), new("dd.mm.aaaa", "dd.MM.yyyy")],
            layout.DateFormat, v => _importLayout = layout with { DateFormat = v });
        Field("Descrição", columns, layout.DescriptionColumn, v => _importLayout = layout with { DescriptionColumn = v });
        Field("Valor (com sinal)", optional, layout.AmountColumn, v => _importLayout = layout with { AmountColumn = v, DebitColumn = v is null ? layout.DebitColumn : null, CreditColumn = v is null ? layout.CreditColumn : null });
        if (layout.AmountColumn is null)
        {
            Field("Débito (saídas)", optional, layout.DebitColumn, v => _importLayout = layout with { DebitColumn = v });
            Field("Crédito (entradas)", optional, layout.CreditColumn, v => _importLayout = layout with { CreditColumn = v });
        }
        Field("Decimal", [new("Vírgula (1.234,56)", true), new("Ponto (1,234.56)", false)], layout.DecimalComma, v => _importLayout = layout with { DecimalComma = v });
        body.Children.Add(grid);
        var options = new WrapPanel();
        var header = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), Content = "Primeira linha é cabeçalho", IsChecked = layout.HasHeader, Margin = new Thickness(0, 0, 20, 0) };
        header.Click += (_, _) =>
        {
            _importLayout = layout with { HasHeader = header.IsChecked == true };
            Render();
        };
        options.Children.Add(header);
        var invert = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), Content = "Saídas vêm positivas (inverter sinal)", IsChecked = layout.InvertSign };
        invert.Click += (_, _) =>
        {
            _importLayout = layout with { InvertSign = invert.IsChecked == true };
            Render();
        };
        options.Children.Add(invert);
        body.Children.Add(options);

        body.Children.Add(Caps("Primeiras linhas do arquivo"));
        body.Children.Add(FinanceCharts.Table([.. names.Select(n => n.Length > 22 ? n[..22] + "…" : n)],
            rows.Skip(layout.HasHeader ? 1 : 0).Take(5).Select(r => Enumerable.Range(0, width).Select(c => c < r.Length ? (r[c].Length > 30 ? r[c][..30] + "…" : r[c]) : "").ToArray())));

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var back = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Voltar", Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) =>
        {
            ResetImport();
            Render();
        };
        var next = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Pré-visualizar" };
        next.Click += (_, _) =>
        {
            try
            {
                _importParsed = StatementParser.ParseCsv(_importText!, _importLayout!);
                BuildImportPreview();
            }
            catch (FinanceValidationException ex)
            {
                FooterText.Text = ex.Message;
                return;
            }
            Render();
        };
        buttons.Children.Add(back);
        buttons.Children.Add(next);
        body.Children.Add(buttons);
        ListPanel.Children.Add(Columns((Panel("Importar extrato", body, "Mapeamento das colunas do CSV."), 1)));
    }

    // ---------- 3. Pré-visualização ----------

    private void BuildImportPreview()
    {
        _importCandidates = [.. _finance.PreviewImport(_importAccount!.Value, _importParsed!.Rows)];
        _importInclude = [.. _importCandidates.Select(c => c.IncludedByDefault)];
        _importStep = ImportStep.Preview;
    }

    private void RenderImportPreview()
    {
        var currency = _finance.Account(_importAccount)?.Currency ?? _finance.BaseCurrency;
        var body = new StackPanel();
        body.Children.Add(StepTitle(_importFormat == ImportFormat.Csv ? 3 : 2, _importFormat == ImportFormat.Csv ? 3 : 2, $"Conferir {_importFile} → {_finance.Account(_importAccount)?.Name}"));
        var counts = _importCandidates.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
        var summary = $"{counts.GetValueOrDefault(ImportRowStatus.New)} novos · {counts.GetValueOrDefault(ImportRowStatus.PossibleDuplicate)} possíveis duplicatas (desmarcados) · " +
            $"{counts.GetValueOrDefault(ImportRowStatus.AlreadyImported)} já importados" +
            (counts.GetValueOrDefault(ImportRowStatus.BeforeOpening) > 0 ? $" · {counts[ImportRowStatus.BeforeOpening]} antes do saldo inicial da conta" : "");
        body.Children.Add(Wrap(AgendaUi.Text(summary, "BodyText", 13)));
        if (_importSha is { } sha && _finance.PreviousImport(sha) is { } previous)
        {
            body.Children.Add(Wrap(AgendaUi.Text($"⚠ Este mesmo arquivo já foi importado em {FinanceUi.Date(DateOnly.FromDateTime(previous.CreatedAt.LocalDateTime))} ({previous.Imported} lançamentos). As linhas repetidas aparecem como \"já importado\".",
                "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
        }
        if (_importParsed is { Currency: { } c } && c != currency)
        {
            body.Children.Add(Wrap(AgendaUi.Text($"⚠ O extrato é em {c} e a conta em {currency}: confira se escolheu a conta certa.", "CaptionText", 12, FinanceUi.Res("WarningBrush"))));
        }
        if (_importParsed is { AccountHint: { } hint })
        {
            body.Children.Add(AgendaUi.Text($"Conta no arquivo termina em {hint}.", "CaptionText", 11.5));
        }
        if (_importParsed is { Errors.Count: > 0 } parsed)
        {
            body.Children.Add(Wrap(AgendaUi.Text($"{parsed.Errors.Count} linha(s) ignorada(s): " + string.Join(" ", parsed.Errors.Take(4)) + (parsed.Errors.Count > 4 ? " …" : ""), "CaptionText", 11.5, FinanceUi.Res("WarningBrush"))));
        }

        var defaults = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        ComboBox CategoryBox(string label, CategoryKind kind, Guid? selected, Action<Guid?> set)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 230 };
            box.Children.Add(AgendaUi.Text(label, "CaptionText", 11.5));
            var items = FinanceUi.CategoryChoices(_finance, kind, "Sem categoria");
            var combo = new ComboBox { ItemsSource = items, Margin = new Thickness(0, 4, 0, 0) };
            combo.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Value == selected));
            AutomationProperties.SetName(combo, label);
            combo.SelectionChanged += (_, _) => set((combo.SelectedItem as Choice<Guid?>)?.Value);
            box.Children.Add(combo);
            defaults.Children.Add(box);
            return combo;
        }
        CategoryBox("Saídas sem sugestão", CategoryKind.Expense, _importExpenseCategory, v => _importExpenseCategory = v);
        CategoryBox("Entradas sem sugestão", CategoryKind.Income, _importIncomeCategory, v => _importIncomeCategory = v);
        body.Children.Add(defaults);
        body.Children.Add(AgendaUi.Text("A categoria sugerida vem do último lançamento com a mesma descrição.", "CaptionText", 11.5));

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < _importCandidates.Count && i < MaxPreviewRows; i++)
        {
            list.Children.Add(ImportRow(i, currency));
        }
        if (_importCandidates.Count > MaxPreviewRows)
        {
            list.Children.Add(AgendaUi.Text($"+ {_importCandidates.Count - MaxPreviewRows} linhas não exibidas (entram conforme a situação: novas marcadas, demais desmarcadas).", "CaptionText", 11.5));
        }
        body.Children.Add(list);

        var selected = _importInclude.Count(x => x);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var back = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Voltar", Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) =>
        {
            _importStep = _importFormat == ImportFormat.Csv ? ImportStep.Map : ImportStep.Pick;
            if (_importStep == ImportStep.Pick)
            {
                ResetImport();
            }
            Render();
        };
        var go = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = $"Importar {selected} lançamento(s)", IsEnabled = selected > 0 };
        go.Click += (_, _) => DoImport();
        buttons.Children.Add(back);
        buttons.Children.Add(go);
        body.Children.Add(buttons);
        ListPanel.Children.Add(Columns((Panel("Importar extrato", body, "Linhas novas vêm marcadas; possíveis duplicatas (mesmo valor, até 2 dias de um lançamento já existente) vêm desmarcadas; já importadas não podem entrar."), 1)));
    }

    private Grid ImportRow(int index, string currency)
    {
        var c = _importCandidates[index];
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var locked = c.Status is ImportRowStatus.AlreadyImported or ImportRowStatus.BeforeOpening;
        var status = c.Status switch
        {
            ImportRowStatus.AlreadyImported => "já importado",
            ImportRowStatus.PossibleDuplicate => $"parecido com {(_finance.Find(c.MatchId ?? Guid.Empty) is { } m ? $"\"{m.Description}\" de {m.Date.ToString("dd/MM", FinanceUi.PtBr)}" : "um lançamento")}",
            ImportRowStatus.BeforeOpening => "antes do saldo inicial",
            _ => _finance.Category(c.SuggestedCategoryId)?.Name ?? "novo",
        };
        var check = new CheckBox { IsChecked = _importInclude[index], IsEnabled = !locked, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(check, $"Importar {c.Row.Description} de {FinanceUi.Date(c.Row.Date)}, {FinanceUi.Amount(c.Row.Amount, currency, showPlus: true)}, {status}");
        check.Click += (_, _) =>
        {
            _importInclude[index] = check.IsChecked == true;
            Render();
        };
        row.Children.Add(check);
        var date = AgendaUi.Text(c.Row.Date.ToString("dd/MM/yy", FinanceUi.PtBr), "CaptionText", 12);
        date.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(date, 1);
        row.Children.Add(date);
        var description = AgendaUi.Text(c.Row.Description, "BodyText", 12.5, locked ? FinanceUi.Res("TextTertiaryBrush") : null);
        description.VerticalAlignment = VerticalAlignment.Center;
        description.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(description, 2);
        row.Children.Add(description);
        var statusText = AgendaUi.Text(status, "CaptionText", 11.5, c.Status == ImportRowStatus.PossibleDuplicate ? FinanceUi.Res("WarningBrush") : null);
        statusText.VerticalAlignment = VerticalAlignment.Center;
        statusText.ToolTip = status;
        Grid.SetColumn(statusText, 3);
        row.Children.Add(statusText);
        var amount = AgendaUi.Text(FinanceUi.Amount(c.Row.Amount, currency, showPlus: true), "BodyText", 12.5, c.Row.Amount > 0 ? FinanceUi.Res("SuccessBrush") : null);
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(amount, 4);
        row.Children.Add(amount);
        return row;
    }

    private void DoImport()
    {
        var chosen = _importCandidates.Where((_, i) => _importInclude[i]).ToList();
        try
        {
            _importResult = _finance.Import(_importAccount!.Value, _importFormat, _importFile ?? "extrato", _importSha!, chosen,
                c => c.SuggestedCategoryId ?? (c.Row.Amount > 0 ? _importIncomeCategory : _importExpenseCategory),
                skipped: _importCandidates.Count - chosen.Count);
        }
        catch (FinanceValidationException ex)
        {
            FooterText.Text = ex.Message;
            return;
        }
        _importStep = ImportStep.Done;
        Render();
    }

    // ---------- Fim e histórico ----------

    private void RenderImportDone()
    {
        var body = new StackPanel();
        var result = _importResult!;
        body.Children.Add(AgendaUi.Text($"✓ {result.Imported} lançamento(s) importado(s) em {_finance.Account(result.AccountId)?.Name}" +
            (result.Skipped > 0 ? $"; {result.Skipped} deixado(s) de fora." : "."), "BodyText", 14, FinanceUi.Res("SuccessBrush")));
        body.Children.Add(Paragraph("Se algo não ficou certo, desfaça o lote inteiro no histórico abaixo."));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var view = new Button { Style = (Style)Application.Current.FindResource("AccentButton"), Content = "Ver lançamentos", Margin = new Thickness(0, 0, 8, 0) };
        view.Click += (_, _) =>
        {
            var account = result.AccountId;
            ResetImport();
            ShowAccountTransactions(account);
        };
        var another = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Importar outro arquivo" };
        another.Click += (_, _) =>
        {
            ResetImport();
            Render();
        };
        buttons.Children.Add(view);
        buttons.Children.Add(another);
        body.Children.Add(buttons);
        ListPanel.Children.Add(Columns((Panel("Importar extrato", body, "Importação concluída."), 1)));
    }

    private void RenderImportHistory()
    {
        var batches = _finance.ImportBatches;
        if (batches.Count == 0)
        {
            return;
        }
        var body = new StackPanel();
        foreach (var b in batches.Take(20))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var current = _finance.BatchTransactions(b.Id).Count;
            if (b.UndoneAt is null)
            {
                var confirming = _confirmUndo == b.Id;
                var undo = Chip(confirming ? $"Confirmar: remover {current} lançamento(s)" : "Desfazer", (_, _) =>
                {
                    if (_confirmUndo == b.Id)
                    {
                        _confirmUndo = null;
                        Try(() => FooterText.Text = $"{_finance.UndoImport(b.Id)} lançamento(s) removido(s).");
                    }
                    else
                    {
                        _confirmUndo = b.Id;
                    }
                    Render();
                }, confirming);
                AutomationProperties.SetName(undo, confirming ? $"Confirmar desfazer a importação de {b.FileName}: remove {current} lançamentos" : $"Desfazer a importação de {b.FileName}");
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
            }
            var when = b.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
            var text = $"{when} · {b.FileName} · {_finance.Account(b.AccountId)?.Name} · {b.Imported} importado(s)" +
                (b.Skipped > 0 ? $", {b.Skipped} de fora" : "") + (b.UndoneAt is { } u ? $" · desfeito em {u.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.GetCultureInfo("pt-BR"))}" : "");
            row.Children.Add(Wrap(AgendaUi.Text(text, "CaptionText", 12, b.UndoneAt is null ? null : FinanceUi.Res("TextTertiaryBrush"))));
            AutomationProperties.SetName(row, text);
            body.Children.Add(row);
        }
        ListPanel.Children.Add(Columns((Panel("Importações anteriores", body, "Desfazer remove todos os lançamentos do lote, inclusive os que você editou depois."), 1)));
    }
}
