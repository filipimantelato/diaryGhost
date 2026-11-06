# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/); versões seguem SemVer.

## [0.2.0] — 2026-11-06 — Fase 2: widget e mídia
### Adicionado
- Controle universal de mídia pelas sessões do Windows (`Windows.Media.Control`): metadados, capa,
  estado, linha do tempo, tocar/pausar/anterior/próxima e busca na faixa, só para comandos suportados.
- Seleção explícita de sessão (chips "Auto" + apps), fonte preferencial, exibição de capa opcional.
- Volume geral do Windows (Core Audio), com silenciar e acompanhamento de mudanças externas.
- Widget redesenhado a partir do vídeo de referência: cabeçalho com data/hora, abas na base (Início,
  Mídia, Foco, Agenda, Atividade, Volume), altura que segue o conteúdo, menu de contexto.
- Página Mídia na janela principal; submenu de mídia na bandeja; cartão "Em reprodução" no dashboard.
- Ilha acionável por leitores de tela (UI Automation Invoke).
- `crash.log` síncrono para falhas fatais; log das causas de mudança de layout do widget.
- `scripts/measure-media.ps1`; `docs/VIDEO-ANALYSIS.md`; benchmark da Fase 2; ADR-009 a ADR-012.

### Corrigido
- Pausar a sessão exibida não faz mais o widget pular para outra sessão que estava tocando
  (o clique seguinte pausava o app errado) — encontrado em teste ao vivo.
- `RPC_E_WRONG_THREAD` ao ler sessões de mídia de threads do pool.

### Alterado
- Nova paleta padrão: cinzas neutros com destaque amarelo suave (`#C9A94E`), no app, no widget, na bandeja
  e no ícone. Quem tinha o roxo antigo salvo como padrão é migrado uma única vez; roxo continua disponível
  como opção. Avisos passam a laranja para não se confundirem com o destaque.
- Projetos de infraestrutura e app passam a `net10.0-windows10.0.19041.0` (projeções WinRT); pacote
  publicado: 159 → 213 MB.
- Painel "Hoje" da Fase 1 virou "Início" (configuração antiga migrada automaticamente).

## [0.1.0] — 2026-11-06 — Fase 1: fundação
### Adicionado
- Solução .NET 10 + WPF: `Core` (domínio/aplicação), `Infrastructure`, `App`, e dois projetos de teste (28 testes).
- Barramento de eventos tipado com isolamento de falhas por assinante.
- SQLite com migrações versionadas, backup antes de migrar, detecção e quarentena de banco corrompido,
  recusa de banco de versão mais nova.
- Configurações persistentes com sanitização de valores inválidos.
- Armazenamento de segredos com DPAPI.
- Logs JSON Lines com rotação diária e retenção.
- Widget flutuante (compacto/expandido, arrastar, multi-monitor, sempre visível, bloqueio, opacidade,
  sem foco, oculto em tela cheia, exclusão de capturas).
- Bandeja do sistema, janela principal com navegação, dashboard e configurações.
- Atalhos globais, instância única, `--quit`, inicialização com o Windows.
- Scripts `generate-icon.ps1`, `publish.ps1`, `measure.ps1`; benchmark da Fase 1.

### Segurança
- `Microsoft.Data.Sqlite` 10.0.14 (SQLite nativo 2.1.13), evitando o alerta GHSA-2m69-gcr7-jv3q da 2.1.11.
