using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Media;

/// <summary>
/// Caso de uso de mídia: mantém a seleção do usuário, resolve a sessão ativa e encaminha comandos
/// somente para a sessão indicada. Não conhece WinRT nem WPF.
/// </summary>
public sealed partial class MediaService : IDisposable
{
    private readonly IMediaSessionSource _source;
    private readonly IEventBus _bus;
    private readonly SettingsService _settings;
    private readonly ILogger<MediaService> _logger;
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private IReadOnlyList<MediaSessionSnapshot> _sessions = [];
    private string? _systemCurrentId;
    private string? _userSelectedId;
    private HashSet<string> _previouslyPlaying = new(StringComparer.OrdinalIgnoreCase);

    public MediaService(IMediaSessionSource source, IEventBus bus, SettingsService settings, ILogger<MediaService> logger)
    {
        _source = source;
        _bus = bus;
        _settings = settings;
        _logger = logger;
        _subscription = bus.Subscribe<MediaSessionsChanged>(OnSessionsChanged);
    }

    public bool IsAvailable => _source.IsAvailable;

    public MediaSessionSnapshot? Active { get; private set; }

    public IReadOnlyList<MediaSessionSnapshot> Sessions => _sessions;

    /// <summary>Verdadeiro quando a sessão exibida foi fixada pelo usuário (não segue o Windows automaticamente).</summary>
    public bool IsPinnedByUser
    {
        get
        {
            lock (_gate)
            {
                return _userSelectedId is not null && Active?.Id == _userSelectedId;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => _source.StartAsync(cancellationToken);

    /// <summary>O usuário escolheu explicitamente uma sessão (ex.: chip "Brave"). null volta ao automático.</summary>
    public void Select(string? sessionId)
    {
        lock (_gate)
        {
            _userSelectedId = sessionId;
        }
        // Voltar ao "Auto" é um pedido explícito: resolve do zero, sem a histerese da sessão anterior.
        Recompute(fresh: sessionId is null);
    }

    public async Task<bool> SendAsync(string sessionId, MediaCommand command)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (target is null)
        {
            LogMissing(sessionId, command);
            return false; // nunca redireciona para outra sessão
        }
        if (!target.Controls.Supports(command))
        {
            LogUnsupported(target.AppName, command);
            return false;
        }
        var ok = await _source.SendAsync(sessionId, command).ConfigureAwait(false);
        if (!ok)
        {
            LogRefused(target.AppName, command);
        }
        return ok;
    }

    public Task<bool> SeekAsync(string sessionId, TimeSpan position)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == sessionId);
        return target is { Controls.Seek: true } ? _source.SeekAsync(sessionId, position) : Task.FromResult(false);
    }

    private void OnSessionsChanged(MediaSessionsChanged e)
    {
        lock (_gate)
        {
            _sessions = e.Sessions;
            _systemCurrentId = e.SystemCurrentId;
        }
        Recompute();
    }

    private void Recompute(bool fresh = false)
    {
        MediaSessionSnapshot? active;
        IReadOnlyList<MediaSessionSnapshot> sessions;
        bool pinned;
        lock (_gate)
        {
            sessions = _sessions;
            active = MediaSelection.Resolve(sessions, _userSelectedId, _settings.Media.PreferredSourceId, _systemCurrentId,
                previousActiveId: fresh ? null : Active?.Id, previouslyPlayingIds: _previouslyPlaying);
            pinned = _userSelectedId is not null && active?.Id == _userSelectedId;
            Active = active;
            // Uma transição "começou a tocar" só conta uma vez.
            _previouslyPlaying = [.. sessions.Where(s => s.IsPlaying).Select(s => s.Id)];
        }
        _bus.Publish(new ActiveMediaChanged(active, sessions, pinned));
    }

    public void Dispose() => _subscription.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Comando {Command} ignorado: a sessão {SessionId} não existe mais")]
    private partial void LogMissing(string sessionId, MediaCommand command);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comando {Command} não suportado por {App}")]
    private partial void LogUnsupported(string app, MediaCommand command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{App} recusou o comando {Command}")]
    private partial void LogRefused(string app, MediaCommand command);
}
