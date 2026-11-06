namespace DesktopHub.Core.Media;

/// <summary>
/// Regra pura que decide qual sessão o app exibe e controla. Ordem de prioridade:
/// <list type="number">
/// <item>Sessão escolhida explicitamente pelo usuário, se ainda existir.</item>
/// <item>Fonte preferencial configurada (ex.: sempre Spotify), se tiver sessão.</item>
/// <item>Uma sessão que <b>acabou de começar</b> a tocar (desempate: a "atual" do Windows).</item>
/// <item>A sessão exibida antes, se ainda existir — histerese: pausar a sessão exibida não faz o app
///   pular para outra que já estava tocando; assim "pausar e depois retomar" atua sempre no mesmo app.</item>
/// <item>Uma sessão tocando; depois a "atual" do Windows; depois a primeira.</item>
/// </list>
/// Comandos sempre levam o ID da sessão exibida; se ela sumir, o comando falha em vez de ir para outra.
/// </summary>
public static class MediaSelection
{
    public static MediaSessionSnapshot? Resolve(
        IReadOnlyList<MediaSessionSnapshot> sessions,
        string? userSelectedId,
        string? preferredSourceId,
        string? systemCurrentId,
        string? previousActiveId = null,
        IReadOnlySet<string>? previouslyPlayingIds = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return null;
        }

        var startedPlaying = previouslyPlayingIds is null
            ? []
            : sessions.Where(s => s.IsPlaying && !previouslyPlayingIds.Contains(s.Id)).ToList();

        return Find(sessions, userSelectedId)
            ?? Find(sessions, preferredSourceId)
            ?? Playing(startedPlaying, systemCurrentId)
            ?? Find(sessions, previousActiveId)
            ?? Playing(sessions, systemCurrentId)
            ?? Find(sessions, systemCurrentId)
            ?? sessions[0];
    }

    private static MediaSessionSnapshot? Find(IReadOnlyList<MediaSessionSnapshot> sessions, string? id) =>
        id is null ? null : sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private static MediaSessionSnapshot? Playing(IReadOnlyList<MediaSessionSnapshot> sessions, string? systemCurrentId)
    {
        var playing = sessions.Where(s => s.IsPlaying).ToList();
        return playing.Count switch
        {
            0 => null,
            1 => playing[0],
            _ => Find(playing, systemCurrentId) ?? playing[0],
        };
    }

    /// <summary>
    /// Nome amigável a partir do AUMID quando o Windows não fornece um (apps Win32 como "Spotify.exe").
    /// Ex.: "Spotify.exe" → "Spotify", "Brave.XYZ" → "Brave", "MSEdge" → "Edge",
    /// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify".
    /// </summary>
    public static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return "Desconhecido";
        }

        var name = aumid;
        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
        {
            name = name[(bang + 1)..]; // app empacotado: "Família!App"
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = Path.GetFileName(name);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            name = name[..dot]; // "Brave.ABC123" → "Brave"
        }

        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "brave" => "Brave",
            "firefox" => "Firefox",
            "opera" => "Opera",
            "spotify" => "Spotify",
            "vlc" => "VLC",
            "zunemusic" or "music" => "Media Player",
            _ when name.Length > 0 => char.ToUpperInvariant(name[0]) + name[1..],
            _ => aumid,
        };
    }
}
