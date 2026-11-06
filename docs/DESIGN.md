# Design system e interface

## Princípios
Escuro, calmo, denso porém organizado. Uma cor de destaque, usada com parcimônia (indicadores,
seleção, interruptores). Animações curtas (≤ 200 ms), desativáveis, e automaticamente desligadas quando
"Efeitos de animação" do Windows estão desligados.

## Tokens (`src/DesktopHub.App/Theme/Tokens.xaml`)
| Token | Valor | Uso |
|---|---|---|
| Bg | `#111318` | fundo da janela |
| Surface | `#1B1E25` | barra lateral, cartões |
| SurfaceElevated | `#252A34` | hover, seleção, menus |
| Border / BorderStrong | `#2C313C` / `#3A404D` | contornos |
| TextPrimary / Secondary / Tertiary | `#F1F3F7` / `#9BA3B2` / `#6B7383` | hierarquia de texto |
| Accent | `#8B5CF6` (trocável: roxo, azul, ciano, verde, âmbar, rosa) | destaque |
| Island | `#0D0F13` a 96% | fundo do widget |

Tipografia: Segoe UI Variable (Display para títulos, Text para corpo). Ícones: Segoe Fluent Icons
(fallback MDL2). Raios: 8 (controles), 12 (cartões), 22–24 (ilha).

## Componentes
`IconButton`, `IconToggleButton`, `SecondaryButton`, `NavItem` (RadioButton: setas do teclado navegam),
`ToggleSwitch` (CheckBox), `Card`, `Badge`, barra de rolagem fina, `FocusRing` (anel de foco no destaque).
Todo controle interativo tem `AutomationProperties.Name` ou conteúdo textual — validado com UI Automation.

## Telas (Modo A)
Dashboard · Notícias · Calendário · Agenda e tarefas · Lembretes · Mídia · Produtividade ·
Atividades e integrações · Configurações. Módulos ainda não implementados mostram uma página que diz
em qual fase chegam — nunca dados de exemplo.

## Estados do widget (Modo B)
Estrutura derivada do vídeo de referência ([análise](VIDEO-ANALYSIS.md)): cabeçalho (painel + data/hora),
corpo e barra de abas na base. A altura acompanha o conteúdo, com transição de 180 ms.

| Estado | Largura | Conteúdo | Fase |
|---|---|---|---|
| Compacto | 140–340 (segue o texto) | capa circular + "título · artista" + indicador de reprodução; sem mídia: hora e data | 2 ✅ |
| Início | 420 | cartões Mídia (real), Foco, Claude Code | 2 ✅ (Foco/Claude: fases 3/6) |
| Mídia | 420 | capa, título, artista, fonte e estado, progresso (se o app informa), controles suportados, chips Auto + sessões | 2 ✅ |
| Volume | 420 | silenciar, slider, valor; volume geral do Windows | 2 ✅ |
| Foco | 420 | cronômetro / temporizador / Pomodoro | 3 |
| Agenda | 420 | próximo evento, tempo restante, lembretes | 4 |
| Atividade | 420 | eventos de integrações (Claude Code via hooks) | 6 |

Interações: clique na ilha compacta expande; botão no cabeçalho recolhe; arrastar move (se
desbloqueado; limiar de 4 px separa clique de arraste); clique direito abre o menu (sempre visível,
bloquear, ocultar, abrir o app); posição, layout e painel são lembrados; o widget nunca recebe foco.
Leitores de tela veem a ilha como botão "expandir ou recolher" (padrão Invoke).

Indicador de reprodução: estático por padrão; equalizador animado opcional (custo medido no benchmark).
