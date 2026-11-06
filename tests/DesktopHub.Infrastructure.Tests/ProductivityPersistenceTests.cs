using DesktopHub.Core.Notifications;
using DesktopHub.Core.Productivity;
using DesktopHub.Infrastructure.Notifications;
using DesktopHub.Infrastructure.Productivity;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Infrastructure.Tests;

public sealed class ProductivityPersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Active_state_roundtrips_through_sqlite()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteProductivityStore(dir.Database, NullLogger<SqliteProductivityStore>.Instance);
        var snapshot = new ProductivitySnapshot(
            StopwatchState.Idle.Start(T0).Lap(T0.AddSeconds(42)),
            CountdownState.Ready(TimeSpan.FromMinutes(10)).Start(T0),
            T0,
            new PomodoroState(PomodoroPhase.ShortBreak, 3, CountdownState.Ready(TimeSpan.FromMinutes(5))),
            null);

        store.SaveActive(snapshot);
        var loaded = new SqliteProductivityStore(dir.Reopen(), NullLogger<SqliteProductivityStore>.Instance).LoadActive();

        Assert.NotNull(loaded);
        Assert.Equal(T0, loaded.Stopwatch.RunningSince);
        Assert.Equal([TimeSpan.FromSeconds(42)], loaded.Stopwatch.Laps);
        Assert.Equal(T0.AddMinutes(10), loaded.Timer!.Deadline);
        Assert.Equal(snapshot.Pomodoro.Phase, loaded.Pomodoro.Phase);
        Assert.Equal(3, loaded.Pomodoro.CompletedFocusInSet);
    }

    [Fact]
    public void Sessions_are_queried_by_time()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var store = new SqliteProductivityStore(dir.Database, NullLogger<SqliteProductivityStore>.Instance);
        store.AddSession(new FocusSession(Guid.CreateVersion7(), SessionKind.PomodoroFocus, null, T0.AddDays(-10), T0.AddDays(-10).AddMinutes(25), TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(25), true));
        store.AddSession(new FocusSession(Guid.CreateVersion7(), SessionKind.Timer, "chá", T0, T0.AddMinutes(4), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4), true));

        var recent = store.SessionsSince(T0.AddDays(-1));

        var s = Assert.Single(recent);
        Assert.Equal(("chá", SessionKind.Timer, TimeSpan.FromMinutes(4)), (s.Label, s.Kind, s.Actual));
        Assert.Equal(2, store.RecentSessions(10).Count);
    }

    [Fact]
    public void Notification_history_supports_dedup_rate_and_pruning()
    {
        using var dir = new TempDataDir();
        dir.Database.Initialize();
        var history = new SqliteNotificationHistory(dir.Database);
        history.Add(new NotificationRecord(Guid.CreateVersion7(), NotificationCategory.Timer, NotificationPriority.High, "a", null, "k1", T0, DeliveryOutcome.Shown));
        history.Add(new NotificationRecord(Guid.CreateVersion7(), NotificationCategory.Timer, NotificationPriority.High, "b", null, null, T0, DeliveryOutcome.SuppressedPaused));
        history.Add(new NotificationRecord(Guid.CreateVersion7(), NotificationCategory.News, NotificationPriority.Low, "velha", null, null, T0.AddDays(-40), DeliveryOutcome.Shown));

        Assert.True(history.ExistsSince("k1", T0.AddMinutes(-1)));
        Assert.False(history.ExistsSince("k1", T0.AddMinutes(1)));
        Assert.Equal(1, history.CountShownSince(NotificationCategory.Timer, T0.AddMinutes(-1))); // suprimida não conta
        Assert.Equal(1, history.PruneBefore(T0.AddDays(-30)));
        Assert.Equal(2, history.Recent(10).Count);
    }

    [Fact]
    public void Toast_xml_escapes_untrusted_text()
    {
        var request = new NotificationRequest(NotificationCategory.News, NotificationPriority.Normal,
            "<script>&\"", "fim</text><action content=\"x\"/>", "news", Actions: [new("ok", "Ok & <já>")]);

        var xml = WindowsToastSink.BuildXml(Guid.Empty, request);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml); // XML válido

        Assert.Equal(2, doc.GetElementsByTagName("text").Count);
        Assert.Equal("<script>&\"", doc.GetElementsByTagName("text")[0]!.InnerText);
        Assert.Single(doc.GetElementsByTagName("action").Cast<System.Xml.XmlNode>()); // nenhuma ação injetada
    }
}
