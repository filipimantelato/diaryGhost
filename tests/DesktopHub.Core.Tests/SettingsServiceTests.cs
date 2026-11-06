using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Core.Tests;

public sealed class SettingsServiceTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public Dictionary<string, object> Data { get; } = new();
        public int Writes { get; private set; }

        public T? Read<T>(string key) where T : class => Data.TryGetValue(key, out var v) ? v as T : null;

        public void Write<T>(string key, T value) where T : class
        {
            Writes++;
            Data[key] = value;
        }
    }

    private static (SettingsService Service, MemoryStore Store, List<string> Changes) Create()
    {
        var store = new MemoryStore();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var changes = new List<string>();
        bus.Subscribe<SettingsChanged>(e => changes.Add(e.Section));
        var service = new SettingsService(store, bus, NullLogger<SettingsService>.Instance);
        return (service, store, changes);
    }

    [Fact]
    public void Load_without_stored_data_uses_defaults()
    {
        var (service, _, _) = Create();
        service.Load();

        Assert.Equal(CloseBehavior.MinimizeToTray, service.General.CloseBehavior);
        Assert.True(service.Widget.Visible);
        Assert.Equal(1.0, service.Widget.Opacity);
    }

    [Fact]
    public void Load_sanitizes_invalid_values()
    {
        var (service, store, _) = Create();
        store.Data[SettingsSections.Widget] = new WidgetSettings { Opacity = 0.01 };
        store.Data[SettingsSections.General] = new GeneralSettings { AccentColor = "red; drop table" };

        service.Load();

        Assert.Equal(WidgetSettings.MinOpacity, service.Widget.Opacity);
        Assert.Equal(GeneralSettings.DefaultAccent, service.General.AccentColor);
    }

    [Fact]
    public void Update_persists_and_publishes_once()
    {
        var (service, store, changes) = Create();
        service.Load();

        service.UpdateWidget(w => w with { Locked = true });

        Assert.True(service.Widget.Locked);
        Assert.True(((WidgetSettings)store.Data[SettingsSections.Widget]).Locked);
        Assert.Equal([SettingsSections.Widget], changes);
    }

    [Fact]
    public void Update_without_change_does_not_write_or_publish()
    {
        var (service, store, changes) = Create();
        service.Load();

        service.UpdateGeneral(g => g with { });
        service.UpdateWidget(w => w with { Position = null });

        Assert.Equal(0, store.Writes);
        Assert.Empty(changes);
    }

    [Fact]
    public void Phase1_info_panel_is_migrated_to_home()
    {
        var (service, store, _) = Create();
        store.Data[SettingsSections.Widget] = new WidgetSettings { Panel = WidgetPanel.Info };

        service.Load();

        Assert.Equal(WidgetPanel.Home, service.Widget.Panel);
    }

    [Fact]
    public void Position_record_uses_value_equality()
    {
        var (service, store, _) = Create();
        service.Load();
        service.UpdateWidget(w => w with { Position = new WidgetPosition(10, 20) });
        service.UpdateWidget(w => w with { Position = new WidgetPosition(10, 20) });

        Assert.Equal(1, store.Writes);
    }
}
