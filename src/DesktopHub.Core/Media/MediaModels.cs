using DesktopHub.Core.Events;

namespace DesktopHub.Core.Media;

public enum PlaybackState
{
    Unknown,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

public enum MediaCommand
{
    Play,
    Pause,
    TogglePlayPause,
    Next,
    Previous,
}

/// <summary>Comandos que a sessão declara suportar. A UI exibe somente estes.</summary>
public sealed record MediaControls(bool Play, bool Pause, bool TogglePlayPause, bool Next, bool Previous, bool Seek)
{
    public static MediaControls None { get; } = new(false, false, false, false, false, false);

    public bool Supports(MediaCommand command) => command switch
    {
        MediaCommand.Play => Play || TogglePlayPause,
        MediaCommand.Pause => Pause || TogglePlayPause,
        MediaCommand.TogglePlayPause => TogglePlayPause || Play || Pause,
        MediaCommand.Next => Next,
        MediaCommand.Previous => Previous,
        _ => false,
    };
}

/// <summary>
/// Linha do tempo informada pelo app. A posição só é atualizada pelo app em eventos esporádicos,
/// então a posição atual é extrapolada a partir de <see cref="LastUpdated"/> enquanto toca.
/// </summary>
public sealed record MediaTimeline(TimeSpan Position, TimeSpan Duration, DateTimeOffset LastUpdated)
{
    public TimeSpan PositionAt(DateTimeOffset now, bool playing, double rate = 1.0)
    {
        if (!playing || now <= LastUpdated)
        {
            return Clamp(Position);
        }
        return Clamp(Position + TimeSpan.FromTicks((long)((now - LastUpdated).Ticks * rate)));
    }

    private TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value > Duration ? Duration : value;
}

/// <summary>
/// Retrato imutável de uma sessão de mídia do Windows. Estado transitório: nunca é persistido.
/// Campos ausentes ficam nulos — nada é inventado.
/// </summary>
public sealed record MediaSessionSnapshot(
    string Id,
    string AppName,
    string? Title,
    string? Artist,
    string? Album,
    PlaybackState State,
    MediaControls Controls,
    MediaTimeline? Timeline,
    double? PlaybackRate,
    MediaArtwork? Artwork)
{
    public bool IsPlaying => State == PlaybackState.Playing;
}

/// <summary>Bytes da capa como entregues pelo app (dado não confiável; decodificado com limites na UI).</summary>
public sealed class MediaArtwork(byte[] bytes, string fingerprint)
{
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;

    /// <summary>Permite à UI saber se a imagem mudou sem comparar bytes.</summary>
    public string Fingerprint { get; } = fingerprint;
}

/// <summary>A lista de sessões ou o estado de alguma delas mudou (publicado pelo adaptador do Windows).</summary>
public sealed record MediaSessionsChanged(IReadOnlyList<MediaSessionSnapshot> Sessions, string? SystemCurrentId) : IAppEvent;

/// <summary>A sessão exibida/controlada pelo app mudou, ou o estado dela mudou.</summary>
public sealed record ActiveMediaChanged(MediaSessionSnapshot? Active, IReadOnlyList<MediaSessionSnapshot> Sessions, bool IsPinnedByUser) : IAppEvent;

/// <summary>Porta para a fonte de sessões (implementada com Windows.Media.Control na Infrastructure).</summary>
public interface IMediaSessionSource : IDisposable
{
    bool IsAvailable { get; }

    Task StartAsync(CancellationToken cancellationToken);

    IReadOnlyList<MediaSessionSnapshot> Sessions { get; }

    string? SystemCurrentId { get; }

    /// <returns>false se a sessão não existe mais ou o app recusou o comando.</returns>
    Task<bool> SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken = default);

    Task<bool> SeekAsync(string sessionId, TimeSpan position, CancellationToken cancellationToken = default);
}
