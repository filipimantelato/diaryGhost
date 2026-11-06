using DesktopHub.Core.Windowing;

namespace DesktopHub.Core.Tests;

public sealed class WindowPlacementTests
{
    // Monitor primário 1920x1080 (barra de tarefas de 48 px) e secundário à direita 2560x1440.
    private static readonly PixelRect Primary = new(0, 0, 1920, 1032);
    private static readonly PixelRect Secondary = new(1920, 0, 2560, 1392);

    [Fact]
    public void Window_fully_inside_a_monitor_is_unchanged()
    {
        var w = new PixelRect(100, 100, 400, 200);
        Assert.Equal(w, WindowPlacement.EnsureVisible(w, [Primary, Secondary]));
    }

    [Fact]
    public void Window_on_removed_monitor_moves_to_nearest_remaining_one()
    {
        // Estava no secundário, que foi desconectado.
        var w = new PixelRect(3000, 500, 400, 200);
        var result = WindowPlacement.EnsureVisible(w, [Primary]);

        Assert.Equal(1920 - 400, result.Left);
        Assert.Equal(500, result.Top);
    }

    [Fact]
    public void Window_straddling_monitors_snaps_into_the_one_with_larger_overlap()
    {
        var w = new PixelRect(1800, 100, 400, 200); // 120 px no primário, 280 px no secundário
        var result = WindowPlacement.EnsureVisible(w, [Primary, Secondary]);

        Assert.Equal(1920, result.Left);
    }

    [Fact]
    public void Window_above_screen_is_pulled_down()
    {
        var w = new PixelRect(100, -150, 400, 200);
        Assert.Equal(0, WindowPlacement.EnsureVisible(w, [Primary]).Top);
    }

    [Fact]
    public void Monitor_with_negative_coordinates_is_supported()
    {
        var left = new PixelRect(-1280, 0, 1280, 984);
        var w = new PixelRect(-2000, 100, 400, 200);

        Assert.Equal(-1280, WindowPlacement.EnsureVisible(w, [Primary, left]).Left);
    }

    [Fact]
    public void No_work_areas_returns_input()
    {
        var w = new PixelRect(5, 5, 10, 10);
        Assert.Equal(w, WindowPlacement.EnsureVisible(w, []));
    }

    [Fact]
    public void TopCenter_centers_horizontally_with_margin()
    {
        var r = WindowPlacement.TopCenter(Primary, 400, 200, 8);
        Assert.Equal((1920 - 400) / 2, r.Left);
        Assert.Equal(8, r.Top);
    }
}
