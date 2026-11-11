using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Finance;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Finance;

/// <summary>Textos, cores e o modo privacidade do módulo de finanças.</summary>
internal static class FinanceUi
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly string[] Palette = ["#9A86C4", "#6B9AC4", "#5FB3A8", "#8FB573", "#C9A94E", "#C47A6B", "#C48FB0", "#8A8A8A"];

    private static bool s_privacy;
    private static SettingsService? s_settings;

    /// <summary>Liga o modo privacidade às configurações (fica salvo entre sessões). Chamado na thread de UI.</summary>
    public static void Attach(SettingsService settings, IEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        s_settings = settings;
        bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Finance)
            {
                PrivacyChanged?.Invoke();
            }
        }, SynchronizationContext.Current);
    }

    /// <summary>Modo privacidade: oculta valores em todas as telas do módulo e no dashboard (percentuais e barras continuam).</summary>
    public static bool Privacy
    {
        get => s_settings?.Finance.HideValues ?? s_privacy;
        set
        {
            if (s_settings is { } settings)
            {
                settings.UpdateFinance(f => f with { HideValues = value });
            }
            else if (s_privacy != value)
            {
                s_privacy = value;
                PrivacyChanged?.Invoke();
            }
        }
    }

    private static long s_margin;

    /// <summary>Margem de segurança do "quanto posso reservar" (salva nas configurações).</summary>
    public static long SafetyMargin
    {
        get => s_settings?.Finance.SafetyMargin ?? s_margin;
        set
        {
            if (s_settings is { } settings)
            {
                settings.UpdateFinance(f => f with { SafetyMargin = Math.Max(0, value) });
            }
            else if (s_margin != value)
            {
                s_margin = Math.Max(0, value);
                PrivacyChanged?.Invoke();
            }
        }
    }

    public static event Action? PrivacyChanged;

    public static string Amount(long minor, string currency = Money.DefaultCurrency, bool showPlus = false) =>
        Privacy ? Money.Masked(currency) : Money.Format(minor, currency, showPlus);

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", PtBr);

    public static string DayTitle(DateOnly d, DateOnly today)
    {
        var text = d.ToString("ddd, d 'de' MMMM", PtBr);
        text = char.ToUpper(text[0], PtBr) + text[1..];
        if (d == today)
        {
            return "Hoje · " + text;
        }
        return d == today.AddDays(-1) ? "Ontem · " + text : text;
    }

    public static string MonthTitle(int year, int month)
    {
        var text = new DateTime(year, month, 1).ToString("MMMM 'de' yyyy", PtBr);
        return char.ToUpper(text[0], PtBr) + text[1..];
    }

    public static bool TryParseDate(string? text, DateOnly today, out DateOnly date)
    {
        date = default;
        var t = (text ?? "").Trim().ToLower(PtBr);
        if (t == "ontem")
        {
            date = today.AddDays(-1);
            return true;
        }
        if (AgendaUi.TryParseDate(t, today.ToDateTime(TimeOnly.MinValue), out var parsed))
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }
        return false;
    }

    public static string Kind(TransactionKind kind) => kind switch
    {
        TransactionKind.Income => "Receita",
        TransactionKind.Expense => "Despesa",
        TransactionKind.Transfer => "Transferência",
        TransactionKind.Refund => "Estorno",
        TransactionKind.Adjustment => "Ajuste de saldo",
        _ => kind.ToString(),
    };

    /// <summary>Tipo para exibição, distinguindo lançamentos de cartão.</summary>
    public static string Kind(FinanceTransaction t) => t switch
    {
        { IsCardPayment: true } => "Pagamento de fatura",
        { PurchaseId: not null } => "Compra no cartão",
        { Kind: TransactionKind.Refund, CardId: not null } => "Estorno no cartão",
        _ => Kind(t.Kind),
    };

    public static string StatementStatus(StatementStatus status) => status switch
    {
        Core.Finance.StatementStatus.Future => "futura",
        Core.Finance.StatementStatus.Open => "aberta",
        Core.Finance.StatementStatus.Closed => "fechada",
        Core.Finance.StatementStatus.Paid => "paga",
        Core.Finance.StatementStatus.PartiallyPaid => "paga em parte",
        _ => "vencida",
    };

    public static string Kind(AccountKind kind) => kind switch
    {
        AccountKind.Checking => "Conta corrente",
        AccountKind.Savings => "Poupança",
        AccountKind.DigitalWallet => "Carteira digital",
        AccountKind.Cash => "Dinheiro",
        AccountKind.Investment => "Investimentos",
        _ => "Outra",
    };

    public static string Method(PaymentMethod method) => method switch
    {
        PaymentMethod.None => "Não informada",
        PaymentMethod.Pix => "Pix",
        PaymentMethod.Debit => "Débito",
        PaymentMethod.Cash => "Dinheiro",
        PaymentMethod.BankSlip => "Boleto",
        PaymentMethod.BankTransfer => "Transferência/TED",
        PaymentMethod.Credit => "Crédito (sem cartão cadastrado)",
        _ => "Outra",
    };

    public static string Glyph(TransactionKind kind) => kind switch
    {
        TransactionKind.Income => "",      // seta para cima
        TransactionKind.Expense => "",     // seta para baixo
        TransactionKind.Transfer => "",    // trocar
        TransactionKind.Refund => "",      // desfazer
        _ => "",                           // editar (ajuste)
    };

    public static string Glyph(AccountKind kind) => kind switch
    {
        AccountKind.Savings => "",
        AccountKind.DigitalWallet => "",
        AccountKind.Cash => "",
        AccountKind.Investment => "",
        _ => "",
    };

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Cor do valor: entrada em verde com "+", saída no texto normal com "−" (a cor nunca é o único sinal).</summary>
    public static Brush AmountBrush(long signed) => signed > 0 ? Res("SuccessBrush") : Res("TextPrimaryBrush");

    /// <summary>Efeito do lançamento visto de uma conta (ou do conjunto, sem conta: transferências valem 0).</summary>
    public static long SignedFor(FinanceTransaction t, Guid? accountId) =>
        accountId is { } id ? t.Postings.Where(p => p.AccountId == id || p.CardId == id).Sum(p => p.Amount)
        : t.Kind == TransactionKind.Transfer ? 0 : t.Postings.Sum(p => p.Amount);

    public static List<Choice<Guid?>> CategoryChoices(FinanceService finance, CategoryKind kind, string none, Guid? keep = null)
    {
        var list = new List<Choice<Guid?>> { new(none, null) };
        foreach (var c in finance.OrderedCategories(kind, includeArchived: true).Where(c => !c.IsArchived || c.Id == keep))
        {
            list.Add(new((c.ParentId is null ? "" : "      ") + c.Name + (c.IsArchived ? " (arquivada)" : ""), c.Id));
        }
        return list;
    }

    public static List<Choice<Guid>> AccountChoices(FinanceService finance, Guid? keep = null) =>
        [.. finance.Accounts.Where(a => !a.IsArchived || a.Id == keep)
            .Select(a => new Choice<Guid>(a.Name + (a.Currency != finance.BaseCurrency ? $" ({a.Currency})" : "") + (a.IsArchived ? " (arquivada)" : ""), a.Id))];

    public static Border ColorDot(string? hex, double size = 10) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = AgendaUi.ColorBrush(hex),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Linha de chips de cor; devolve o painel e uma função que lê a cor escolhida.</summary>
    public static (WrapPanel Panel, Func<string> Selected) ColorPicker(string initial)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var selected = Palette.Contains(initial) ? initial : Palette[0];
        var buttons = new List<(Button Button, string Hex)>();
        void Refresh()
        {
            foreach (var (button, hex) in buttons)
            {
                button.BorderBrush = hex == selected ? Res("TextPrimaryBrush") : Brushes.Transparent;
            }
        }
        foreach (var hex in Palette)
        {
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Background = AgendaUi.ColorBrush(hex),
                BorderThickness = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ColorButtonTemplate(),
                ToolTip = hex,
            };
            AutomationProperties.SetName(button, $"Cor {Array.IndexOf(Palette, hex) + 1}");
            button.Click += (_, _) =>
            {
                selected = hex;
                Refresh();
            };
            buttons.Add((button, hex));
            panel.Children.Add(button);
        }
        Refresh();
        return (panel, () => selected);
    }

    private static ControlTemplate? s_colorTemplate;

    private static ControlTemplate ColorButtonTemplate()
    {
        if (s_colorTemplate is not null)
        {
            return s_colorTemplate;
        }
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        s_colorTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
        s_colorTemplate.Seal();
        return s_colorTemplate;
    }
}

/// <summary>
/// Painel lateral de edição (mesmo visual dos editores da agenda), montado em código: título, campos,
/// mensagem de erro e botões. As subclasses só declaram os campos e o que fazer ao salvar.
/// </summary>
internal abstract class FinanceForm : UserControl
{
    private readonly StackPanel _fields = new() { Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _error;
    private readonly WrapPanel _buttons = new();
    private readonly DockPanel _footer = new();

    public event Action? Done;

    protected FinanceForm(string title)
    {
        _error = AgendaUi.Text("", "CaptionText", foreground: FinanceUi.Res("DangerBrush"));
        _error.TextWrapping = TextWrapping.Wrap;
        _error.TextTrimming = TextTrimming.None;
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        var header = AgendaUi.Text(title, "SubtitleText");
        _fields.Children.Add(header);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_error);
        _footer.Children.Add(_buttons);
        bottom.Children.Add(_footer);

        var dock = new DockPanel();
        dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(18, 16, 18, 16), Child = dock };
    }

    protected void Finish() => Done?.Invoke();

    protected void ShowError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected TextBlock Label(string text)
    {
        var label = AgendaUi.Text(text, "CaptionText");
        label.Margin = new Thickness(0, 12, 0, 4);
        _fields.Children.Add(label);
        return label;
    }

    protected TextBlock Hint(string text)
    {
        var hint = AgendaUi.Text(text, "CaptionText", 11.5);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 4, 0, 0);
        _fields.Children.Add(hint);
        return hint;
    }

    protected T Add<T>(T element) where T : UIElement
    {
        _fields.Children.Add(element);
        return element;
    }

    protected TextBox TextField(string label, string value, string automationName, int maxLength = 200, bool multiline = false)
    {
        Label(label);
        var box = new TextBox { Text = value, MaxLength = maxLength };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 60;
            box.VerticalContentAlignment = VerticalAlignment.Top;
        }
        AutomationProperties.SetName(box, automationName);
        _fields.Children.Add(box);
        return box;
    }

    protected ComboBox Combo<T>(string label, List<Choice<T>> items, T selected, string automationName)
    {
        Label(label);
        var combo = new ComboBox { ItemsSource = items };
        combo.SelectedIndex = Math.Max(0, items.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        AutomationProperties.SetName(combo, automationName);
        _fields.Children.Add(combo);
        return combo;
    }

    protected static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice<T> c ? c.Value : default;

    protected Button Button(string text, string style, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(0, 0, 8, 6), IsDefault = isDefault, IsCancel = isCancel };
        button.Click += click;
        _buttons.Children.Add(button);
        return button;
    }

    protected void DeleteButton(string automationName, RoutedEventHandler click)
    {
        var button = AgendaUi.IconButton("", automationName, click, size: 32);
        DockPanel.SetDock(button, Dock.Right);
        _footer.Children.Insert(0, button);
    }
}

/// <summary>
/// Cores do módulo Finanças (inspiradas na referência visual: azul como cor principal e cores vivas por tipo de
/// informação). Funcionam nos dois temas; cor nunca é o único sinal (rótulos e sinais acompanham).
/// </summary>
internal static class FinanceColors
{
    public const string Blue = "#2F6BFF";
    public const string Green = "#22A06B";
    public const string Red = "#E5484D";
    public const string Orange = "#F59E0B";
    public const string Purple = "#8B5CF6";
    public const string Teal = "#14B8A6";
    public const string Pink = "#EC4899";
    public const string Cyan = "#06B6D4";
    public const string Gray = "#8A93A2";
}
