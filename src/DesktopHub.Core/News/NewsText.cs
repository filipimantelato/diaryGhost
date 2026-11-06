using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopHub.Core.News;

/// <summary>HTML de feed → texto simples. Nada de HTML é renderizado no app: só texto.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|iframe|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex Blocks();

    public static string ToPlain(string html, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(html);
        string text;
        try
        {
            text = Blocks().Replace(html, " ");
        }
        catch (RegexMatchTimeoutException)
        {
            text = html;
        }
        var sb = new StringBuilder(Math.Min(text.Length, maxLength * 2));
        var inTag = false;
        foreach (var c in text)
        {
            if (c == '<')
            {
                inTag = true;
                sb.Append(' ');
            }
            else if (c == '>' && inTag)
            {
                inTag = false;
            }
            else if (!inTag)
            {
                sb.Append(c);
            }
        }
        // Decodifica duas vezes: alguns feeds escapam o HTML da descrição e de novo as entidades.
        var decoded = WebUtility.HtmlDecode(WebUtility.HtmlDecode(sb.ToString()));
        if (decoded.Contains('<', StringComparison.Ordinal) && decoded.Contains('>', StringComparison.Ordinal) && decoded != html)
        {
            // Era HTML escapado (&lt;p&gt;…): remove as tags que apareceram depois de decodificar.
            return ToPlain(decoded, maxLength);
        }
        var collapsed = CollapseWhitespace(decoded);
        return collapsed.Length <= maxLength ? collapsed : collapsed[..Math.Max(0, maxLength - 1)].TrimEnd() + "…";
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>URLs: só http/https são aceitas (nada de javascript:, file: etc.) e há uma forma canônica para deduplicar.</summary>
public static class NewsUrl
{
    private static readonly string[] TrackingPrefixes = ["utm_", "fbclid", "gclid", "mc_", "cmpid", "ref", "xtor", "_ga", "igshid", "ncid"];

    public static bool TryParseHttp(string? text, Uri? baseUri, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
        {
            if (baseUri is null || !Uri.TryCreate(baseUri, text.Trim(), out parsed))
            {
                return false;
            }
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }
        url = parsed;
        return true;
    }

    /// <summary>"host/caminho?query": sem esquema, "www.", fragmento, barra final e parâmetros de rastreamento.</summary>
    public static string Canonical(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        var path = url.AbsolutePath.TrimEnd('/');
        var query = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TrackingPrefixes.Any(t => p.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();
        return host + path + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }
}

/// <summary>Título normalizado e agrupamento de notícias sobre o mesmo acontecimento.</summary>
public static class NewsTitles
{
    private static readonly HashSet<string> StopWords =
    [
        // pt
        "que", "para", "com", "por", "dos", "das", "nos", "nas", "uma", "como", "mais", "sobre", "apos", "entre", "ate", "pelo", "pela",
        "seu", "sua", "seus", "suas", "diz", "sao", "nao", "tem", "ser", "foi", "vai", "ano", "anos", "este", "esta", "isso",
        // en
        "the", "and", "for", "with", "from", "that", "this", "are", "was", "has", "have", "its", "into", "after", "about", "new", "will",
    ];

    /// <summary>Minúsculas, sem acentos e pontuação, espaços simples.</summary>
    public static string Key(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var sb = new StringBuilder(title.Length);
        var space = false;
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                space = true;
            }
        }
        return sb.ToString();
    }

    public static HashSet<string> Tokens(string key) =>
        [.. key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !StopWords.Contains(w))];

    /// <summary>
    /// Mesmo acontecimento: pelo menos 3 palavras relevantes em comum e similaridade de Jaccard ≥ 0,45.
    /// Conservador de propósito — juntar notícias diferentes é pior do que deixar duas separadas.
    /// </summary>
    public static bool SameStory(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }
        var shared = a.Count(b.Contains);
        return shared >= 3 && shared / (double)(a.Count + b.Count - shared) >= 0.45;
    }

    /// <summary>O título contém o assunto seguido (comparação sem acentos, por palavra inteira ou expressão).</summary>
    public static bool Matches(string titleKey, string topicKey) =>
        topicKey.Length > 0 && (" " + titleKey + " ").Contains(" " + topicKey + " ", StringComparison.Ordinal);
}
