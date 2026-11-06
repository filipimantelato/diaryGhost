using System.Net;
using DesktopHub.Core.News;
using DesktopHub.Infrastructure.Net;

namespace DesktopHub.Infrastructure.News;

/// <summary>GET condicional de feeds (If-None-Match / If-Modified-Since), até 5 MB, só http/https.</summary>
public sealed class HttpFeedFetcher(HttpClient http) : IFeedFetcher
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public async Task<FetchResult> FetchAsync(Uri url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return FetchResult.Fail("Só endereços http(s) são aceitos.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5");
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        if (!string.IsNullOrEmpty(lastModified))
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(FetchStatus.NotModified, null, etag, lastModified, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter is { } ra
                    ? ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                    : null;
                return FetchResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", retryAfter);
            }
            var bytes = await HttpClients.ReadLimitedAsync(response.Content, MaxBytes, cancellationToken).ConfigureAwait(false);
            return new FetchResult(FetchStatus.Ok, bytes,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"),
                null);
        }
        catch (HttpRequestException ex)
        {
            return FetchResult.Fail(ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchResult.Fail("Tempo esgotado.");
        }
    }
}
