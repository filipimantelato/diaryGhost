using System.Net;
using System.Net.Http.Headers;

namespace DesktopHub.Infrastructure.Net;

/// <summary>
/// Um único <see cref="HttpClient"/> para o app (reaproveita conexões; DNS renovado a cada 10 min).
/// Identifica-se de forma genérica, sem dados do usuário; segue no máximo 5 redirecionamentos.
/// </summary>
public static class HttpClients
{
    public const string UserAgent = "DiaryGhost/0.5 (leitor local de RSS e clima; Windows)";

    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxAutomaticRedirections = 5,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    /// <summary>Lê o corpo com limite de tamanho (protege contra respostas gigantes).</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new HttpRequestException($"Resposta grande demais ({length / 1024 / 1024} MB).");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new HttpRequestException($"Resposta maior que {maxBytes / 1024 / 1024} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
