using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DesktopHub.Core.News;

/// <summary>
/// Lê RSS 0.9x/2.0, RSS 1.0 (RDF) e Atom. Conteúdo de feed é dado não confiável: DTDs são ignoradas
/// e nenhuma entidade externa é resolvida (sem XXE), o tamanho é limitado e todo texto passa por
/// <see cref="HtmlText"/> antes de chegar à tela.
/// </summary>
public static class FeedParser
{
    public const long MaxCharacters = 10_000_000;
    public const int MaxItems = 200;

    public static ParsedFeed Parse(Stream content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(content, settings);
        return Parse(XDocument.Load(reader, LoadOptions.None));
    }

    public static ParsedFeed Parse(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Parse(stream);
    }

    private static ParsedFeed Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("Feed vazio.");
        var rootName = root.Name.LocalName;
        if (rootName is not ("rss" or "feed" or "RDF"))
        {
            throw new FormatException($"Não é um feed RSS/Atom (raiz '{rootName}').");
        }
        var isAtom = rootName == "feed";
        var title = Text(isAtom ? root : root.Descendants().FirstOrDefault(e => e.Name.LocalName == "channel") ?? root, "title");
        var items = root.Descendants()
            .Where(e => e.Name.LocalName is "item" or "entry")
            .Take(MaxItems)
            .Select(e => isAtom || e.Name.LocalName == "entry" ? ReadAtom(e) : ReadRss(e))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
        return new ParsedFeed(title is null ? null : HtmlText.ToPlain(title, 200), items);
    }

    private static FeedItem? ReadRss(XElement item)
    {
        var title = Text(item, "title");
        var link = Text(item, "link");
        var guid = Text(item, "guid");
        if (link is null && item.Elements().FirstOrDefault(e => e.Name.LocalName == "guid") is { } g &&
            !string.Equals((string?)g.Attribute("isPermaLink"), "false", StringComparison.OrdinalIgnoreCase))
        {
            link = guid;
        }
        var summary = Text(item, "description") ?? Text(item, "encoded");
        var author = Text(item, "creator") ?? Text(item, "author");
        var date = Text(item, "pubDate") ?? Text(item, "date") ?? Text(item, "published");
        return Build(guid ?? link, title, link, summary, author, date);
    }

    private static FeedItem? ReadAtom(XElement entry)
    {
        var links = entry.Elements().Where(e => e.Name.LocalName == "link").ToList();
        var link = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault();
        var author = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "author") is { } a ? Text(a, "name") ?? a.Value : null;
        return Build(Text(entry, "id"), Text(entry, "title"), (string?)link?.Attribute("href"),
            Text(entry, "summary") ?? Text(entry, "content"), author, Text(entry, "published") ?? Text(entry, "updated"));
    }

    private static FeedItem? Build(string? id, string? title, string? link, string? summary, string? author, string? date)
    {
        var cleanTitle = title is null ? "" : HtmlText.ToPlain(title, 300);
        if (cleanTitle.Length == 0)
        {
            return null;
        }
        return new FeedItem(id?.Trim(), cleanTitle, link?.Trim(), summary is null ? null : HtmlText.ToPlain(summary, 500),
            author is null ? null : HtmlText.ToPlain(author, 120) is { Length: > 0 } a ? a : null, ParseDate(date));
    }

    private static string? Text(XElement parent, string localName)
    {
        var value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = "+0000", ["UT"] = "+0000", ["UTC"] = "+0000", ["Z"] = "+0000",
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
        ["BRT"] = "-0300", ["BRST"] = "-0200",
    };

    private static readonly string[] RfcFormats =
    [
        "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "d MMM yyyy H:mm:ss zzz",
    ];

    /// <summary>Datas RFC 822 (RSS, com fusos por nome) e ISO 8601 (Atom). Sem fuso → UTC. Inválida → null.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Trim();
        var comma = t.IndexOf(',', StringComparison.Ordinal);
        if (comma is > 0 and <= 10)
        {
            t = t[(comma + 1)..].Trim(); // remove o dia da semana
        }
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            var zone = parts[^1];
            if (Zones.TryGetValue(zone, out var offset))
            {
                zone = offset;
            }
            if (zone.Length == 5 && zone[0] is '+' or '-' && zone[1..].All(char.IsDigit))
            {
                zone = zone[..3] + ":" + zone[3..];
                var candidate = string.Join(' ', parts[..^1]) + " " + zone;
                if (DateTimeOffset.TryParseExact(candidate, RfcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var rfc))
                {
                    return rfc.ToUniversalTime();
                }
            }
        }
        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var any)
            ? any.ToUniversalTime()
            : null;
    }
}
