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
| Estado | Tamanho da ilha | Conteúdo | Fase |
|---|---|---|---|
| Compacto | 252 × 44 | ponto de atividade, ícone do painel, hora, status curto, expandir | 1 ✅ |
| Expandido | 420 × 150 | abas (Mídia, Foco, Agenda, Hoje, Atividade) + fixar, bloquear, recolher, ocultar | 1 ✅ |
| Player | expandido | capa, faixa, artista, fonte, controles, progresso, seletor de sessão | 2 |
| Produtividade | expandido | cronômetro / temporizador / Pomodoro + iniciar, pausar, reiniciar, concluir | 3 |
| Agenda | expandido | próximo evento, horário, tempo restante, lembretes | 4 |
| Informações | expandido | data ✅; temperatura, condição, manchete, alertas | 5 |
| Atividade | expandido | eventos de integrações (Claude Code etc.) | 6 |

Interações: duplo clique alterna compacto/expandido; arrastar move (se desbloqueado); posição e
painel são lembrados; o widget nunca recebe foco ao ser clicado.

## Sobre a referência em vídeo
O vídeo mencionado na especificação **não foi anexado** à conversa em que este documento foi escrito.
Os estados acima derivam apenas da descrição textual (ilha escura horizontal, player, cronômetro curto,
temporizador de ~25 min, barra ajustável, transições). Ao receber o vídeo, revisar proporções, tempos
de transição e a barra ajustável.
