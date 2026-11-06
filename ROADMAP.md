# Roadmap

Cada fase termina com um executável utilizável, testes passando e um benchmark registrado em
`docs/benchmarks/`. Um controle só conta como pronto quando executa a operação real — ou diz
explicitamente que depende de configuração.

```mermaid
flowchart LR
  F1[Fase 1<br/>Fundação] --> F2[Fase 2<br/>Widget + mídia]
  F1 --> F3[Fase 3<br/>Produtividade + notificações]
  F3 --> F4[Fase 4<br/>Agenda + lembretes]
  F1 --> F5[Fase 5<br/>Notícias + clima + dashboard]
  F3 --> F5
  F4 --> F6[Fase 6<br/>Integrações]
  F5 --> F6
  F2 --> F7[Fase 7<br/>Refinamento + instalador]
  F6 --> F7
```
Fases 2 e 3 são independentes entre si; a 3 vem antes da 4 porque a central de notificações nasce nela.

---

## Fase 1 — Fundação ✅ (2026-11-06)
Entregue: solução, arquitetura, SQLite com migrações/backup/recuperação de corrupção, configurações
persistentes, logs estruturados, segredos DPAPI, janela principal com navegação, bandeja, widget
flutuante (compacto/expandido, arrastar, posição memorizada e validada em multi-monitor, sempre
visível, bloqueio, opacidade, sem roubar foco, oculto em tela cheia, exclusão de capturas),
atalhos globais, instância única, `--quit`, inicialização com o Windows, 28 testes, benchmark.

Critérios de aceitação do MVP atendidos nesta fase: 2, 3, 4, 5\*, 13, 14, 15.
\* Mover entre monitores: lógica testada; validação manual com dois monitores pendente.

## Fase 2 — Widget e mídia
- Adaptador `GsmtcMediaSource` (`Windows.Media.Control`): sessões, metadados, capa, estado, linha do tempo.
- `MediaService`: sessão **selecionada** explicitamente; comandos só para ela; sessão padrão configurável;
  tratamento de sessões encerradas; botões só para comandos suportados (`PlaybackInfo.Controls`).
- Widget: estado Player (capa, faixa, artista, fonte, play/pause/anterior/próxima, progresso quando houver).
- Volume por app via Core Audio (`ISimpleAudioVolume`) quando o processo for identificável — distinto do volume global.
- Navegação por teclado no widget (foco explícito por atalho).
- **Aceite:** com Spotify e Brave tocando, listar ambos, pausar Spotify, depois controlar Brave, sem
  comando ir para a sessão errada; metadados ausentes não são inventados. Testes de seleção de sessão.

## Fase 3 — Produtividade e notificações
- Cronômetro (voltas), temporizador (presets 5/10/15/25/30/60), Pomodoro (ciclos, histórico, estatísticas).
- Cálculo por timestamps (`TimeProvider`), recuperação após suspensão/reinício (`timer_sessions`).
- Central de notificações: prioridades, horário de silêncio, modo foco, deduplicação, histórico,
  respeito ao "Não incomodar" e a jogos em tela cheia. ADR de toasts.
- **Aceite:** temporizador de 25 min notifica com app minimizado; suspender a máquina no meio não gera
  deriva; testes com `FakeTimeProvider`.

## Fase 4 — Agenda e lembretes
- Calendário local (dia/semana/mês/lista), eventos recorrentes (RRULE) com fuso, categorias e cores.
- Tarefas e lembretes com prioridade, recorrência, adiar 5/10/30/60, reagendar.
- `ReminderScheduler` que dorme até o próximo vencimento; reavalia em retomada/mudança de horário.
- **Aceite:** lembrete criado sobrevive a reinício e dispara; vencimento perdido durante suspensão é
  entregue ao retomar como "atrasado"; testes de recorrência (DST, fim de mês, exceções).

## Fase 5 — Informações
- Fontes RSS/Atom curadas + do usuário, ETag/If-Modified-Since, backoff, limites por fonte.
- Deduplicação (URL canônica + título normalizado) e agrupamento por acontecimento.
- Leitor: salvar, favoritar, lida, busca, filtros, seguir assuntos, ocultar fontes; HTML convertido em texto.
- Clima Open-Meteo (localização manual; automática só com consentimento); investigar alertas INMET.
- Dashboard com cartões reorganizáveis e redimensionáveis.
- **Aceite:** lista de notícias reais com fonte e horários de publicação e de consulta; clima real;
  offline mostra o último horário de atualização.

## Fase 6 — Integrações
- Google Calendar e Outlook (OAuth no navegador do sistema, PKCE, sync incremental, conflitos).
- Spotify Web API opcional (claramente separada do controle local; avisa sobre exigência de Premium).
- `hub-notify` + named pipe para Claude Code (hooks) e outras ferramentas; página de estado das integrações.

## Fase 7 — Refinamento
- Instalador, atalhos, desinstalação limpa, atualizações (ADR), assinatura de código.
- Acessibilidade (auditoria com Narrador, contraste, tamanho de fonte), i18n.
- Otimização guiada pelos benchmarks; testes de UI.

## Ideias futuras (não comprometidas)
Paleta de comandos e busca global · atalhos personalizáveis · resumo diário · IA opcional para resumos
(conteúdo externo tratado sempre como dado) · GitHub · Steam · perfis · exportar/importar · plugins.
