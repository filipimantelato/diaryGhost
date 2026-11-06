# Fontes de notícias

O Diary Ghost só lê **feeds RSS/Atom oficiais**, publicados pelo próprio veículo ou órgão. Não há raspagem
de sites, agregadores de terceiros nem redes sociais. A lista está em
`src/DesktopHub.Core/News/TrustedSources.cs`; todas responderam HTTP 200 em 2026-11-06.

## Critérios para uma fonte curada
1. Redação identificada e política editorial pública (veículo jornalístico profissional), ou órgão
   público / instituição científica falando do próprio trabalho.
2. Feed oficial, servido por HTTPS, no domínio do próprio veículo (ou da sua CDN oficial).
3. Itens com título, link e, de preferência, data e resumo.
4. Cobertura útil para uma das categorias do app.

## Lista atual

| Categoria | Fonte | Tipo |
|---|---|---|
| Geral | Agência Brasil (EBC) | agência pública de notícias |
| Geral | BBC News Brasil | jornalismo internacional |
| Geral | g1 | jornalismo |
| Geral | Folha de S.Paulo | jornalismo |
| Política | Agência Senado | órgão público (Senado Federal) |
| Política | Agência Câmara | órgão público (Câmara dos Deputados) |
| Política | g1 Política, Folha Poder | jornalismo |
| Tecnologia | g1 Tecnologia, Tecnoblog, Canaltech | jornalismo de tecnologia (BR) |
| Tecnologia | Ars Technica, The Verge | jornalismo de tecnologia (EN) |
| Ciência e meio ambiente | Agência FAPESP, Revista Pesquisa FAPESP | fundação pública de pesquisa |
| Ciência e meio ambiente | g1 Ciência e Saúde, g1 Natureza | jornalismo |
| Ciência e meio ambiente | NASA | agência espacial (comunicados oficiais) |
| Ciência e meio ambiente | BBC Science & Environment | jornalismo internacional |
| Games | IGN Brasil, Eurogamer, Polygon | jornalismo de games |
| Desenvolvimento | GitHub Blog | blog oficial da plataforma |
| Desenvolvimento | CISA — alertas de segurança | agência de cibersegurança dos EUA (avisos oficiais) |

Avaliadas e **não** incluídas: The Enemy e Reuters (feeds não responderam), INMET (site não respondeu;
os alertas meteorológicos não têm API pública documentada — ver ADR-022).

## O que o usuário controla
- Ocultar qualquer fonte curada (não é apagada; pode voltar).
- Adicionar feeds próprios (ex.: releases do GitHub: `https://github.com/<dono>/<repo>/releases.atom`). O
  endereço é baixado e validado como RSS/Atom antes de entrar; só http(s).
- Seguir assuntos (destaque e filtro "Seguindo"; aviso opcional).

## Como cada notícia é exibida
Título e resumo convertidos para texto (nenhum HTML é renderizado), fonte, autor quando o feed informa,
**horário de publicação** (ou "sem data de publicação") e **horário da consulta**. O link abre no navegador
padrão, só se for http(s). Notícias iguais (mesma URL canônica ou mesmo título) aparecem uma vez; notícias
sobre o mesmo acontecimento em fontes diferentes ficam agrupadas ("Também em: …").
