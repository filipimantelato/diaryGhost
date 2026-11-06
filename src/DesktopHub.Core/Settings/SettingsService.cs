using DesktopHub.Core.Events;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Settings;

/// <summary>Porta de persistência das configurações (implementada na Infrastructure).</summary>
public interface ISettingsStore
{
    /// <summary>Retorna null se a chave não existir ou o conteúdo for inválido.</summary>
    T? Read<T>(string key) where T : class;

    void Write<T>(string key, T value) where T : class;
}

/// <summary>
/// Fonte única das configurações em memória. Toda alteração é validada, persistida e anunciada
/// com <see cref="SettingsChanged"/>. Thread-safe.
/// </summary>
public sealed partial class SettingsService(ISettingsStore store, IEventBus bus, ILogger<SettingsService> logger)
{
    private readonly Lock _gate = new();

    public GeneralSettings General { get; private set; } = new();
    public WidgetSettings Widget { get; private set; } = new();
    public MediaSettings Media { get; private set; } = new();
    public NotificationSettings Notifications { get; private set; } = new();
    public ProductivitySettings Productivity { get; private set; } = new();

    public void Load()
    {
        lock (_gate)
        {
            General = (store.Read<GeneralSettings>(SettingsSections.General) ?? new GeneralSettings()).Sanitize();
            Widget = (store.Read<WidgetSettings>(SettingsSections.Widget) ?? new WidgetSettings()).Sanitize();
            Media = (store.Read<MediaSettings>(SettingsSections.Media) ?? new MediaSettings()).Sanitize();
            Notifications = (store.Read<NotificationSettings>(SettingsSections.Notifications) ?? new NotificationSettings()).Sanitize();
            Productivity = (store.Read<ProductivitySettings>(SettingsSections.Productivity) ?? new ProductivitySettings()).Sanitize();
        }
        LogLoaded();
    }

    public void UpdateMedia(Func<MediaSettings, MediaSettings> change) =>
        Update(SettingsSections.Media, () => Media, v => Media = v, change, s => s.Sanitize());

    public void UpdateNotifications(Func<NotificationSettings, NotificationSettings> change) =>
        Update(SettingsSections.Notifications, () => Notifications, v => Notifications = v, change, s => s.Sanitize());

    public void UpdateProductivity(Func<ProductivitySettings, ProductivitySettings> change) =>
        Update(SettingsSections.Productivity, () => Productivity, v => Productivity = v, change, s => s.Sanitize());

    private void Update<T>(string section, Func<T> get, Action<T> set, Func<T, T> change, Func<T, T> sanitize) where T : class
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = sanitize(change(get()));
            if (updated.Equals(get()))
            {
                return;
            }
            store.Write(section, updated);
            set(updated);
        }
        bus.Publish(new SettingsChanged(section));
    }

    public void UpdateGeneral(Func<GeneralSettings, GeneralSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(General).Sanitize();
            if (updated == General)
            {
                return;
            }
            store.Write(SettingsSections.General, updated);
            General = updated;
        }
        bus.Publish(new SettingsChanged(SettingsSections.General));
    }

    public void UpdateWidget(Func<WidgetSettings, WidgetSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(Widget).Sanitize();
            if (updated == Widget)
            {
                return;
            }
            store.Write(SettingsSections.Widget, updated);
            Widget = updated;
        }
        bus.Publish(new SettingsChanged(SettingsSections.Widget));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Configurações carregadas")]
    private partial void LogLoaded();
}
