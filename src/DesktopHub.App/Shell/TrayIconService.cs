using System.Drawing;
using System.Windows.Forms;
using DesktopHub.App.Widget;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;
using WpfApplication = System.Windows.Application;

namespace DesktopHub.App.Shell;

/// <summary>
/// Ícone e menu da bandeja (Modo C). Itens de módulos ainda não implementados aparecem desabilitados,
/// com a fase prevista no texto — nunca como botões que fingem funcionar.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleWidget;
    private readonly ToolStripMenuItem _alwaysOnTop;
    private readonly ToolStripMenuItem _mediaMenu;
    private readonly ToolStripMenuItem _mediaPlayPause;
    private readonly ToolStripMenuItem _mediaNext;
    private readonly ToolStripMenuItem _mediaPrevious;
    private readonly WidgetController _widget;
    private readonly SettingsService _settings;
    private readonly MediaService _media;

    public TrayIconService(AppShell shell, WidgetController widget, SettingsService settings, MediaService media)
    {
        _widget = widget;
        _settings = settings;
        _media = media;

        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = DarkMenuColors.Surface,
            ForeColor = DarkMenuColors.Text,
            Font = new Font("Segoe UI", 9.5f),
        };

        var open = new ToolStripMenuItem("Abrir Desktop Hub", null, (_, _) => shell.OpenMain()) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
        _toggleWidget = new ToolStripMenuItem("Mostrar widget", null, (_, _) => widget.Toggle()) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
        _alwaysOnTop = new ToolStripMenuItem("Widget sempre visível", null, (_, _) => settings.UpdateWidget(w => w with { AlwaysOnTop = !w.AlwaysOnTop }));

        // Comandos de mídia vão sempre para a sessão exibida pelo app (a mesma do widget).
        _mediaPlayPause = new ToolStripMenuItem("Tocar/pausar", null, (_, _) => SendToActive(MediaCommand.TogglePlayPause));
        _mediaNext = new ToolStripMenuItem("Próxima faixa", null, (_, _) => SendToActive(MediaCommand.Next));
        _mediaPrevious = new ToolStripMenuItem("Faixa anterior", null, (_, _) => SendToActive(MediaCommand.Previous));
        _mediaMenu = new ToolStripMenuItem("Mídia");
        _mediaMenu.DropDownItems.AddRange([_mediaPlayPause, _mediaNext, _mediaPrevious]);
        _mediaMenu.DropDown.Renderer = _menu.Renderer;
        _mediaMenu.DropDown.BackColor = DarkMenuColors.Surface;
        _mediaMenu.DropDown.ForeColor = DarkMenuColors.Text;

        _menu.Items.AddRange(
        [
            open,
            _toggleWidget,
            _alwaysOnTop,
            new ToolStripSeparator(),
            _mediaMenu,
            Pending("Iniciar cronômetro — Fase 3"),
            Pending("Pausar notificações — Fase 3"),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Configurações", null, (_, _) => shell.OpenMain(MainPages.Settings)),
            new ToolStripMenuItem("Sair do Desktop Hub", null, (_, _) => shell.Exit()),
        ]);
        _menu.Opening += (_, _) =>
        {
            _toggleWidget.Text = _widget.IsShown ? "Ocultar widget" : "Mostrar widget";
            _alwaysOnTop.Checked = _settings.Widget.AlwaysOnTop;
            UpdateMediaMenu();
        };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Desktop Hub",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                shell.OpenMain();
            }
        };
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(5000, title, text, icon);

    private static ToolStripMenuItem Pending(string text) => new(text) { Enabled = false };

    private void UpdateMediaMenu()
    {
        var active = _media.Active;
        if (active is null)
        {
            _mediaMenu.Text = "Mídia — nada tocando";
            _mediaMenu.Enabled = false;
            return;
        }
        _mediaMenu.Enabled = true;
        _mediaMenu.Text = $"Mídia — {active.AppName}";
        _mediaPlayPause.Text = active.IsPlaying ? $"Pausar {active.AppName}" : $"Tocar {active.AppName}";
        _mediaPlayPause.Enabled = active.Controls.Supports(MediaCommand.TogglePlayPause);
        _mediaNext.Enabled = active.Controls.Next;
        _mediaPrevious.Enabled = active.Controls.Previous;
    }

    private void SendToActive(MediaCommand command)
    {
        if (_media.Active is { } active)
        {
            _ = _media.SendAsync(active.Id, command);
        }
    }

    private static Icon LoadIcon()
    {
        var resource = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = resource!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        // Sem isso, o ícone "fantasma" fica na bandeja até o mouse passar sobre ele.
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public static readonly Color Surface = ColorTranslator.FromHtml("#1C1C1C");
        public static readonly Color Elevated = ColorTranslator.FromHtml("#262626");
        public static readonly Color Border = ColorTranslator.FromHtml("#2E2E2E");
        public static readonly Color Text = ColorTranslator.FromHtml("#EDEDED");

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Elevated;
        public override Color MenuItemSelected => Elevated;
        public override Color MenuItemSelectedGradientBegin => Elevated;
        public override Color MenuItemSelectedGradientEnd => Elevated;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Elevated;
        public override Color CheckSelectedBackground => Elevated;
        public override Color CheckPressedBackground => Elevated;
    }
}
