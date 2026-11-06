using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopHub.App.Platform;
using DesktopHub.App.Theme;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;
using DesktopHub.Core.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopHub.App.Widget;

/// <summary>
/// A "ilha" flutuante. Não rouba foco (<c>WS_EX_NOACTIVATE</c>), não aparece no Alt+Tab
/// (<c>WS_EX_TOOLWINDOW</c>) e só renderiza quando algo muda. Este arquivo cuida da janela
/// (posição, arrastar, layout, abas); <c>WidgetWindow.Media.cs</c> cuida dos painéis de mídia e volume.
/// </summary>
public partial class WidgetWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Duration ResizeDuration = TimeSpan.FromMilliseconds(180);
    private const int TopMargin = 8;
    private const double ExpandedWidth = 420;
    private const double MaxCompactWidth = 340;
    private const double DragThreshold = 4;

    private readonly SettingsService _settings;
    private readonly IEventBus _bus;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _clock;
    private nint _hwnd;
    private WidgetLayout? _appliedLayout;
    private WidgetPanel? _appliedPanel;
    private bool? _appliedCaptureExclusion;
    private bool _syncingTabs;
    private bool _resizePending;

    private bool _pressed;
    private bool _dragging;
    private object? _pressSource;
    private (int X, int Y) _dragCursorStart;
    private PixelRect _dragWindowStart;

    internal WidgetWindow(SettingsService settings, IEventBus bus, MediaService media, IMasterVolume volume, ProductivityService productivity, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        _bus = bus;
        _media = media;
        _volume = volume;
        _productivity = productivity;
        InitializeComponent();
        _clock = new DispatcherTimer(DispatcherPriority.Background);
        _clock.Tick += (_, _) => UpdateClock();
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        IsVisibleChanged += OnIsVisibleChanged;
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        InitializeMedia();
        InitializeFocus();
    }

    /// <summary>Falso quando o Windows recusou a exclusão de capturas (ex.: versão anterior ao 2004).</summary>
    public bool CaptureExclusionSupported { get; private set; } = true;

    private bool AnimationsAllowed => ThemeManager.AnimationsAllowed(_settings.Widget.AnimationsEnabled);

    internal void Apply(WidgetSettings s)
    {
        Topmost = s.AlwaysOnTop;
        Opacity = s.Opacity;
        Island.Cursor = s.Locked ? null : Cursors.SizeAll;

        var layoutChanged = _appliedLayout != s.Layout;
        var animate = _appliedLayout is not null && AnimationsAllowed;
        if (layoutChanged && _appliedLayout is not null)
        {
            LogLayoutApplied(_logger, s.Layout);
        }
        if (_appliedPanel != s.Panel || layoutChanged)
        {
            _appliedPanel = s.Panel;
            _appliedLayout = s.Layout;
            ShowPanel(s.Panel, s.Layout);
            ResizeIsland(animate);
        }
        if (_hwnd != 0 && _appliedCaptureExclusion != s.ExcludeFromCapture)
        {
            _appliedCaptureExclusion = s.ExcludeFromCapture;
            CaptureExclusionSupported = WindowInterop.SetExcludeFromCapture(_hwnd, s.ExcludeFromCapture) || !s.ExcludeFromCapture;
        }
        RenderFocusPanel(); // a ferramenta do painel Foco pode ter mudado
        RenderFocusTick();
        UpdateTimers();
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

    // ---------- Painéis e tamanho da ilha ----------

    private static readonly Dictionary<WidgetPanel, (string Title, string? Placeholder, string? Badge)> PanelInfo = new()
    {
        [WidgetPanel.Home] = ("Início", null, null),
        [WidgetPanel.Media] = ("Mídia", null, null),
        [WidgetPanel.Volume] = ("Volume", null, null),
        [WidgetPanel.Focus] = ("Foco", null, null),
        [WidgetPanel.Agenda] = ("Agenda", "Próximo compromisso, horário, tempo restante e lembretes.", "Chega na Fase 4"),
        [WidgetPanel.Activity] = ("Atividade", "Eventos autorizados do Claude Code (via hooks oficiais) e de outras ferramentas.", "Chega na Fase 6"),
    };

    private void ShowPanel(WidgetPanel panel, WidgetLayout layout)
    {
        var expanded = layout == WidgetLayout.Expanded;
        CompactView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        var (title, placeholder, badge) = PanelInfo.GetValueOrDefault(panel, PanelInfo[WidgetPanel.Home]);
        PanelTitle.Text = title;
        HomeBody.Visibility = panel == WidgetPanel.Home ? Visibility.Visible : Visibility.Collapsed;
        MediaBody.Visibility = panel == WidgetPanel.Media ? Visibility.Visible : Visibility.Collapsed;
        VolumeBody.Visibility = panel == WidgetPanel.Volume ? Visibility.Visible : Visibility.Collapsed;
        FocusBody.Visibility = panel == WidgetPanel.Focus ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderBody.Visibility = placeholder is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderText.Text = placeholder ?? "";
        PlaceholderBadge.Text = badge ?? "";

        _syncingTabs = true;
        TabFor(panel).IsChecked = true;
        _syncingTabs = false;

        if (expanded && AnimationsAllowed)
        {
            ExpandedView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(40) });
        }
        UpdateClock();
        RenderCompact();
    }

    /// <summary>Pede um redimensionamento da ilha para caber no conteúdo atual (agrupa vários pedidos).</summary>
    private void RequestResize()
    {
        if (_resizePending)
        {
            return;
        }
        _resizePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _resizePending = false;
            ResizeIsland(AnimationsAllowed);
        }, DispatcherPriority.Loaded);
    }

    private void ResizeIsland(bool animate)
    {
        var expanded = _appliedLayout == WidgetLayout.Expanded;
        FrameworkElement view = expanded ? ExpandedView : CompactView;
        view.Measure(new Size(expanded ? ExpandedWidth : MaxCompactWidth, double.PositiveInfinity));
        var border = Island.BorderThickness.Left + Island.BorderThickness.Right;
        var width = expanded
            ? ExpandedWidth + border
            : Math.Clamp(view.DesiredSize.Width + view.Margin.Left + view.Margin.Right + border, 140, MaxCompactWidth);
        var height = Math.Ceiling(view.DesiredSize.Height + view.Margin.Top + view.Margin.Bottom + border);
        Island.CornerRadius = new CornerRadius(expanded ? 22 : height / 2);

        if (Math.Abs(Island.ActualWidth - width) < 0.5 && Math.Abs(Island.ActualHeight - height) < 0.5)
        {
            return;
        }
        if (animate && Island.IsLoaded)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(Island.ActualWidth, width, ResizeDuration) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(Island.ActualHeight, height, ResizeDuration) { EasingFunction = ease });
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

    private RadioButton TabFor(WidgetPanel panel) => panel switch
    {
        WidgetPanel.Media => TabMedia,
        WidgetPanel.Focus => TabFocus,
        WidgetPanel.Agenda => TabAgenda,
        WidgetPanel.Activity => TabActivity,
        WidgetPanel.Volume => TabVolume,
        _ => TabHome,
    };

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
            _ when ReferenceEquals(sender, TabActivity) => WidgetPanel.Activity,
            _ when ReferenceEquals(sender, TabVolume) => WidgetPanel.Volume,
            _ => WidgetPanel.Home,
        };
        _settings.UpdateWidget(w => w with { Panel = panel });
    }

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
        UpdateTimers();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        HeaderClock.Text = $"{now.ToString("ddd, d MMM", PtBr)} · {now.ToString("HH:mm", PtBr)}";
        if (_shownMedia is null)
        {
            RenderCompact();
        }
        if (IsVisible)
        {
            // Próxima virada de minuto (+50 ms de folga), em vez de um tick por segundo.
            _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
            _clock.Start();
        }
    }

    // ---------- Mouse: clique expande; arrastar move (pixels físicos, sem ativar a janela) ----------

    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_hwnd == 0)
        {
            return;
        }
        _pressSource = e.OriginalSource;
        _dragCursorStart = WindowInterop.GetCursor();
        _dragWindowStart = WindowInterop.GetRect(_hwnd);
        _pressed = Island.CaptureMouse();
        e.Handled = true;
    }

    private void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        var (x, y) = WindowInterop.GetCursor();
        var dx = x - _dragCursorStart.X;
        var dy = y - _dragCursorStart.Y;
        if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold) && !_settings.Widget.Locked)
        {
            _dragging = true;
        }
        if (_dragging)
        {
            WindowInterop.MoveTo(_hwnd, _dragWindowStart.Left + dx, _dragWindowStart.Top + dy);
        }
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        var wasDrag = _dragging;
        Island.ReleaseMouseCapture(); // dispara LostMouseCapture → EndPress
        if (!wasDrag)
        {
            OnIslandClick(_pressSource as DependencyObject);
        }
    }

    private void Island_LostMouseCapture(object sender, MouseEventArgs e) => EndPress();

    private void EndPress()
    {
        if (!_pressed)
        {
            return;
        }
        _pressed = false;
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

    private void OnIslandClick(DependencyObject? source)
    {
        if (_appliedLayout == WidgetLayout.Compact)
        {
            LogLayoutRequest(_logger, WidgetLayout.Expanded, "clique na ilha");
            _settings.UpdateWidget(w => w with { Layout = WidgetLayout.Expanded });
            return;
        }
        // Cartões do Início levam ao painel correspondente.
        if (IsWithin(source, HomeMediaCard))
        {
            _settings.UpdateWidget(w => w with { Panel = WidgetPanel.Media });
        }
        else if (IsWithin(source, HomeFocusCard))
        {
            _settings.UpdateWidget(w => w with { Panel = WidgetPanel.Focus });
        }
        else if (IsWithin(source, HomeActivityCard))
        {
            _settings.UpdateWidget(w => w with { Panel = WidgetPanel.Activity });
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- Comandos e menu ----------

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        LogLayoutRequest(_logger, WidgetLayout.Compact, "botão Recolher");
        _settings.UpdateWidget(w => w with { Layout = WidgetLayout.Compact });
    }

    /// <summary>Acionado por UI Automation/leitor de tela: alterna compacto ⇄ expandido.</summary>
    private void Island_Invoked(object? sender, EventArgs e)
    {
        var target = _settings.Widget.Layout == WidgetLayout.Compact ? WidgetLayout.Expanded : WidgetLayout.Compact;
        LogLayoutRequest(_logger, target, "UI Automation (Invoke)");
        _settings.UpdateWidget(w => w with { Layout = target });
    }

    private void IslandMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag switch
            {
                "pin" => _settings.Widget.AlwaysOnTop,
                "lock" => _settings.Widget.Locked,
                _ => item.IsChecked,
            };
        }
    }

    private void MenuAlwaysOnTop_Click(object sender, RoutedEventArgs e) =>
        _settings.UpdateWidget(w => w with { AlwaysOnTop = ((MenuItem)sender).IsChecked });

    private void MenuLocked_Click(object sender, RoutedEventArgs e) =>
        _settings.UpdateWidget(w => w with { Locked = ((MenuItem)sender).IsChecked });

    private void MenuHide_Click(object sender, RoutedEventArgs e) => _settings.UpdateWidget(w => w with { Visible = false });

    private void MenuOpenMain_Click(object sender, RoutedEventArgs e) => _bus.Publish(new OpenMainRequested(null));

    [LoggerMessage(Level = LogLevel.Information, Message = "Widget: pedido para {Layout} (origem: {Origin})")]
    private static partial void LogLayoutRequest(ILogger logger, WidgetLayout layout, string origin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Widget: layout aplicado {Layout}")]
    private static partial void LogLayoutApplied(ILogger logger, WidgetLayout layout);

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // evento estático: evita vazamento
        _clock.Stop();
        _progressTimer.Stop();
        _focusTimer?.Stop();
        StopEqualizer();
        base.OnClosed(e);
    }
}
