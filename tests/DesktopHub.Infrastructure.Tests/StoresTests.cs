using DesktopHub.Core.Settings;
using DesktopHub.Infrastructure.Security;
using DesktopHub.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Infrastructure.Tests;

public sealed class StoresTests
{
    [Fact]
    public void Settings_survive_restart()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteSettingsStore(dir.Database, TimeProvider.System, NullLogger<SqliteSettingsStore>.Instance);
        var widget = new WidgetSettings { Opacity = 0.8, Locked = true, Position = new WidgetPosition(-1200, 40), Fullscreen = FullscreenBehavior.KeepVisible };

        store.Write(SettingsSections.Widget, widget);

        var reopened = new SqliteSettingsStore(dir.Reopen(), TimeProvider.System, NullLogger<SqliteSettingsStore>.Instance);
        Assert.Equal(widget, reopened.Read<WidgetSettings>(SettingsSections.Widget));
    }

    [Fact]
    public void Missing_or_invalid_settings_return_null()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteSettingsStore(dir.Database, TimeProvider.System, NullLogger<SqliteSettingsStore>.Instance);

        Assert.Null(store.Read<GeneralSettings>(SettingsSections.General));

        using (var c = dir.Database.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO settings VALUES ('general', '{not json', 0);";
            cmd.ExecuteNonQuery();
        }
        Assert.Null(store.Read<GeneralSettings>(SettingsSections.General));
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_new_ones_get_defaults()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        using (var c = dir.Database.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """INSERT INTO settings VALUES ('widget', '{"locked":true,"propriedadeRemovida":42}', 0);""";
            cmd.ExecuteNonQuery();
        }
        var store = new SqliteSettingsStore(dir.Database, TimeProvider.System, NullLogger<SqliteSettingsStore>.Instance);

        var widget = store.Read<WidgetSettings>(SettingsSections.Widget);

        Assert.NotNull(widget);
        Assert.True(widget.Locked);
        Assert.True(widget.AlwaysOnTop); // padrão do código
    }

    [Fact]
    public void Secrets_roundtrip_and_are_not_stored_in_plain_text()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var secrets = new DpapiSecretStore(dir.Database, TimeProvider.System);
        const string token = "token-de-teste-123456";

        secrets.Save("spotify.refresh", token);

        Assert.Equal(token, secrets.Read("spotify.refresh"));
        using (var c = dir.Database.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT protected_blob FROM secrets;";
            var blob = (byte[])cmd.ExecuteScalar()!;
            Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(blob), StringComparison.Ordinal);
        }

        Assert.True(secrets.Delete("spotify.refresh"));
        Assert.Null(secrets.Read("spotify.refresh"));
        Assert.False(secrets.Delete("spotify.refresh"));
    }
}
