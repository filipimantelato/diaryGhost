using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WinStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DesktopHub.Infrastructure.Media;

/// <summary>
/// Adaptador de <c>GlobalSystemMediaTransportControlsSessionManager</c> (Windows.Media.Control): a API
/// oficial que alimenta o painel de mídia do próprio Windows. Totalmente orientado a eventos — sem polling.
/// Eventos do Windows chegam em threads do pool; várias mudanças próximas são agrupadas (debounce)
/// e geram um único <see cref="MediaSessionsChanged"/>, publicado só se algo realmente mudou.
/// </summary>
public sealed partial class GsmtcMediaSource : IMediaSessionSource
{
    private const int MaxArtworkBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    /// <summary>Atualizações de linha do tempo dentro desta tolerância da posição extrapolada são ignoradas.</summary>
    private static readonly TimeSpan TimelineTolerance = TimeSpan.FromSeconds(1.5);

    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<GsmtcMediaSource> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, Entry> _entries = [];
    private readonly Lock _entriesLock = new();
    private readonly Timer _debounceTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private IReadOnlyList<MediaSessionSnapshot> _sessions = [];
    private Dictionary<string, GlobalSystemMediaTransportControlsSession> _byId = [];
    private string? _currentId;
    private bool _disposed;

    public GsmtcMediaSource(IEventBus bus, TimeProvider time, ILogger<GsmtcMediaSource> logger)
    {
        _bus = bus;
        _time = time;
        _logger = logger;
        _debounceTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsAvailable => _manager is not null;

    public IReadOnlyList<MediaSessionSnapshot> Sessions => _sessions;

    public string? SystemCurrentId => _currentId;

    /// <remarks>
    /// Todo acesso aos objetos do GSMTC acontece em threads do pool (MTA). Se o gerenciador for obtido na
    /// thread de UI (STA), chamadas posteriores de outras threads falham com RPC_E_WRONG_THREAD (0x8001010E)
    /// — observado em teste real. Por isso a obtenção e os comandos passam por <see cref="Task.Run(Func{Task})"/>.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() => StartOnPoolAsync(cancellationToken), cancellationToken);

    private async Task StartOnPoolAsync(CancellationToken cancellationToken)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            LogUnavailable(ex);
            return;
        }
        _manager.SessionsChanged += OnManagerChanged;
        _manager.CurrentSessionChanged += OnManagerChanged;
        await RefreshAsync().ConfigureAwait(false);
    }

    public Task<bool> SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken = default) =>
        Task.Run(() => SendOnPoolAsync(sessionId, command, cancellationToken), cancellationToken);

    public Task<bool> SeekAsync(string sessionId, TimeSpan position, CancellationToken cancellationToken = default) =>
        Task.Run(() => SeekOnPoolAsync(sessionId, position, cancellationToken), cancellationToken);

    private async Task<bool> SendOnPoolAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken)
    {
        if (!_byId.TryGetValue(sessionId, out var session))
        {
            return false;
        }
        try
        {
            var operation = command switch
            {
                MediaCommand.Play => session.TryPlayAsync(),
                MediaCommand.Pause => session.TryPauseAsync(),
                MediaCommand.TogglePlayPause => session.TryTogglePlayPauseAsync(),
                MediaCommand.Next => session.TrySkipNextAsync(),
                MediaCommand.Previous => session.TrySkipPreviousAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
            return await operation.AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            // O app fechou entre a exibição e o clique.
            LogCommandFailed(ex, command, sessionId);
            return false;
        }
    }

    private async Task<bool> SeekOnPoolAsync(string sessionId, TimeSpan position, CancellationToken cancellationToken)
    {
        if (!_byId.TryGetValue(sessionId, out var session))
        {
            return false;
        }
        try
        {
            return await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            LogCommandFailed(ex, MediaCommand.Play, sessionId);
            return false;
        }
    }

    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (!_disposed)
        {
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshAsync()
    {
        if (_manager is null || _disposed)
        {
            return;
        }
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _manager.GetCurrentSession();
            var sessions = _manager.GetSessions();
            List<Entry> entries;
            lock (_entriesLock)
            {
                SyncEntries(sessions);
                entries = [.. sessions.Select(s => _entries[s])];
            }

            var snapshots = new List<MediaSessionSnapshot>(entries.Count);
            var byId = new Dictionary<string, GlobalSystemMediaTransportControlsSession>(StringComparer.OrdinalIgnoreCase);
            string? currentId = null;
            foreach (var entry in entries)
            {
                var id = UniqueId(entry.Session.SourceAppUserModelId, byId);
                byId[id] = entry.Session;
                if (ReferenceEquals(entry.Session, current))
                {
                    currentId = id;
                }
                var snapshot = await BuildSnapshotAsync(entry, id).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }

            var changed = currentId != _currentId || !snapshots.SequenceEqual(_sessions);
            _byId = byId;
            _currentId = currentId;
            _sessions = snapshots;
            if (changed)
            {
                _bus.Publish(new MediaSessionsChanged(snapshots, currentId));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Sessões podem desaparecer no meio da leitura; a próxima notificação corrige o estado.
            LogRefreshFailed(ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SyncEntries(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        foreach (var gone in _entries.Keys.Except(sessions).ToList())
        {
            _entries[gone].Detach();
            _entries.Remove(gone);
        }
        foreach (var session in sessions)
        {
            if (!_entries.ContainsKey(session))
            {
                _entries[session] = new Entry(session, ScheduleRefresh);
            }
        }
    }

    private static string UniqueId(string aumid, Dictionary<string, GlobalSystemMediaTransportControlsSession> taken)
    {
        var id = string.IsNullOrEmpty(aumid) ? "desconhecido" : aumid;
        var candidate = id;
        for (var n = 2; taken.ContainsKey(candidate); n++)
        {
            candidate = $"{id}#{n}";
        }
        return candidate;
    }

    private async Task<MediaSessionSnapshot?> BuildSnapshotAsync(Entry entry, string id)
    {
        var session = entry.Session;
        if (entry.PropertiesDirty || entry.Properties is null)
        {
            entry.PropertiesDirty = false;
            var props = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            entry.Properties = props is null
                ? new Props(null, null, null, null)
                : new Props(Clean(props.Title), Clean(props.Artist) ?? Clean(props.AlbumArtist), Clean(props.AlbumTitle), await ReadArtworkAsync(props).ConfigureAwait(false));
        }

        var info = session.GetPlaybackInfo();
        if (info is null)
        {
            return null;
        }
        var c = info.Controls;
        var controls = new MediaControls(c.IsPlayEnabled, c.IsPauseEnabled, c.IsPlayPauseToggleEnabled, c.IsNextEnabled, c.IsPreviousEnabled, c.IsPlaybackPositionEnabled);
        var state = Map(info.PlaybackStatus);
        var timeline = ReadTimeline(session, entry, state == PlaybackState.Playing, info.PlaybackRate ?? 1.0);

        var p = entry.Properties;
        return new MediaSessionSnapshot(id, MediaSelection.FriendlyAppName(session.SourceAppUserModelId),
            p.Title, p.Artist, p.Album, state, controls, timeline, info.PlaybackRate, p.Artwork);
    }

    /// <summary>
    /// Só aceita linha do tempo com duração positiva (navegadores costumam não informar).
    /// Mantém o objeto anterior quando a nova posição bate com a extrapolação — evita redesenhos a cada segundo.
    /// </summary>
    private MediaTimeline? ReadTimeline(GlobalSystemMediaTransportControlsSession session, Entry entry, bool playing, double rate)
    {
        var t = session.GetTimelineProperties();
        var duration = t.EndTime - t.StartTime;
        if (duration <= TimeSpan.Zero)
        {
            entry.Timeline = null;
            return null;
        }
        var fresh = new MediaTimeline(t.Position - t.StartTime, duration, t.LastUpdatedTime);
        if (entry.Timeline is { } previous && previous.Duration == fresh.Duration && entry.TimelineWasPlaying == playing)
        {
            var now = _time.GetUtcNow();
            var drift = (previous.PositionAt(now, playing, rate) - fresh.PositionAt(now, playing, rate)).Duration();
            if (drift < TimelineTolerance)
            {
                return previous;
            }
        }
        entry.Timeline = fresh;
        entry.TimelineWasPlaying = playing;
        return fresh;
    }

    private async Task<MediaArtwork?> ReadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null)
        {
            return null;
        }
        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
            if (winStream.Size is 0 or > MaxArtworkBytes)
            {
                return null;
            }
            await using var stream = winStream.AsStreamForRead();
            using var buffer = new MemoryStream((int)winStream.Size);
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            return new MediaArtwork(bytes, Convert.ToHexString(SHA256.HashData(bytes))[..16]);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            LogArtworkFailed(ex);
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PlaybackState Map(WinStatus status) => status switch
    {
        WinStatus.Closed => PlaybackState.Closed,
        WinStatus.Opened => PlaybackState.Opened,
        WinStatus.Changing => PlaybackState.Changing,
        WinStatus.Stopped => PlaybackState.Stopped,
        WinStatus.Playing => PlaybackState.Playing,
        WinStatus.Paused => PlaybackState.Paused,
        _ => PlaybackState.Unknown,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _debounceTimer.Dispose();
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnManagerChanged;
            _manager.CurrentSessionChanged -= OnManagerChanged;
        }
        lock (_entriesLock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Detach();
            }
            _entries.Clear();
        }
    }

    private sealed record Props(string? Title, string? Artist, string? Album, MediaArtwork? Artwork);

    /// <summary>Assinaturas de eventos de uma sessão e o cache das partes caras (metadados e capa).</summary>
    private sealed class Entry
    {
        private readonly Action _changed;

        public Entry(GlobalSystemMediaTransportControlsSession session, Action changed)
        {
            Session = session;
            _changed = changed;
            Session.MediaPropertiesChanged += OnProperties;
            Session.PlaybackInfoChanged += OnPlayback;
            Session.TimelinePropertiesChanged += OnTimeline;
        }

        public GlobalSystemMediaTransportControlsSession Session { get; }
        public bool PropertiesDirty { get; set; } = true;
        public Props? Properties { get; set; }
        public MediaTimeline? Timeline { get; set; }
        public bool TimelineWasPlaying { get; set; }

        private void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        {
            PropertiesDirty = true;
            _changed();
        }

        private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _changed();

        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _changed();

        public void Detach()
        {
            Session.MediaPropertiesChanged -= OnProperties;
            Session.PlaybackInfoChanged -= OnPlayback;
            Session.TimelinePropertiesChanged -= OnTimeline;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sessões de mídia do Windows indisponíveis neste sistema")]
    private partial void LogUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comando {Command} para {SessionId} falhou")]
    private partial void LogCommandFailed(Exception ex, MediaCommand command, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao atualizar sessões de mídia")]
    private partial void LogRefreshFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capa indisponível para a sessão")]
    private partial void LogArtworkFailed(Exception ex);
}
