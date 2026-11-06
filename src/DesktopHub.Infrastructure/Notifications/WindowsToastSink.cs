using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using DesktopHub.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DesktopHub.Infrastructure.Notifications;

/// <summary>
/// Notificações nativas do Windows (toasts) para um app sem pacote MSIX. Requisitos documentados para
/// esse caso: um AppUserModelID próprio, registrado por usuário em <c>HKCU\Software\Classes\AppUserModelId</c>
/// (nome e ícone exibidos no toast) e associado ao processo com <c>SetCurrentProcessExplicitAppUserModelID</c>.
/// <para>
/// Limitação: sem um ativador COM, clicar num toast que ficou na Central de Notificações depois que o app
/// fechou não reabre o app. Com o app aberto, cliques e botões chegam pelo evento <c>Activated</c>.
/// O Windows aplica sozinho o "Não incomodar" e as preferências de notificação do usuário.
/// </para>
/// </summary>
public sealed partial class WindowsToastSink(ILogger<WindowsToastSink> logger) : INotificationSink
{
    public const string AppUserModelId = "DiaryGhost.App";
    private const int MaxTracked = 50;
    private readonly ConcurrentDictionary<Guid, ToastNotification> _live = new();
    private ToastNotifier? _notifier;

    public event Action<Guid, string?>? Activated;

    /// <summary>Registra a identidade do app para notificações. Idempotente; chamar na inicialização.</summary>
    public void Register(string displayName, string iconPath)
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}", writable: true))
            {
                key.SetValue("DisplayName", displayName, RegistryValueKind.String);
                key.SetValue("IconUri", iconPath, RegistryValueKind.String);
            }
            _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            _notifier = null;
            LogRegisterFailed(ex);
        }
    }

    public NotificationChannelStatus Status
    {
        get
        {
            if (_notifier is null)
            {
                return NotificationChannelStatus.Unavailable;
            }
            try
            {
                return _notifier.Setting switch
                {
                    NotificationSetting.Enabled => NotificationChannelStatus.Enabled,
                    NotificationSetting.DisabledForApplication or NotificationSetting.DisabledForUser => NotificationChannelStatus.DisabledByUser,
                    _ => NotificationChannelStatus.Unavailable,
                };
            }
            catch (COMException)
            {
                return NotificationChannelStatus.Unknown;
            }
        }
    }

    public DeliveryOutcome Show(Guid id, NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_notifier is null)
        {
            return DeliveryOutcome.Failed;
        }
        if (Status == NotificationChannelStatus.DisabledByUser)
        {
            return DeliveryOutcome.BlockedByWindows;
        }
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildXml(id, request));
            // Group tem limite de 64 caracteres no Windows; o tag completo fica no NotificationCenter.
            var toast = new ToastNotification(xml) { Tag = id.ToString("N")[..16], Group = request.Tag.Length <= 64 ? request.Tag : request.Tag[..64] };
            var selectionId = request.Selection?.Id;
            toast.Activated += (_, args) => OnActivated(id, args, selectionId);
            toast.Dismissed += (_, _) => _live.TryRemove(id, out _);
            toast.Failed += (_, e) =>
            {
                _live.TryRemove(id, out _);
                LogToastFailed(e.ErrorCode);
            };
            Track(id, toast);
            _notifier.Show(toast);
            return DeliveryOutcome.Shown;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            LogShowFailed(ex);
            return DeliveryOutcome.Failed;
        }
    }

    /// <summary>
    /// XML do toast montado com texto escapado: títulos e textos vêm de dados (inclusive, no futuro, de fontes
    /// externas como notícias) e nunca podem injetar marcação ou ações.
    /// </summary>
    internal static string BuildXml(Guid id, NotificationRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("<toast launch=\"id=").Append(id.ToString("N")).Append("\">");
        sb.Append("<visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>").Append(SecurityElement.Escape(request.Title)).Append("</text>");
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            sb.Append("<text>").Append(SecurityElement.Escape(request.Body)).Append("</text>");
        }
        sb.Append("</binding></visual>");
        if (request.Actions is { Count: > 0 } actions)
        {
            sb.Append("<actions>");
            if (request.Selection is { Options.Count: > 0 } selection)
            {
                // Entradas vêm antes dos botões; o valor escolhido chega em UserInput[Id].
                sb.Append("<input id=\"").Append(SecurityElement.Escape(selection.Id)).Append("\" type=\"selection\" defaultInput=\"")
                  .Append(SecurityElement.Escape(selection.DefaultOptionId)).Append("\">");
                foreach (var option in selection.Options.Take(5))
                {
                    sb.Append("<selection id=\"").Append(SecurityElement.Escape(option.Id)).Append("\" content=\"")
                      .Append(SecurityElement.Escape(option.Label)).Append("\"/>");
                }
                sb.Append("</input>");
            }
            foreach (var action in actions.Take(5)) // limite do Windows
            {
                sb.Append("<action content=\"").Append(SecurityElement.Escape(action.Label))
                  .Append("\" arguments=\"action=").Append(SecurityElement.Escape(action.Id)).Append("\" activationType=\"foreground\"/>");
            }
            sb.Append("</actions>");
        }
        sb.Append("</toast>");
        return sb.ToString();
    }

    private void OnActivated(Guid id, object args, string? selectionId)
    {
        var activated = args as ToastActivatedEventArgs;
        var arguments = activated?.Arguments ?? "";
        var actionId = arguments.StartsWith("action=", StringComparison.Ordinal) ? arguments["action=".Length..] : null;
        if (actionId is not null && selectionId is not null &&
            activated?.UserInput is { } input && input.TryGetValue(selectionId, out var chosen) && chosen is string value)
        {
            actionId = $"{actionId}:{value}";
        }
        Activated?.Invoke(id, actionId);
    }

    private void Track(Guid id, ToastNotification toast)
    {
        // Referência viva para que os eventos do toast continuem chegando; limitado para não crescer sem fim.
        _live[id] = toast;
        if (_live.Count > MaxTracked)
        {
            foreach (var old in _live.Keys.Order().Take(_live.Count - MaxTracked))
            {
                _live.TryRemove(old, out _);
            }
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notificações do Windows indisponíveis")]
    private partial void LogRegisterFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao exibir notificação")]
    private partial void LogShowFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "O Windows não exibiu a notificação")]
    private partial void LogToastFailed(Exception error);
}
