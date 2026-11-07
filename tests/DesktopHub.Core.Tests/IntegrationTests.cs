using System.Text;
using System.Text.Json.Nodes;
using DesktopHub.Core.Activity;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Notifications;
using DesktopHub.Core.Security;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DesktopHub.Core.Tests;

public sealed class ActivityContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claude_hook_payload_is_reduced_to_the_minimum()
    {
        var hook = """
            {"session_id":"1a2b3c4d-5e6f-7a8b","transcript_path":"C:\\Users\\x\\.claude\\projects\\p\\t.jsonl",
             "cwd":"C:\\Users\\x\\repos\\meu-app","hook_event_name":"UserPromptSubmit","prompt":"minha senha é 123"}
            """;

        var line = ActivityContract.FromClaudeHook(hook)!;

        Assert.DoesNotContain("senha", line, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", line, StringComparison.Ordinal);
        var evt = ActivityContract.Parse(line, T0)!;
        Assert.Equal((ActivityKind.Working, "meu-app", "1a2b3c4d", (string?)null), (evt.Kind, evt.Project, evt.Session, evt.Message));
    }

    [Fact]
    public void Notification_keeps_only_its_message()
    {
        var line = ActivityContract.FromClaudeHook("""{"hook_event_name":"Notification","message":"Claude needs your permission to use Bash","cwd":"/home/a/proj"}""")!;
        var evt = ActivityContract.Parse(line, T0)!;
        Assert.Equal((ActivityKind.Waiting, "proj", "Claude needs your permission to use Bash"), (evt.Kind, evt.Project, evt.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"v":2,"source":"claude-code","event":"Stop"}""")]
    [InlineData("""{"v":1,"source":"outro-programa","event":"Stop"}""")]
    [InlineData("""{"v":1,"source":"claude-code"}""")]
    public void Lines_outside_the_contract_are_rejected(string line) => Assert.Null(ActivityContract.Parse(line, T0));

    [Fact]
    public void Invalid_hook_json_without_event_produces_nothing() => Assert.Null(ActivityContract.FromClaudeHook("{"));
}

public sealed class ActivityServiceTests
{
    private sealed class FakeChannel : IActivityChannel
    {
        public bool IsListening { get; private set; }
        public string? LastError => null;
        public event Action<string>? MessageReceived;
        public void Start() => IsListening = true;
        public void Close() => IsListening = false;
        public void Push(string line) => MessageReceived?.Invoke(line);
    }

    private sealed class MemoryActivityStore : IActivityStore
    {
        public List<ActivityEvent> Items { get; } = [];
        public void Add(ActivityEvent value) => Items.Insert(0, value);
        public IReadOnlyList<ActivityEvent> Recent(int count) => [.. Items.Take(count)];
        public int PruneBefore(DateTimeOffset cutoff) => Items.RemoveAll(i => i.ReceivedAt < cutoff);
    }

    private sealed class NoHooks : IClaudeHooksInstaller
    {
        public string SettingsPath => "x";
        public bool IsInstalled() => false;
        public string? Install(string command) => null;
        public void Uninstall() { }
    }

    private sealed class Sink : INotificationSink
    {
        public List<NotificationRequest> Shown { get; } = [];
        public NotificationChannelStatus Status => NotificationChannelStatus.Enabled;
        public event Action<Guid, string?>? Activated { add { } remove { } }
        public DeliveryOutcome Show(Guid id, NotificationRequest request)
        {
            Shown.Add(request);
            return DeliveryOutcome.Shown;
        }
    }

    private sealed class NullHistory : INotificationHistory
    {
        public void Add(NotificationRecord record) { }
        public bool ExistsSince(string dedupKey, DateTimeOffset since) => false;
        public int CountShownSince(NotificationCategory category, DateTimeOffset since) => 0;
        public IReadOnlyList<NotificationRecord> Recent(int count) => [];
        public int PruneBefore(DateTimeOffset cutoff) => 0;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = [];
        public T? Read<T>(string key) where T : class => _values.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _values[key] = value;
    }

    private static (ActivityService Service, FakeChannel Channel, Sink Sink, SettingsService Settings) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var settings = new SettingsService(new MemorySettings(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        var sink = new Sink();
        var center = new NotificationCenter(sink, new NullHistory(), settings, bus, time, NullLogger<NotificationCenter>.Instance);
        var channel = new FakeChannel();
        var service = new ActivityService(channel, new MemoryActivityStore(), new NoHooks(), settings, center, bus, time, NullLogger<ActivityService>.Instance);
        service.Start();
        return (service, channel, sink, settings);
    }

    [Fact]
    public void Channel_listens_only_when_enabled()
    {
        var (_, channel, _, settings) = Create();
        Assert.False(channel.IsListening);
        settings.UpdateIntegrations(i => i with { ClaudeCodeEnabled = true });
        Assert.True(channel.IsListening);
        settings.UpdateIntegrations(i => i with { ClaudeCodeEnabled = false });
        Assert.False(channel.IsListening);
    }

    [Fact]
    public void Finished_and_waiting_notify_according_to_preferences()
    {
        var (service, channel, sink, settings) = Create();
        settings.UpdateIntegrations(i => i with { ClaudeCodeEnabled = true, NotifyClaudeFinished = false });

        channel.Push(ActivityContract.FromClaudeHook("""{"hook_event_name":"Stop","cwd":"C:/r/app","session_id":"aaaaaaaa"}""")!);
        channel.Push(ActivityContract.FromClaudeHook("""{"hook_event_name":"Notification","message":"Permissão para Bash","cwd":"C:/r/app","session_id":"aaaaaaaa"}""")!);

        var shown = Assert.Single(sink.Shown);
        Assert.Equal("Claude Code aguardando você em app", shown.Title);
        Assert.Equal(NotificationPriority.High, shown.Priority);
        var session = Assert.Single(service.Sessions);
        Assert.Equal(ActivityKind.Waiting, session.Kind);
    }

    [Fact]
    public void Flood_is_rate_limited()
    {
        var (service, _, _, _) = Create();
        var accepted = Enumerable.Range(0, 100).Count(i => service.Receive(ActivityContract.TestMessage()) is not null);
        Assert.Equal(ActivityService.MaxPerMinute, accepted);
    }
}

public sealed class ICalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private const string Google = """
        BEGIN:VCALENDAR
        VERSION:2.0
        X-WR-CALNAME:Trabalho
        BEGIN:VTIMEZONE
        TZID:America/New_York
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:daily-1@google.com
        DTSTART;TZID=America/New_York:20261102T090000
        DTEND;TZID=America/New_York:20261102T093000
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR
        EXDATE;TZID=America/New_York:20261104T090000
        SUMMARY:Daily\, do time
        DESCRIPTION:Linha 1\nLinha 2
        LOCATION:Sala 3
        BEGIN:VALARM
        TRIGGER:-PT10M
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:daily-1@google.com
        RECURRENCE-ID;TZID=America/New_York:20261106T090000
        DTSTART;TZID=America/New_York:20261106T100000
        DTEND;TZID=America/New_York:20261106T103000
        SUMMARY:Daily (mudou de horário)
        END:VEVENT
        BEGIN:VEVENT
        UID:feriado@google.com
        DTSTART;VALUE=DATE:20261115
        DTEND;VALUE=DATE:20261116
        SUMMARY:Feriado
        END:VEVENT
        BEGIN:VEVENT
        UID:utc@google.com
        DTSTART:20261110T150000Z
        DURATION:PT2H
        SUMMARY:Reunião em UTC
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelado@google.com
        DTSTART:20261111T150000Z
        STATUS:CANCELLED
        SUMMARY:Cancelado
        END:VEVENT
        BEGIN:VEVENT
        UID:antigo@google.com
        DTSTART:20200101T150000Z
        SUMMARY:Muito antigo
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Google_style_calendar_is_imported()
    {
        var result = ICalendar.Parse(Google, TestZones.Eastern, Now, Now.AddDays(-180));

        Assert.Equal("Trabalho", result.CalendarName);
        Assert.Equal(4, result.Events.Count); // série, ocorrência alterada, feriado, UTC (cancelado e antigo fora)
        var series = result.Events.Single(e => e.Recurrence is not null);
        Assert.Equal(("Daily, do time", "Linha 1\nLinha 2", "Sala 3"), (series.Title, series.Notes, series.Location));
        Assert.Equal(TimeSpan.FromMinutes(30), series.Duration);
        Assert.Contains(new DateTime(2026, 11, 4, 9, 0, 0), series.Exceptions);
        Assert.Contains(new DateTime(2026, 11, 6, 9, 0, 0), series.Exceptions); // substituída pela alterada
        Assert.Contains(result.Events, e => e.Title == "Daily (mudou de horário)" && e.Start.Hour == 10 && e.Recurrence is null);
        var holiday = result.Events.Single(e => e.AllDay);
        Assert.Equal((new DateTime(2026, 11, 15), TimeSpan.FromDays(1)), (holiday.Start, holiday.Duration));
        var utc = result.Events.Single(e => e.Title == "Reunião em UTC");
        Assert.Equal((new DateTime(2026, 11, 10, 10, 0, 0), TimeSpan.FromHours(2)), (utc.Start, utc.Duration)); // 15:00Z = 10:00 EST
    }

    [Fact]
    public void Unsupported_rules_and_unknown_zones_produce_warnings_not_failures()
    {
        var result = ICalendar.Parse("""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:x
            DTSTART;TZID=Customized Time Zone:20261110T090000
            RRULE:FREQ=HOURLY
            SUMMARY:Estranho
            END:VEVENT
            END:VCALENDAR
            """, TestZones.Eastern, Now);

        var e = Assert.Single(result.Events);
        Assert.Null(e.Recurrence);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Non_calendar_content_is_rejected() =>
        Assert.Throws<FormatException>(() => ICalendar.Parse("<html></html>", TestZones.Eastern, Now));

    [Fact]
    public void Export_and_reimport_roundtrip()
    {
        var events = new[]
        {
            new CalendarEvent
            {
                Id = Guid.CreateVersion7(Now), Title = "Aula; com vírgula, e quebra\nde linha", Start = new DateTime(2026, 11, 9, 19, 0, 0),
                Duration = TimeSpan.FromMinutes(90), TimeZoneId = TestZones.Eastern.Id,
                Recurrence = RecurrenceRule.Weekly(DayOfWeek.Monday) with { Until = new DateTime(2026, 12, 21, 23, 59, 59) },
                Exceptions = [new DateTime(2026, 11, 16, 19, 0, 0)], ReminderMinutes = [15], Location = "Sala " + new string('x', 120),
            },
            new CalendarEvent { Id = Guid.CreateVersion7(Now), Title = "Aniversário", Start = new DateTime(2026, 11, 20), AllDay = true, Duration = TimeSpan.FromDays(1), TimeZoneId = "UTC" },
        };

        var ics = ICalendar.Write(events, TestZones.Eastern, Now);
        Assert.All(ics.Split("\r\n"), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.Contains("TRIGGER:-PT15M", ics, StringComparison.Ordinal);

        var back = ICalendar.Parse(ics, TestZones.Eastern, Now);
        var lesson = back.Events.Single(e => !e.AllDay);
        Assert.Equal((events[0].Title, events[0].Start, events[0].Duration), (lesson.Title, lesson.Start, lesson.Duration));
        Assert.Equal(events[0].Exceptions, lesson.Exceptions);
        Assert.Equal(6, RecurrenceExpander.Occurrences(lesson.Start, lesson.Recurrence, lesson.Exceptions, w => ZonedTime.ToUtc(w, TestZones.Eastern)).Count());
        Assert.Contains(back.Events, e => e.AllDay && e.Start == new DateTime(2026, 11, 20));
    }
}

public sealed class CalendarSubscriptionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class Secrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public void Save(string name, string value) => Values[name] = value;
        public string? Read(string name) => Values.GetValueOrDefault(name);
        public bool Delete(string name) => Values.Remove(name);
    }

    private const string Ics = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@x
        DTSTART:20261110T150000Z
        DTEND:20261110T160000Z
        SUMMARY:Reunião
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public async Task Subscription_imports_read_only_events_with_stable_ids_and_keeps_the_url_secret()
    {
        var store = new MemoryAgendaStore();
        var time = new FakeTimeProvider(T0);
        var (agenda, _, _, bus) = AgendaServiceTests.Create(store, time);
        var fetcher = new NewsServiceTests.FakeFetcher();
        const string url = "https://calendar.google.com/calendar/ical/abc%40group/private-SEGREDO/basic.ics";
        fetcher.Responses[url] = etag => etag == "\"1\""
            ? new FetchResult(FetchStatus.NotModified, null, etag, null, null)
            : new FetchResult(FetchStatus.Ok, Encoding.UTF8.GetBytes(Ics), "\"1\"", null, null);
        var secrets = new Secrets();
        var service = new CalendarSubscriptionService(store, agenda, fetcher, secrets, bus, time, NullLogger<CalendarSubscriptionService>.Instance);
        service.Start();

        Assert.Null(await service.AddAsync("Google", url.Replace("https://", "webcal://", StringComparison.Ordinal), null, 10));

        var e = Assert.Single(agenda.Events);
        Assert.True(e.IsReadOnly);
        Assert.Equal([10], e.ReminderMinutes);
        Assert.Throws<InvalidOperationException>(() => agenda.SaveEvent(e with { Title = "x" }));
        Assert.DoesNotContain(store.SubscriptionRows.Values, s => s.ToString().Contains("SEGREDO", StringComparison.Ordinal));
        Assert.Contains(secrets.Values.Values, v => v == url);
        Assert.Equal("calendar.google.com/…", service.MaskedUrl(service.Subscriptions[0].Id));

        await service.SyncAsync(force: true);
        Assert.Equal(e.Id, Assert.Single(agenda.Events).Id); // ID estável entre sincronizações
        await service.SyncAsync();
        Assert.Contains(fetcher.Requests, r => r.ETag == "\"1\"");

        service.Remove(service.Subscriptions[0].Id);
        Assert.Empty(agenda.Events);
        Assert.Empty(secrets.Values);
    }

    [Theory]
    [InlineData("file:///C:/agenda.ics")]
    [InlineData("javascript:alert(1)")]
    [InlineData("agenda.ics")]
    public async Task Only_web_addresses_are_accepted(string url)
    {
        var (agenda, time, store, bus) = AgendaServiceTests.Create();
        var service = new CalendarSubscriptionService(store, agenda, new NewsServiceTests.FakeFetcher(), new Secrets(), bus, time, NullLogger<CalendarSubscriptionService>.Instance);
        Assert.NotNull(await service.AddAsync("x", url, null, null));
    }
}
