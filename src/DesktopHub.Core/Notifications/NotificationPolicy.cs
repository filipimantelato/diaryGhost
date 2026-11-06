using DesktopHub.Core.Settings;

namespace DesktopHub.Core.Notifications;

/// <summary>Contexto do momento da decisão (tudo o que a política precisa, sem dependências).</summary>
public sealed record NotificationContext(
    DateTimeOffset NowUtc,
    TimeSpan LocalTimeOfDay,
    bool ForegroundFullscreen,
    bool DuplicateRecently,
    int ShownRecentlyInCategory);

/// <summary>
/// Regras puras de entrega, na ordem:
/// categoria desligada → duplicata → pausa manual → horário de silêncio → tela cheia → limite de frequência.
/// <list type="bullet">
/// <item>Critical atravessa tudo, exceto duplicata.</item>
/// <item>Pausa manual: só Critical.</item>
/// <item>Horário de silêncio e tela cheia (jogos): só High e Critical.</item>
/// <item>Limite (janela de <see cref="RateWindow"/>): Low/Normal até 5 por categoria; High até 20.</item>
/// </list>
/// </summary>
public static class NotificationPolicy
{
    public static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);

    public static DeliveryOutcome Decide(NotificationRequest request, NotificationSettings settings, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        var critical = request.Priority == NotificationPriority.Critical;

        if (request.Category == NotificationCategory.Timer && !settings.TimerNotifications && !critical)
        {
            return DeliveryOutcome.SuppressedCategoryOff;
        }
        if (context.DuplicateRecently)
        {
            return DeliveryOutcome.Duplicate;
        }
        if (critical)
        {
            return DeliveryOutcome.Shown;
        }
        if (settings.PausedUntil is { } until && context.NowUtc < until)
        {
            return DeliveryOutcome.SuppressedPaused;
        }
        var important = request.Priority == NotificationPriority.High;
        if (!important && settings.QuietHoursEnabled && IsWithinQuietHours(context.LocalTimeOfDay, settings.QuietStartMinutes, settings.QuietEndMinutes))
        {
            return DeliveryOutcome.SuppressedQuietHours;
        }
        if (!important && settings.SuppressInFullscreen && context.ForegroundFullscreen)
        {
            return DeliveryOutcome.SuppressedFullscreen;
        }
        var limit = important ? 20 : 5;
        return context.ShownRecentlyInCategory >= limit ? DeliveryOutcome.RateLimited : DeliveryOutcome.Shown;
    }

    /// <summary>Intervalo [início, fim) em minutos do dia; se início &gt; fim, atravessa a meia-noite.</summary>
    public static bool IsWithinQuietHours(TimeSpan timeOfDay, int startMinutes, int endMinutes)
    {
        var minute = (int)timeOfDay.TotalMinutes;
        if (startMinutes == endMinutes)
        {
            return false;
        }
        return startMinutes < endMinutes
            ? minute >= startMinutes && minute < endMinutes
            : minute >= startMinutes || minute < endMinutes;
    }
}
