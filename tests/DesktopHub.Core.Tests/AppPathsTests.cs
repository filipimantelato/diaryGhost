namespace DesktopHub.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "DiaryGhostPathTests", Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_localAppData);

    public void Dispose() => Directory.Delete(_localAppData, recursive: true);

    [Fact]
    public void Legacy_folder_is_moved_with_its_contents()
    {
        var legacy = Path.Combine(_localAppData, AppPaths.LegacyFolderName);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "hub.db"), "dados");

        var outcome = AppPaths.MigrateLegacyFolder(_localAppData, out var paths);

        Assert.Equal(MigrationOutcome.Moved, outcome);
        Assert.Equal("dados", File.ReadAllText(paths.DatabaseFile));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Existing_new_folder_is_never_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(_localAppData, AppPaths.LegacyFolderName));
        var current = Directory.CreateDirectory(Path.Combine(_localAppData, AppPaths.FolderName)).FullName;

        var outcome = AppPaths.MigrateLegacyFolder(_localAppData, out var paths);

        Assert.Equal(MigrationOutcome.NotNeeded, outcome);
        Assert.Equal(current, paths.Root);
        Assert.True(Directory.Exists(Path.Combine(_localAppData, AppPaths.LegacyFolderName)));
    }

    [Fact]
    public void Legacy_folder_in_use_is_kept_and_used()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(_localAppData, AppPaths.LegacyFolderName)).FullName;
        using var locked = new FileStream(Path.Combine(legacy, "hub.db"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var outcome = AppPaths.MigrateLegacyFolder(_localAppData, out var paths);

        Assert.Equal(MigrationOutcome.KeptLegacyInUse, outcome);
        Assert.Equal(legacy, paths.Root);
    }
}
