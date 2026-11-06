# Análise do vídeo de referência

**Arquivo:** `VIDEO-2026-11-02-22-26-17.mov` — 15,4 s, 360×640 (gravação vertical de celular apontado para o monitor), H.264.
**Método:** quadros extraídos a cada 0,5 s com a API `Windows.Media.Editing` (sem ferramentas de terceiros) e
analisados em pranchas com marcação de tempo. O vídeo não está no repositório (arquivo pessoal).

## Linha do tempo observada

| Tempo | O que aparece |
|---|---|
| 0,0–0,5 s | Painel expandido **"Música"**: capa, título ("unravel"), artista ("Ado"), fonte ("Spotify"), ícone de abrir no app à direita, barra de progresso laranja com tempos (0:39 / 3:58), anterior / pausar / próxima, chips **"Brave" "Spotify"** (Spotify selecionado), barra de abas na base |
| 1,0–1,5 s | **Modo compacto**: pílula pequena com capa circular, "unravel · Ado" e barras de equalizador laranja à direita |
| 2,0–3,0 s | Volta ao painel Música; o cursor clica no chip **"Brave"**; o conteúdo faz transição cruzada |
| 3,5–4,5 s | Painel Música mostrando a sessão do **Brave** ("fears to fathom muito medo"). **Sem barra de progresso** e o painel fica mais baixo |
| 5,0–6,0 s | Aba **Início**: três cartões lado a lado — "Claude Code" (laranja, "0:10", "RessoEye · Concluído"), Música (capa como fundo, botão tocar), "Foco" (roxo, "25:00", "Pronto para começar") |
| 6,5–7,5 s | Aba **Claude Code**: projeto "RessoEye", "0:10", "Concluído" e uma trilha de etapas **Ler → Editar → Testar → Concluído** |
| 8,0–12,0 s | Aba **Foco**: anel com ícone de cronômetro, "Pronto para focar" → "Focando" → "Foco pausado", 25:00 → 24:57, quatro pontos de ciclo, tocar/pausar, pular, reiniciar |
| 12,5–15,0 s | Aba **Volume**: ícone de alto-falante, slider laranja, valor numérico (50 → 14 → 83 → 65 → 50), legenda "Saída de áudio padrão do Windows" |

Elementos constantes no modo expandido: **cabeçalho** com o nome do painel à esquerda e data/hora à direita
("seg., 2 nov. · 21:17"); **barra de abas** centralizada na base com 5 ícones (Início, Claude, Música,
Foco, Volume), aba ativa destacada por uma pílula cinza.

## O que é observável vs. hipótese

**Observável:** a mesma superfície muda de altura conforme o conteúdo (Brave sem progresso → painel menor);
a seleção explícita de fonte por chips; o compacto mostra a mídia atual; o Volume é o volume geral do
Windows; o Foco conta regressivamente a partir de 25:00 e pode ser pausado.

**Hipóteses (não dá para afirmar pelo vídeo):**
- O que dispara o recolhimento para o compacto (mouse saindo? tempo? clique?) — a transição acontece entre dois quadros.
- Se o equalizador reflete o áudio real ou é só animação decorativa.
- Se o "0:10" do Claude Code é tempo decorrido da tarefa ou outra métrica, e de onde vêm as etapas
  Ler/Editar/Testar. Isso é compatível com os **hooks** do Claude Code (eventos de uso de ferramentas),
  que é o plano da Fase 6.
- Se a sessão do navegador é por aba ou por navegador.

## O que adotamos (Fase 2) e por quê

| Do vídeo | No Desktop Hub | Observação |
|---|---|---|
| Cabeçalho título + data/hora | ✅ igual | relógio acorda 1×/min |
| Abas na base | ✅ Início, Mídia, Foco, Agenda, Atividade, Volume | Agenda vem da nossa especificação |
| Painel Música com chips de fonte | ✅ + chip **Auto** | "Auto" deixa explícito quando o app escolhe sozinho |
| Progresso só quando existe | ✅ | navegadores costumam não informar duração |
| Altura segue o conteúdo | ✅ animada (180 ms) | medida do conteúdo, sem redimensionar o HWND |
| Compacto com capa + "título · artista" | ✅ | sem mídia: hora e data |
| Equalizador animado | ⚙️ opcional, **estático por padrão** | animação contínua medida em ~0,18% de CPU vs ~0,01% |
| Volume geral com slider | ✅ Core Audio | legenda deixa claro que não é o volume de um app |
| Início com cartões | ✅ Mídia real; Foco e Claude dizem a fase em que chegam | sem dados fictícios |
| Botão "abrir no app" | ❌ adiado | não há forma confiável de trazer um app Win32 ao primeiro plano a partir de uma janela que não ativa |
| Clique na ilha expande | ✅ | o vídeo não mostra o gatilho; recolher é pelo botão do cabeçalho |

Identidade visual própria: cinzas neutros com destaque amarelo suave (configurável) em vez do laranja do
vídeo, tipografia Segoe UI Variable, ícones Segoe Fluent.
