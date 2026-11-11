# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/); versões seguem SemVer.

## [0.8.0] — 2026-11-11 — Fase 8: fundação financeira
### Adicionado
- Página **Finanças** (menu lateral, link `diaryghost:finance`):
  - **Contas e carteiras**: corrente, poupança, carteira digital, dinheiro, investimentos e outras; instituição,
    moeda, cor, saldo inicial com data, "entra no saldo disponível"; arquivar e reativar; ajustar saldo pelo extrato.
  - **Lançamentos**: receita, despesa, transferência, estorno (ligado à despesa, sem passar do valor dela) e
    ajuste de saldo; confirmado ou pendente; forma de pagamento, etiquetas e observações; editar, duplicar,
    excluir com "desfazer" por 10 s; "Salvar e novo" para lançar vários seguidos.
  - **Filtros e busca**: mês ou todo o período, conta, categoria (com subcategorias), tipo, situação, faixa de
    valor e busca sem acentos em descrição, observações, etiquetas e categoria; totais da lista filtrada.
  - **Categorias** padrão de despesa e receita, com subcategorias; criar, renomear, arquivar e excluir as sem uso.
  - **Resumo do mês**: saldo em contas, patrimônio, receitas e despesas (confirmadas e pendentes) e resultado,
    cada um com a definição no próprio cartão.
  - **Modo privacidade** (botão ou `Ctrl+Shift+H`): oculta todos os valores.
- Banco próprio `finance.db` (ADR-028), com verificação de integridade, quarentena e backup antes de migrar,
  como o banco principal. Se ele falhar, só o módulo fica indisponível.
- Proposta completa do módulo em `docs/FINANCE.md`; ADR-028 a 031.
- 55 testes novos (275 no total).

### Segurança e correção
- Dinheiro em centavos inteiros com aritmética verificada; o banco recusa ponto flutuante (tabelas STRICT).
- Saldos nunca armazenados: saldo inicial + partidas. Transferência e ajuste não contam como receita ou despesa.
- Cada lançamento e suas partidas são gravados numa única transação; restrições no banco impedem partida sem
  conta, valor zero e transferência com categoria.
- Logs só com contagens (há teste que procura valores e descrições nos logs); o módulo não referencia a rede.

## [0.7.0] — 2026-11-07 — Fase 7: instalação, notificações e acessibilidade
### Adicionado
- `DiaryGhost.exe --install`: instalação por usuário (sem administrador) em `%LOCALAPPDATA%\Programs\DiaryGhost`,
  atalho no Menu Iniciar e entrada em "Aplicativos instalados"; reinstalar atualiza.
- `--uninstall` (também por "Aplicativos instalados"): remove programa, atalho, inicialização com o Windows,
  registros e hooks do Claude Code; os dados só são apagados se você escolher.
- Clicar numa notificação abre a página certa mesmo com o app fechado (links `diaryghost:`).
- Tamanho do texto da janela: 100%, 115% e 130% (Configurações › Geral).
- `scripts/a11y-audit.ps1` (auditoria por UI Automation) e teste automático de contraste WCAG.
- `scripts/publish.ps1` gera também `artifacts/DiaryGhost-<versão>-win-x64.zip`.
- ADR-025 a 027.

### Corrigido
- Cinza terciário (dicas e legendas) com contraste insuficiente (3,6:1 → ≥ 4,5:1).
- Botões internos dos controles deslizantes sem nome para leitores de tela.

## [0.6.0] — 2026-11-07 — Fase 6: integrações
### Adicionado
- Claude Code pelos hooks oficiais: `DiaryGhost.exe --hook claude-code` repassa ao app, por um canal local
  restrito ao seu usuário, só o tipo do evento, o nome da pasta, um ID curto da sessão e o texto do aviso.
  Avisos de "resposta pronta" e "aguardando você"; instalação dos hooks por clique (com backup) ou manual.
- Painel Atividade no widget e cartão do Início com o estado das sessões.
- Agendas do Google e do Outlook por assinatura iCal (sem login; endereço cifrado; somente leitura).
- Importar e exportar arquivos `.ics`.
- Página Atividades e integrações; migração de banco 5 (`activity_events`, `calendar_subscriptions`).
- ADR-023 e ADR-024.

### Alterado
- O executável tem um ponto de entrada próprio (`Program.Main`) para atender hooks sem abrir a interface.

## [0.5.0] — 2026-11-06 — Fase 5: notícias, clima e dashboard
### Adicionado
- Notícias de 24 fontes confiáveis (feeds oficiais), em Geral, Política, Tecnologia, Games, Ciência e meio
  ambiente e Desenvolvimento; ativação explícita; deduplicação e agrupamento por acontecimento; salvar,
  favoritar, lida, busca, filtros, seguir assuntos (com aviso opcional), ocultar e adicionar fontes.
- Clima do Open-Meteo para a cidade escolhida: agora, hoje e 6 dias; funciona offline com a última previsão;
  temperatura no cabeçalho do widget.
- Dashboard reorganizável: ordem, largura e cartões ocultos salvos; todos os cartões com dados reais.
- Migração de banco 4: `news_sources`, `news_articles`, `cache`. ADR-020 a 022; `docs/NEWS-SOURCES.md`;
  benchmark da Fase 5.

### Corrigido
- `scripts/publish.ps1` encontra o SDK 10 em qualquer local comum de instalação.

## [0.4.0] — 2026-11-06 — Fase 4: agenda e lembretes
### Adicionado
- Calendário local com visões de dia, semana (grade de horas), mês e lista; eventos de dia inteiro ou com
  horário, recorrentes (presets ou RRULE), com exceções, até 2 avisos, local, notas e categoria.
- Edição e exclusão de séries: só esta ocorrência, esta e as próximas, ou a série inteira.
- Tarefas com prazo opcional, prioridade, categoria, aviso e recorrência; criação rápida, filtros,
  grupos por prazo, adiar/reagendar.
- Lembretes: criação rápida, próximos 7 dias, histórico com o resultado de cada aviso; toast com adiar
  5/10/30/60 min e concluir; entrega dos avisos perdidos com o app fechado ou o PC suspenso, como "atrasados".
- Painel Agenda no widget; cartões reais de compromissos e tarefas no dashboard.
- Categorias com cores em Configurações; opção para desligar avisos da agenda.
- Migração de banco 3: `calendar_events`, `tasks`, `agenda_categories`, `reminder_log`, `agenda_meta`.
- Estilo escuro para listas suspensas; ADR-017 a ADR-019; benchmark da Fase 4.

### Alterado
- Item de menu "Agenda e tarefas" virou "Tarefas" (o calendário tem item próprio).

## [0.3.0] — 2026-11-06 — Diary Ghost e Fase 3: produtividade e notificações
### Adicionado
- Nome **Diary Ghost** e logo original (fantasminha desenhado por código); ícone, executável, pasta de
  dados (`%LOCALAPPDATA%\DiaryGhost`, com migração automática da antiga) e inicialização com o Windows renomeados.
- Pomodoro, temporizador e cronômetro: widget (painel Foco, cartão no Início, compacto), página
  Produtividade com configuração, estatísticas e histórico; bandeja.
- Persistência e recuperação: estado retomado ao reiniciar; prazos vencidos com o app fechado ou o PC
  suspenso concluídos como "atrasados", com o horário real; reagendamento ao voltar da suspensão.
- Notificações nativas do Windows com botões; central com prioridades, pausa manual, horário de silêncio,
  respeito a jogos em tela cheia, deduplicação, limite de frequência e histórico de 30 dias.
- Migração de banco 2: `productivity_state`, `focus_sessions`, `notifications`.
- Estilo escuro para caixas de texto; ADR-013 a ADR-016; benchmark da Fase 3.

### Corrigido
- Logs JSON: campos do template (ex.: `Category`) ficam em `fields` e não colidem mais com campos fixos.

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
