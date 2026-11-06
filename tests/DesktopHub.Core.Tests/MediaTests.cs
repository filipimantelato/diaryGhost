using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Core.Tests;

public sealed class MediaSelectionTests
{
    private static readonly MediaControls All = new(true, true, true, true, true, true);

    internal static MediaSessionSnapshot Session(string id, PlaybackState state = PlaybackState.Paused, MediaControls? controls = null) =>
        new(id, MediaSelection.FriendlyAppName(id), "Faixa " + id, "Artista", null, state, controls ?? All, null, null, null);

    [Fact]
    public void No_sessions_resolves_to_null()
    {
        Assert.Null(MediaSelection.Resolve([], "Spotify.exe", null, null));
    }

    [Fact]
    public void User_selection_wins_even_when_another_session_is_playing()
    {
        var spotify = Session("Spotify.exe", PlaybackState.Paused);
        var brave = Session("Brave.ABC", PlaybackState.Playing);

        var active = MediaSelection.Resolve([spotify, brave], userSelectedId: "Spotify.exe", preferredSourceId: null, systemCurrentId: "Brave.ABC");

        Assert.Same(spotify, active);
    }

    [Fact]
    public void Preferred_source_wins_over_playing_session_when_user_did_not_choose()
    {
        var spotify = Session("Spotify.exe", PlaybackState.Paused);
        var brave = Session("Brave.ABC", PlaybackState.Playing);

        Assert.Same(spotify, MediaSelection.Resolve([brave, spotify], null, "spotify.exe", "Brave.ABC"));
    }

    [Fact]
    public void Without_choices_the_playing_session_is_shown()
    {
        var spotify = Session("Spotify.exe", PlaybackState.Paused);
        var brave = Session("Brave.ABC", PlaybackState.Playing);

        Assert.Same(brave, MediaSelection.Resolve([spotify, brave], null, null, "Spotify.exe"));
    }

    [Fact]
    public void Two_playing_sessions_tie_break_on_system_current()
    {
        var a = Session("A.exe", PlaybackState.Playing);
        var b = Session("B.exe", PlaybackState.Playing);

        Assert.Same(b, MediaSelection.Resolve([a, b], null, null, "B.exe"));
    }

    [Fact]
    public void Closed_user_selection_falls_back_without_error()
    {
        var brave = Session("Brave.ABC", PlaybackState.Paused);

        Assert.Same(brave, MediaSelection.Resolve([brave], "Spotify.exe", null, null));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Brave.ABCDEF123", "Brave")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("C:\\Apps\\vlc.exe", "VLC")]
    [InlineData("", "Desconhecido")]
    public void Friendly_names(string aumid, string expected)
    {
        Assert.Equal(expected, MediaSelection.FriendlyAppName(aumid));
    }

    [Fact]
    public void Timeline_extrapolates_only_while_playing_and_clamps_to_duration()
    {
        var t0 = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var timeline = new MediaTimeline(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), t0);

        Assert.Equal(TimeSpan.FromSeconds(40), timeline.PositionAt(t0.AddSeconds(10), playing: true));
        Assert.Equal(TimeSpan.FromSeconds(30), timeline.PositionAt(t0.AddSeconds(10), playing: false));
        Assert.Equal(TimeSpan.FromSeconds(60), timeline.PositionAt(t0.AddMinutes(5), playing: true));
        Assert.Equal(TimeSpan.FromSeconds(50), timeline.PositionAt(t0.AddSeconds(10), playing: true, rate: 2.0));
    }

    [Fact]
    public void Controls_support_toggle_as_play_and_pause()
    {
        var toggleOnly = new MediaControls(false, false, true, false, false, false);
        Assert.True(toggleOnly.Supports(MediaCommand.Play));
        Assert.True(toggleOnly.Supports(MediaCommand.Pause));
        Assert.False(toggleOnly.Supports(MediaCommand.Next));
    }
}

public sealed class MediaServiceTests
{
    private sealed class FakeSource : IMediaSessionSource
    {
        public List<(string Id, MediaCommand Command)> Sent { get; } = [];
        public bool IsAvailable => true;
        public IReadOnlyList<MediaSessionSnapshot> Sessions { get; set; } = [];
        public string? SystemCurrentId { get; set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken = default)
        {
            Sent.Add((sessionId, command));
            return Task.FromResult(true);
        }

        public Task<bool> SeekAsync(string sessionId, TimeSpan position, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class MemoryStore : ISettingsStore
    {
        private readonly Dictionary<string, object> _data = [];
        public T? Read<T>(string key) where T : class => _data.TryGetValue(key, out var v) ? (T)v : null;
        public void Write<T>(string key, T value) where T : class => _data[key] = value;
    }

    private static (MediaService Service, FakeSource Source, EventBus Bus, List<ActiveMediaChanged> Events) Create()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var settings = new SettingsService(new MemoryStore(), bus, NullLogger<SettingsService>.Instance);
        settings.Load();
        var source = new FakeSource();
        var service = new MediaService(source, bus, settings, NullLogger<MediaService>.Instance);
        var events = new List<ActiveMediaChanged>();
        bus.Subscribe<ActiveMediaChanged>(events.Add);
        return (service, source, bus, events);
    }

    [Fact]
    public async Task Command_goes_only_to_the_named_session()
    {
        var (service, source, bus, _) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X", PlaybackState.Playing)], "Brave.X"));

        Assert.True(await service.SendAsync("Spotify.exe", MediaCommand.Pause));

        Assert.Equal([("Spotify.exe", MediaCommand.Pause)], source.Sent);
    }

    [Fact]
    public async Task Command_to_a_closed_session_fails_instead_of_redirecting()
    {
        var (service, source, bus, _) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Brave.X", PlaybackState.Playing)], "Brave.X"));

        Assert.False(await service.SendAsync("Spotify.exe", MediaCommand.Pause));
        Assert.Empty(source.Sent);
    }

    [Fact]
    public async Task Unsupported_command_is_not_sent()
    {
        var (service, source, bus, _) = Create();
        var noNext = new MediaControls(true, true, true, false, false, false);
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Brave.X", PlaybackState.Playing, noNext)], null));

        Assert.False(await service.SendAsync("Brave.X", MediaCommand.Next));
        Assert.Empty(source.Sent);
    }

    [Fact]
    public void Explicit_selection_is_kept_and_reported_as_pinned()
    {
        var (service, _, bus, events) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X")], "Spotify.exe"));
        Assert.Equal("Spotify.exe", service.Active?.Id);

        service.Select("Brave.X");
        // O Windows continua dizendo que o Spotify é o atual e está tocando, mas a escolha do usuário prevalece.
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X")], "Spotify.exe"));

        Assert.Equal("Brave.X", service.Active?.Id);
        Assert.True(events[^1].IsPinnedByUser);
    }

    /// <summary>
    /// Regressão encontrada no teste ao vivo: Spotify e outra sessão tocando; pausar o Spotify fazia o
    /// widget pular para a outra, e o clique seguinte ("retomar") pausava o app errado.
    /// </summary>
    [Fact]
    public async Task Pausing_the_shown_session_does_not_jump_to_another_playing_one()
    {
        var (service, source, bus, _) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing)], "Spotify.exe"));
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X", PlaybackState.Playing)], "Spotify.exe"));
        // Brave começou a tocar → passa a ser exibido (transição explícita).
        Assert.Equal("Brave.X", service.Active?.Id);

        service.Select("Spotify.exe");
        service.Select(null); // volta ao automático mantendo o Spotify exibido
        Assert.Equal("Spotify.exe", service.Active?.Id);

        // Usuário pausa o Spotify; Brave continua tocando.
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Paused), MediaSelectionTests.Session("Brave.X", PlaybackState.Playing)], "Brave.X"));
        Assert.Equal("Spotify.exe", service.Active?.Id);

        // "Retomar" vai para o Spotify, não para o Brave.
        await service.SendAsync(service.Active!.Id, MediaCommand.TogglePlayPause);
        Assert.Equal(("Spotify.exe", MediaCommand.TogglePlayPause), source.Sent[^1]);
    }

    [Fact]
    public void A_session_that_starts_playing_takes_over_in_automatic_mode()
    {
        var (service, _, bus, _) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Paused), MediaSelectionTests.Session("Brave.X", PlaybackState.Paused)], null));
        Assert.Equal("Spotify.exe", service.Active?.Id);

        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Paused), MediaSelectionTests.Session("Brave.X", PlaybackState.Playing)], null));

        Assert.Equal("Brave.X", service.Active?.Id);
    }

    [Fact]
    public void Shown_session_closing_falls_back_to_a_playing_one()
    {
        var (service, _, bus, _) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X", PlaybackState.Paused)], null));
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Brave.X", PlaybackState.Paused)], null));

        Assert.Equal("Brave.X", service.Active?.Id);
    }

    [Fact]
    public void Selecting_null_returns_to_automatic()
    {
        var (service, _, bus, events) = Create();
        bus.Publish(new MediaSessionsChanged([MediaSelectionTests.Session("Spotify.exe", PlaybackState.Playing), MediaSelectionTests.Session("Brave.X")], null));
        service.Select("Brave.X");

        service.Select(null);

        Assert.Equal("Spotify.exe", service.Active?.Id);
        Assert.False(events[^1].IsPinnedByUser);
    }
}
