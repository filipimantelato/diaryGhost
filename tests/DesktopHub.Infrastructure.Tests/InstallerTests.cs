using DesktopHub.Core.Notifications;
using DesktopHub.Infrastructure.Notifications;
using DesktopHub.Infrastructure.Setup;
using Microsoft.Win32;

namespace DesktopHub.Infrastructure.Tests;

/// <summary>Instala numa pasta temporária, com registro numa subchave descartável de HKCU (removida no fim).</summary>
public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DiaryGhostInstallTests", Guid.NewGuid().ToString("N"));
    private readonly string _registryBase = $@"Software\DiaryGhostTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryBase, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"SoftwareDiaryGhostTests"))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKey(@"SoftwareDiaryGhostTests", throwOnMissingSubKey: false);
            }
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private InstallLayout Layout => new(Path.Combine(_root, "Programs", "DiaryGhost"), Path.Combine(_root, "Menu", "Diary Ghost.lnk"), _registryBase);

    private string Source(params string[] files)
    {
        var dir = Path.Combine(_root, "publish-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "pt-BR"));
        File.WriteAllText(Path.Combine(dir, InstallLayout.ExeName), "exe");
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), f);
        }
        return dir;
    }

    [Fact]
    public void Install_copies_registers_and_creates_the_shortcut()
    {
        var copied = PerUserInstaller.Install(Source("a.dll", @"pt-BR\b.resources.dll"), Layout, "0.7.0");

        Assert.Equal(3, copied);
        Assert.True(File.Exists(Path.Combine(Layout.TargetDir, "pt-BR", "b.resources.dll")));
        Assert.True(File.Exists(Layout.ShortcutPath));
        Assert.Equal("0.7.0", PerUserInstaller.InstalledVersion(Layout));
        using (var key = Registry.CurrentUser.OpenSubKey(Layout.UninstallKey)!)
        {
            Assert.Equal($"\"{Layout.TargetExe}\" --uninstall", key.GetValue("UninstallString"));
            Assert.Equal("Diary Ghost", key.GetValue("DisplayName"));
        }
        using var command = Registry.CurrentUser.OpenSubKey(Layout.ProtocolKey + @"\shell\open\command")!;
        Assert.Equal($"\"{Layout.TargetExe}\" \"%1\"", command.GetValue(""));
    }

    [Fact]
    public void Reinstall_updates_in_place_and_removes_stale_files()
    {
        PerUserInstaller.Install(Source("velho.dll", "comum.dll"), Layout, "0.6.0", createShortcut: false);
        PerUserInstaller.Install(Source("comum.dll", "novo.dll"), Layout, "0.7.0", createShortcut: false);

        Assert.False(File.Exists(Path.Combine(Layout.TargetDir, "velho.dll")));
        Assert.True(File.Exists(Path.Combine(Layout.TargetDir, "novo.dll")));
        Assert.Equal("0.7.0", PerUserInstaller.InstalledVersion(Layout));
    }

    [Fact]
    public void Unregister_removes_entries_and_shortcut()
    {
        PerUserInstaller.Install(Source(), Layout, "0.7.0");
        PerUserInstaller.Unregister(Layout);

        Assert.Null(PerUserInstaller.InstalledVersion(Layout));
        Assert.Null(Registry.CurrentUser.OpenSubKey(Layout.ProtocolKey));
        Assert.False(File.Exists(Layout.ShortcutPath));
    }

    [Fact]
    public void Folder_removal_refuses_unexpected_paths()
    {
        Assert.Throws<InvalidOperationException>(() => PerUserInstaller.ScheduleFolderRemoval(_root));
        Assert.Throws<InvalidOperationException>(() => PerUserInstaller.ScheduleFolderRemoval(@"C:\Windows"));
    }

    [Fact]
    public void Installing_from_the_target_folder_is_refused()
    {
        PerUserInstaller.Install(Source(), Layout, "0.7.0", createShortcut: false);
        Assert.Throws<InvalidOperationException>(() => PerUserInstaller.Install(Layout.TargetDir, Layout, "0.7.0", createShortcut: false));
    }

    [Theory]
    [InlineData("timer", "productivity")]
    [InlineData("reminder:Task:abc:1:0", "tasks")]
    [InlineData("reminder:Event:abc:1:10", "calendar")]
    [InlineData("news", "news")]
    [InlineData("desconhecido", null)]
    public void Toast_body_opens_the_right_page_via_link(string tag, string? page)
    {
        Assert.Equal(page, NotificationPages.ForTag(tag));
        var xml = WindowsToastSink.BuildXml(Guid.Empty, new NotificationRequest(NotificationCategory.Timer, NotificationPriority.High, "t", null, tag));
        if (page is null)
        {
            Assert.DoesNotContain("protocol", xml, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"activationType=\"protocol\" launch=\"diaryghost:{page}\"", xml, StringComparison.Ordinal);
        }
    }
}
