using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Productivity;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

public sealed class TimeStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Countdown_pauses_and_resumes_without_drift()
    {
        var c = CountdownState.Ready(TimeSpan.FromMinutes(25)).Start(T0);
        c = c.Pause(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(15), c.RemainingAt(T0.AddHours(3))); // pausado: tempo não passa

        c = c.Start(T0.AddHours(3));
        Assert.Equal(T0.AddHours(3).AddMinutes(15), c.Deadline);
        Assert.True(c.IsFinishedAt(T0.AddHours(3).AddMinutes(15)));
        Assert.Equal(TimeSpan.Zero, c.RemainingAt(T0.AddDays(1)));
    }

    [Fact]
    public void Countdown_rejects_non_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CountdownState.Ready(TimeSpan.Zero));
    }

    [Fact]
    public void Stopwatch_accumulates_across_pauses_and_records_laps()
    {
        var s = StopwatchState.Idle.Start(T0);
        s = s.Lap(T0.AddSeconds(30));
        s = s.Pause(T0.AddSeconds(45));
        s = s.Start(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(60), s.ElapsedAt(T0.AddMinutes(10).AddSeconds(15)));
        Assert.Equal([TimeSpan.FromSeconds(30)], s.Laps);
        Assert.True(StopwatchState.Reset().IsIdle);
    }

    [Fact]
    public void Pomodoro_goes_to_long_break_after_configured_cycles()
    {
        var config = new PomodoroConfig { CyclesBeforeLongBreak = 2 };
        var p = PomodoroState.Initial(config);

        p = p.Advance(config, T0, completed: true);
        Assert.Equal((PomodoroPhase.ShortBreak, 1), (p.Phase, p.CompletedFocusInSet));
        p = p.Advance(config, T0, completed: true);
        Assert.Equal(PomodoroPhase.Focus, p.Phase);
        p = p.Advance(config, T0, completed: true);
        Assert.Equal((PomodoroPhase.LongBreak, 2), (p.Phase, p.CompletedFocusInSet));
        p = p.Advance(config, T0, completed: true);
        Assert.Equal((PomodoroPhase.Focus, 0), (p.Phase, p.CompletedFocusInSet));
        Assert.Equal(TimeSpan.FromMinutes(25), p.Countdown.Duration);
    }

    [Fact]
    public void Skipped_focus_does_not_count_and_never_autostarts()
    {
        var config = new PomodoroConfig { AutoStartNext = true };
        var p = PomodoroState.Initial(config).Advance(config, T0, completed: false);

        Assert.Equal((PomodoroPhase.ShortBreak, 0), (p.Phase, p.CompletedFocusInSet));
        Assert.False(p.Countdown.IsRunning);
    }

    [Fact]
    public void Completed_phase_autostarts_next_when_configured()
    {
        var config = new PomodoroConfig { AutoStartNext = true };
        var p = PomodoroState.Initial(config).Advance(config, T0, completed: true);

        Assert.True(p.Countdown.IsRunning);
        Assert.Equal(T0.AddMinutes(5), p.Countdown.Deadline);
    }
}

public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 23, 30, 0, TimeSpan.Zero);

    private static NotificationRequest Req(NotificationPriority p, NotificationCategory c = NotificationCategory.News) =>
        new(c, p, "t", null, "tag");

    private static NotificationContext Ctx(TimeSpan? timeOfDay = null, bool fullscreen = false, bool dup = false, int recent = 0) =>
        new(Now, timeOfDay ?? TimeSpan.FromHours(12), fullscreen, dup, recent);

    [Theory]
    [InlineData(23 * 60, 22 * 60, 7 * 60, true)]  // 23:00 dentro de 22–07
    [InlineData(3 * 60, 22 * 60, 7 * 60, true)]   // 03:00 dentro (após a meia-noite)
    [InlineData(7 * 60, 22 * 60, 7 * 60, false)]  // fim é exclusivo
    [InlineData(12 * 60, 9 * 60, 18 * 60, true)]  // intervalo no mesmo dia
    [InlineData(12 * 60, 9 * 60, 9 * 60, false)]  // início = fim: desligado
    public void Quiet_hours(int minute, int start, int end, bool expected)
    {
        Assert.Equal(expected, NotificationPolicy.IsWithinQuietHours(TimeSpan.FromMinutes(minute), start, end));
    }

    [Fact]
    public void Common_news_does_not_interrupt_a_fullscreen_game_but_a_timer_does()
    {
        var settings = new NotificationSettings();
        Assert.Equal(DeliveryOutcome.SuppressedFullscreen, NotificationPolicy.Decide(Req(NotificationPriority.Normal), settings, Ctx(fullscreen: true)));
        Assert.Equal(DeliveryOutcome.Shown, NotificationPolicy.Decide(Req(NotificationPriority.High, NotificationCategory.Timer), settings, Ctx(fullscreen: true)));
    }

    [Fact]
    public void Manual_pause_lets_only_critical_through()
    {
        var settings = new NotificationSettings { PausedUntil = Now.AddMinutes(30) };
        Assert.Equal(DeliveryOutcome.SuppressedPaused, NotificationPolicy.Decide(Req(NotificationPriority.High), settings, Ctx()));
        Assert.Equal(DeliveryOutcome.Shown, NotificationPolicy.Decide(Req(NotificationPriority.Critical), settings, Ctx()));
        // Pausa expirada
        Assert.Equal(DeliveryOutcome.Shown, NotificationPolicy.Decide(Req(NotificationPriority.High), settings with { PausedUntil = Now.AddMinutes(-1) }, Ctx()));
    }

    [Fact]
    public void Quiet_hours_suppress_normal_but_not_high()
    {
        var settings = new NotificationSettings { QuietHoursEnabled = true };
        var night = TimeSpan.FromHours(23.5);
        Assert.Equal(DeliveryOutcome.SuppressedQuietHours, NotificationPolicy.Decide(Req(NotificationPriority.Normal), settings, Ctx(night)));
        Assert.Equal(DeliveryOutcome.Shown, NotificationPolicy.Decide(Req(NotificationPriority.High), settings, Ctx(night)));
    }

    [Fact]
    public void Duplicates_and_rate_limits()
    {
        var settings = new NotificationSettings();
        Assert.Equal(DeliveryOutcome.Duplicate, NotificationPolicy.Decide(Req(NotificationPriority.Critical), settings, Ctx(dup: true)));
        Assert.Equal(DeliveryOutcome.RateLimited, NotificationPolicy.Decide(Req(NotificationPriority.Normal), settings, Ctx(recent: 5)));
        Assert.Equal(DeliveryOutcome.Shown, NotificationPolicy.Decide(Req(NotificationPriority.High), settings, Ctx(recent: 5)));
    }

    [Fact]
    public void Timer_category_can_be_turned_off()
    {
        var settings = new NotificationSettings { TimerNotifications = false };
        Assert.Equal(DeliveryOutcome.SuppressedCategoryOff, NotificationPolicy.Decide(Req(NotificationPriority.High, NotificationCategory.Timer), settings, Ctx()));
    }
}

public sealed class ProductivityServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class MemoryStore : IProductivityStore, ISettingsStore
    {
        public ProductivitySnapshot? Active { get; set; }
        public List<FocusSession> Sessions { get; } = [];
        private readonly Dictionary<string, object> _settings = [];

        public ProductivitySnapshot? LoadActive() => Active;
        public void SaveActive(ProductivitySnapshot snapshot) => Active = snapshot;
        public void AddSession(FocusSession session) => Sessions.Add(session);
        public IReadOnlyList<FocusSession> RecentSessions(int count) => [.. Sessions.TakeLast(count)];
        public IReadOnlyList<FocusSession> SessionsSince(DateTimeOffset since) => [.. Sessions.Where(s => s.StartedAt >= since)];
        public T? Read<T>(string key) where T : class => _settings.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _settings[key] = value;
    }

    private sealed class FakeSink : INotificationSink
    {
        public List<NotificationRequest> Shown { get; } = [];
        public NotificationChannelStatus Status => NotificationChannelStatus.Enabled;
        public event Action<Guid, string?>? Activated;
        public Guid LastId { get; private set; }

        public DeliveryOutcome Show(Guid id, NotificationRequest request)
        {
            LastId = id;
            Shown.Add(request);
            return DeliveryOutcome.Shown;
        }

        public void Click(string? action) => Activated?.Invoke(LastId, action);
    }

    private sealed class MemoryHistory : INotificationHistory
    {
        public List<NotificationRecord> Records { get; } = [];
        public void Add(NotificationRecord record) => Records.Add(record);
        public bool ExistsSince(string dedupKey, DateTimeOffset since) => Records.Any(r => r.DedupKey == dedupKey && r.CreatedAt >= since);
        public int CountShownSince(NotificationCategory category, DateTimeOffset since) => Records.Count(r => r.Category == category && r.CreatedAt >= since && r.Outcome == DeliveryOutcome.Shown);
        public IReadOnlyList<NotificationRecord> Recent(int count) => [.. Records.TakeLast(count)];
        public int PruneBefore(DateTimeOffset cutoff) => Records.RemoveAll(r => r.CreatedAt < cutoff);
    }

    private sealed record Rig(ProductivityService Service, FakeTimeProvider Time, MemoryStore Store, FakeSink Sink, MemoryHistory History,
        SettingsService Settings, List<TimerCompleted> Completed, NotificationCenter Center);

    private static Rig Create(ProductivitySnapshot? existing = null, Action<SettingsService>? configure = null)
    {
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var store = new MemoryStore { Active = existing };
        var settings = new SettingsService(store, bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        configure?.Invoke(settings);
        var sink = new FakeSink();
        var history = new MemoryHistory();
        var center = new NotificationCenter(sink, history, settings, bus, time, NullLogger<NotificationCenter>.Instance);
        var service = new ProductivityService(store, settings, center, bus, time, NullLogger<ProductivityService>.Instance);
        var completed = new List<TimerCompleted>();
        bus.Subscribe<TimerCompleted>(completed.Add);
        service.Start();
        return new Rig(service, time, store, sink, history, settings, completed, center);
    }

    [Fact]
    public void Timer_completes_exactly_once_at_its_deadline_and_notifies()
    {
        var rig = Create();
        rig.Service.TimerStart(TimeSpan.FromMinutes(10));

        rig.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(rig.Completed);

        rig.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(100));
        var done = Assert.Single(rig.Completed);
        Assert.False(done.Late);
        Assert.Equal("Temporizador concluído", Assert.Single(rig.Sink.Shown).Title);
        Assert.Equal(NotificationPriority.High, rig.Sink.Shown[0].Priority);
        Assert.True(Assert.Single(rig.Store.Sessions).Completed);

        rig.Time.Advance(TimeSpan.FromHours(1));
        Assert.Single(rig.Completed); // não repete
    }

    [Fact]
    public void Paused_timer_never_completes()
    {
        var rig = Create();
        rig.Service.TimerStart(TimeSpan.FromMinutes(5));
        rig.Time.Advance(TimeSpan.FromMinutes(2));
        rig.Service.TimerToggle(); // pausa

        rig.Time.Advance(TimeSpan.FromHours(2));

        Assert.Empty(rig.Completed);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Snapshot.Timer!.RemainingAt(rig.Time.GetUtcNow()));
    }

    [Fact]
    public void Deadline_missed_while_app_was_closed_is_completed_as_late_on_start()
    {
        var running = CountdownState.Ready(TimeSpan.FromMinutes(25)).Start(T0.AddMinutes(-40)); // venceu há 15 min
        var existing = new ProductivitySnapshot(StopwatchState.Idle, running, T0.AddMinutes(-40), PomodoroState.Initial(new PomodoroConfig()), null);

        var rig = Create(existing);

        var done = Assert.Single(rig.Completed);
        Assert.True(done.Late);
        Assert.Contains("com o app fechado", rig.Sink.Shown[0].Body, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(-15), rig.Store.Sessions[0].EndedAt); // registra o horário real de término
    }

    [Fact]
    public void State_survives_restart()
    {
        var rig = Create();
        rig.Service.StopwatchToggle();
        rig.Time.Advance(TimeSpan.FromMinutes(3));

        var restarted = Create(rig.Store.Active);
        restarted.Time.Advance(TimeSpan.Zero);

        Assert.True(restarted.Service.Snapshot.Stopwatch.IsRunning);
        Assert.Equal(T0, restarted.Service.Snapshot.Stopwatch.RunningSince);
    }

    [Fact]
    public void Pomodoro_focus_completion_records_session_and_offers_to_start_break()
    {
        var rig = Create();
        rig.Service.PomodoroToggle();

        rig.Time.Advance(TimeSpan.FromMinutes(25) + TimeSpan.FromMilliseconds(100));

        Assert.Equal(PomodoroPhase.ShortBreak, rig.Service.Snapshot.Pomodoro.Phase);
        Assert.False(rig.Service.Snapshot.Pomodoro.Countdown.IsRunning);
        Assert.Equal(SessionKind.PomodoroFocus, Assert.Single(rig.Store.Sessions).Kind);
        var shown = Assert.Single(rig.Sink.Shown);
        Assert.Equal("Foco concluído", shown.Title);
        Assert.Equal("next", Assert.Single(shown.Actions!).Id);

        rig.Sink.Click("next"); // botão "Iniciar" da notificação
        Assert.True(rig.Service.Snapshot.Pomodoro.Countdown.IsRunning);
    }

    [Fact]
    public void Late_pomodoro_completion_does_not_autostart_next_phase()
    {
        var config = new PomodoroConfig { AutoStartNext = true };
        var focus = new PomodoroState(PomodoroPhase.Focus, 0, CountdownState.Ready(TimeSpan.FromMinutes(25)).Start(T0.AddHours(-1)));
        var existing = new ProductivitySnapshot(StopwatchState.Idle, null, null, focus, T0.AddHours(-1));

        var rig = Create(existing, s => s.UpdateProductivity(p => p with { Pomodoro = config }));

        Assert.Equal(PomodoroPhase.ShortBreak, rig.Service.Snapshot.Pomodoro.Phase);
        Assert.False(rig.Service.Snapshot.Pomodoro.Countdown.IsRunning);
    }

    [Fact]
    public void Repeat_action_restarts_the_timer()
    {
        var rig = Create();
        rig.Service.TimerStart(TimeSpan.FromMinutes(15));
        rig.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromMilliseconds(100));

        rig.Sink.Click("repeat");

        var timer = rig.Service.Snapshot.Timer!;
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Duration);
    }

    [Fact]
    public void Paused_notifications_are_recorded_but_not_shown()
    {
        var rig = Create();
        rig.Center.PauseFor(TimeSpan.FromHours(1));
        rig.Service.TimerStart(TimeSpan.FromMinutes(1));

        rig.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(100));

        Assert.Single(rig.Completed);
        Assert.Empty(rig.Sink.Shown);
        Assert.Equal(DeliveryOutcome.SuppressedPaused, Assert.Single(rig.History.Records).Outcome);
    }

    [Fact]
    public void Changing_pomodoro_config_updates_an_idle_session_only()
    {
        var rig = Create();
        rig.Settings.UpdateProductivity(p => p with { Pomodoro = p.Pomodoro with { FocusMinutes = 50 } });
        Assert.Equal(TimeSpan.FromMinutes(50), rig.Service.Snapshot.Pomodoro.Countdown.Duration);

        rig.Service.PomodoroToggle();
        rig.Settings.UpdateProductivity(p => p with { Pomodoro = p.Pomodoro with { FocusMinutes = 10 } });
        Assert.Equal(TimeSpan.FromMinutes(50), rig.Service.Snapshot.Pomodoro.Countdown.Duration);
    }

    [Fact]
    public void Stats_sum_today_focus()
    {
        var rig = Create();
        rig.Service.PomodoroToggle();
        rig.Time.Advance(TimeSpan.FromMinutes(25) + TimeSpan.FromMilliseconds(100));

        var stats = rig.Service.Stats();

        Assert.Equal(TimeSpan.FromMinutes(25), stats.FocusToday);
        Assert.Equal(1, stats.PomodorosToday);
    }
}
