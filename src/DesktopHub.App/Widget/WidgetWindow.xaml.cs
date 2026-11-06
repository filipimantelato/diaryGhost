using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopHub.App.Platform;
using DesktopHub.App.Theme;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Windowing;
using Microsoft.Win32;

namespace DesktopHub.App.Widget;

/// <summary>
/// A "ilha" flutuante. Não rouba foco (<c>WS_EX_NOACTIVATE</c>), não aparece no Alt+Tab
/// (<c>WS_EX_TOOLWINDOW</c>) e só renderiza quando algo muda: o relógio acorda uma vez por minuto
/// e somente enquanto o widget está visível.
/// </summary>
public partial class WidgetWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Duration ResizeDuration = TimeSpan.FromMilliseconds(180);
    private const int TopMargin = 8;

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _clock;
    private nint _hwnd;
    private WidgetLayout? _appliedLayout;
    private WidgetPanel? _appliedPanel;
    private bool? _appliedCaptureExclusion;
    private bool _syncingTabs;

    private bool _dragging;
    private (int X, int Y) _dragCursorStart;
    private PixelRect _dragWindowStart;

    internal WidgetWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        _clock = new DispatcherTimer(DispatcherPriority.Background);
        _clock.Tick += (_, _) => UpdateClock();
        IsVisibleChanged += OnIsVisibleChanged;
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Falso quando o Windows recusou a exclusão de capturas (ex.: versão anterior ao 2004).</summary>
    public bool CaptureExclusionSupported { get; private set; } = true;

    internal void Apply(WidgetSettings s)
    {
        Topmost = s.AlwaysOnTop;
        Opacity = s.Opacity;
        PinButton.IsChecked = s.AlwaysOnTop;
        LockButton.IsChecked = s.Locked;
        LockButton.Content = s.Locked ? "" : "";
        Island.Cursor = s.Locked ? null : Cursors.SizeAll;

        if (_appliedPanel != s.Panel)
        {
            _appliedPanel = s.Panel;
            ShowPanel(s.Panel);
        }
        if (_appliedLayout != s.Layout)
        {
            var animate = _appliedLayout is not null && ThemeManager.AnimationsAllowed(s.AnimationsEnabled);
            _appliedLayout = s.Layout;
            ApplyLayout(s.Layout, animate);
        }
        if (_hwnd != 0 && _appliedCaptureExclusion != s.ExcludeFromCapture)
        {
            _appliedCaptureExclusion = s.ExcludeFromCapture;
            CaptureExclusionSupported = WindowInterop.SetExcludeFromCapture(_hwnd, s.ExcludeFromCapture) || !s.ExcludeFromCapture;
        }
    }

    /// <summary>Recoloca o widget no topo central do monitor principal e esquece a posição salva.</summary>
    internal void ResetPosition()
    {
        if (_hwnd == 0)
        {
            return;
        }
        var rect = WindowInterop.GetRect(_hwnd);
        var target = WindowPlacement.TopCenter(WindowInterop.GetPrimaryWorkArea(), rect.Width, rect.Height, TopMargin);
        WindowInterop.MoveTo(_hwnd, target.Left, target.Top);
        _settings.UpdateWidget(w => w with { Position = null });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.AddExtendedStyle(_hwnd, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        RestorePosition();
        Apply(_settings.Widget);
    }

    private void RestorePosition()
    {
        var rect = WindowInterop.GetRect(_hwnd);
        var target = _settings.Widget.Position is { } p
            ? rect.WithPosition(p.X, p.Y)
            : WindowPlacement.TopCenter(WindowInterop.GetPrimaryWorkArea(), rect.Width, rect.Height, TopMargin);
        target = WindowPlacement.EnsureVisible(target, WindowInterop.GetWorkAreas());
        WindowInterop.MoveTo(_hwnd, target.Left, target.Top);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        // Monitor desconectado ou resolução alterada: garante que o widget continue alcançável.
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwnd == 0)
            {
                return;
            }
            var rect = WindowInterop.GetRect(_hwnd);
            var target = WindowPlacement.EnsureVisible(rect, WindowInterop.GetWorkAreas());
            if (target != rect)
            {
                WindowInterop.MoveTo(_hwnd, target.Left, target.Top);
            }
        });

    // ---------- Layout e animação ----------

    private void ApplyLayout(WidgetLayout layout, bool animate)
    {
        var expanded = layout == WidgetLayout.Expanded;
        var (width, height, radius) = expanded ? (420.0, 150.0, 24.0) : (252.0, 44.0, 22.0);
        CompactView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Island.CornerRadius = new CornerRadius(radius);

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, ResizeDuration) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, ResizeDuration) { EasingFunction = ease });
            var content = expanded ? (UIElement)ExpandedView : CompactView;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        }
        else
        {
            // Remove animações anteriores (que "seguram" o valor final) antes de atribuir diretamente.
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = width;
            Island.Height = height;
        }
    }

    private static readonly Dictionary<WidgetPanel, (string Glyph, string Title, string Body, string Badge)> Panels = new()
    {
        [WidgetPanel.Media] = ("", "Mídia", "Sessões do Windows (Spotify, Brave, Chrome…) com escolha explícita da fonte.", "Disponível na Fase 2"),
        [WidgetPanel.Focus] = ("", "Foco", "Cronômetro, temporizador com presets e Pomodoro.", "Disponível na Fase 3"),
        [WidgetPanel.Agenda] = ("", "Agenda", "Próximo compromisso, tempo restante e lembretes.", "Disponível na Fase 4"),
        [WidgetPanel.Info] = ("", "Hoje", "", "Clima e manchetes na Fase 5"),
        [WidgetPanel.Activity] = ("", "Atividade", "Eventos do Claude Code (via hooks) e de outras ferramentas.", "Disponível na Fase 6"),
    };

    private void ShowPanel(WidgetPanel panel)
    {
        var (glyph, title, body, badge) = Panels[panel];
        CompactGlyph.Text = glyph;
        PanelTitle.Text = title;
        PanelBody.Text = panel == WidgetPanel.Info ? FormatToday() : body;
        PanelBadge.Text = badge;

        _syncingTabs = true;
        TabFor(panel).IsChecked = true;
        _syncingTabs = false;
        UpdateClock();
    }

    private RadioButton TabFor(WidgetPanel panel) => panel switch
    {
        WidgetPanel.Media => TabMedia,
        WidgetPanel.Focus => TabFocus,
        WidgetPanel.Agenda => TabAgenda,
        WidgetPanel.Info => TabInfo,
        _ => TabActivity,
    };

    // ---------- Relógio (acorda 1x por minuto, apenas visível) ----------

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            UpdateClock();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        CompactClock.Text = now.ToString("HH:mm", PtBr);
        if (_appliedPanel is { } panel)
        {
            CompactStatus.Text = panel == WidgetPanel.Info ? now.ToString("ddd, d MMM", PtBr) : $"{Panels[panel].Title} · {Panels[panel].Badge.Replace("Disponível na ", "", StringComparison.Ordinal)}";
            if (panel == WidgetPanel.Info)
            {
                PanelBody.Text = FormatToday();
            }
        }

        if (IsVisible)
        {
            // Próxima virada de minuto (+50 ms de folga), em vez de um tick por segundo.
            _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
            _clock.Start();
        }
    }

    private static string FormatToday()
    {
        var text = DateTime.Now.ToString("dddd, d 'de' MMMM", PtBr);
        return char.ToUpper(text[0], PtBr) + text[1..];
    }

    // ---------- Arrastar (pixels físicos, sem ativar a janela) ----------

    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleLayout();
            e.Handled = true;
            return;
        }
        if (_settings.Widget.Locked || _hwnd == 0)
        {
            return;
        }
        _dragCursorStart = WindowInterop.GetCursor();
        _dragWindowStart = WindowInterop.GetRect(_hwnd);
        _dragging = Island.CaptureMouse();
        e.Handled = true;
    }

    private void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        var (x, y) = WindowInterop.GetCursor();
        WindowInterop.MoveTo(_hwnd, _dragWindowStart.Left + (x - _dragCursorStart.X), _dragWindowStart.Top + (y - _dragCursorStart.Y));
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            Island.ReleaseMouseCapture(); // dispara LostMouseCapture → EndDrag
        }
    }

    private void Island_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        var rect = WindowInterop.GetRect(_hwnd);
        var target = WindowPlacement.EnsureVisible(rect, WindowInterop.GetWorkAreas());
        if (target != rect)
        {
            WindowInterop.MoveTo(_hwnd, target.Left, target.Top);
        }
        if (target != _dragWindowStart)
        {
            _settings.UpdateWidget(w => w with { Position = new WidgetPosition(target.Left, target.Top) });
        }
    }

    // ---------- Comandos ----------

    private void ToggleLayout() =>
        _settings.UpdateWidget(w => w with { Layout = w.Layout == WidgetLayout.Compact ? WidgetLayout.Expanded : WidgetLayout.Compact });

    private void Expand_Click(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { Layout = WidgetLayout.Expanded });

    private void Collapse_Click(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { Layout = WidgetLayout.Compact });

    // Checked/Unchecked (e não Click) para funcionar também por teclado e UI Automation.
    // Quando Apply() sincroniza o estado, a atualização resultante é idêntica e o SettingsService a ignora.
    private void Pin_Changed(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { AlwaysOnTop = PinButton.IsChecked == true });

    private void Lock_Changed(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { Locked = LockButton.IsChecked == true });

    private void Hide_Click(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { Visible = false });

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }
        var panel = sender switch
        {
            _ when ReferenceEquals(sender, TabMedia) => WidgetPanel.Media,
            _ when ReferenceEquals(sender, TabFocus) => WidgetPanel.Focus,
            _ when ReferenceEquals(sender, TabAgenda) => WidgetPanel.Agenda,
            _ when ReferenceEquals(sender, TabInfo) => WidgetPanel.Info,
            _ => WidgetPanel.Activity,
        };
        _settings.UpdateWidget(w => w with { Panel = panel });
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // evento estático: evita vazamento
        _clock.Stop();
        base.OnClosed(e);
    }
}
