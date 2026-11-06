using System.Security.Cryptography;
using System.Text;

namespace DesktopHub.Core.News;

/// <summary>
/// Fontes curadas: veículos e órgãos com redação identificada, política editorial pública e feed oficial
/// (RSS/Atom publicado pelo próprio site — nada de raspagem). Todas verificadas em 2026-11-06.
/// Critérios e lista comentada em docs/NEWS-SOURCES.md.
/// </summary>
public static class TrustedSources
{
    public static IReadOnlyList<NewsSource> All { get; } =
    [
        // Geral
        Make("Agência Brasil", "https://agenciabrasil.ebc.com.br/rss/ultimasnoticias/feed.xml", NewsCategory.Geral),
        Make("BBC News Brasil", "https://feeds.bbci.co.uk/portuguese/rss.xml", NewsCategory.Geral),
        Make("g1", "https://g1.globo.com/rss/g1/", NewsCategory.Geral),
        Make("Folha de S.Paulo", "https://feeds.folha.uol.com.br/emcimadahora/rss091.xml", NewsCategory.Geral),
        // Política
        Make("Agência Senado", "https://www12.senado.leg.br/noticias/rss", NewsCategory.Politica),
        Make("Agência Câmara", "https://www.camara.leg.br/noticias/rss/ultimas-noticias", NewsCategory.Politica),
        Make("g1 Política", "https://g1.globo.com/rss/g1/politica/", NewsCategory.Politica),
        Make("Folha Poder", "https://feeds.folha.uol.com.br/poder/rss091.xml", NewsCategory.Politica),
        // Tecnologia
        Make("g1 Tecnologia", "https://g1.globo.com/rss/g1/tecnologia/", NewsCategory.Tecnologia),
        Make("Tecnoblog", "https://tecnoblog.net/feed/", NewsCategory.Tecnologia),
        Make("Canaltech", "https://canaltech.com.br/rss/", NewsCategory.Tecnologia),
        Make("Ars Technica", "https://feeds.arstechnica.com/arstechnica/index", NewsCategory.Tecnologia),
        Make("The Verge", "https://www.theverge.com/rss/index.xml", NewsCategory.Tecnologia),
        // Ciência e meio ambiente
        Make("Agência FAPESP", "https://agencia.fapesp.br/rss/", NewsCategory.Ciencia),
        Make("Revista Pesquisa FAPESP", "https://revistapesquisa.fapesp.br/feed/", NewsCategory.Ciencia),
        Make("g1 Ciência e Saúde", "https://g1.globo.com/rss/g1/ciencia-e-saude/", NewsCategory.Ciencia),
        Make("g1 Natureza", "https://g1.globo.com/rss/g1/natureza/", NewsCategory.Ciencia),
        Make("NASA", "https://www.nasa.gov/news-release/feed/", NewsCategory.Ciencia),
        Make("BBC Science & Environment", "https://feeds.bbci.co.uk/news/science_and_environment/rss.xml", NewsCategory.Ciencia),
        // Games
        Make("IGN Brasil", "https://br.ign.com/feed.xml", NewsCategory.Games),
        Make("Eurogamer", "https://www.eurogamer.net/feed", NewsCategory.Games),
        Make("Polygon", "https://www.polygon.com/rss/index.xml", NewsCategory.Games),
        // Desenvolvimento e segurança
        Make("GitHub Blog", "https://github.blog/feed/", NewsCategory.Desenvolvimento),
        Make("CISA — alertas de segurança", "https://www.cisa.gov/cybersecurity-advisories/all.xml", NewsCategory.Desenvolvimento),
    ];

    /// <summary>ID estável derivado da URL: a mesma fonte curada tem o mesmo ID em qualquer instalação.</summary>
    public static Guid IdFor(string feedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("diaryghost-news:" + feedUrl.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static NewsSource Make(string name, string url, NewsCategory category) => new()
    {
        Id = IdFor(url),
        Name = name,
        FeedUrl = url,
        Category = category,
        BuiltIn = true,
    };

    public static string Label(NewsCategory category) => category switch
    {
        NewsCategory.Politica => "Política",
        NewsCategory.Tecnologia => "Tecnologia",
        NewsCategory.Games => "Games",
        NewsCategory.Ciencia => "Ciência e meio ambiente",
        NewsCategory.Desenvolvimento => "Desenvolvimento",
        _ => "Geral",
    };
}
