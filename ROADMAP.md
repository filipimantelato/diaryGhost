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

## Fase 2 — Widget e mídia ✅ (2026-11-06)
Entregue:
- Adaptador `GsmtcMediaSource` (`Windows.Media.Control`): sessões, metadados, capa (≤ 4 MB), estado,
  linha do tempo, comandos; orientado a eventos, com debounce e sem republicar o que não mudou.
- `MediaService` + `MediaSelection`: seleção explícita (chips), fonte preferencial, histerese no automático
  (ADR-009), comandos só para a sessão exibida, apenas comandos suportados.
- Volume geral do Windows via Core Audio, acompanhando mudanças externas e troca de dispositivo.
- Widget redesenhado a partir do vídeo (`docs/VIDEO-ANALYSIS.md`): cabeçalho, abas na base, painéis
  Início / Mídia / Volume reais; Foco, Agenda e Atividade indicam a fase.
- Página Mídia na janela principal, submenu de mídia na bandeja, cartão "Em reprodução" no dashboard.
- Ilha acionável por UI Automation (leitores de tela), log de causas de mudança de layout,
  arquivo `crash.log` síncrono para falhas fatais.
- 52 testes (24 novos, quase todos de mídia, incluindo a regressão do ADR-009); benchmark com mídia tocando.

Critérios do MVP: **6 ✅** (controlar sessão real: pausar, retomar e avançar no Spotify verificados).
**7 ⚠️ parcial**: a troca entre sessões está implementada e testada em unidade, e foi exercitada ao vivo com
duas sessões, mas não houve um teste controlado Spotify + Brave lado a lado (o Brave não estava tocando).

Pendências levadas adiante:
- Teste manual Spotify + Brave (ver "Como testar" no README).
- Volume por app (ADR-012).
- Navegação por teclado dentro do widget expandido (hoje: UI Automation sim, foco por teclado não).
- Investigar com profiler o custo residual do painel Mídia (benchmark da Fase 2).
- Botão "abrir no app" da fonte.

## Identidade (entre as fases 2 e 3) ✅
Nome **Diary Ghost** e logo original do fantasminha (ADR-013).

## Fase 3 — Produtividade e notificações ✅ (2026-11-06)
Entregue:
- Cronômetro (voltas), temporizador (presets 5/10/15/25/30/60 e minutos livres), Pomodoro (durações,
  ciclos, início automático opcional, histórico, estatísticas de hoje e 7 dias).
- Modelo de tempo por timestamps UTC, um único timer, reagendamento ao retomar da suspensão (ADR-014);
  estado salvo a cada mudança e retomado ao reiniciar; prazos vencidos com o app fechado concluídos como
  "atrasados".
- Central de notificações (ADR-016) e toasts nativos com botões (ADR-015).
- Widget: painel Foco (anel, status, ciclos, controles), cartão Foco real no Início, compacto mostra o
  timer em andamento. Página Produtividade. Notificações em Configurações. Bandeja: Pomodoro, cronômetro,
  pausar notificações.
- 88 testes (30 novos de tempo, notificação e persistência).

Critérios do MVP: **8 ✅** (cronômetro) e **9 ✅** (temporizador com notificação — verificado ao vivo, inclusive
com o app fechado durante o prazo).

Verificado ao vivo: toast na Central de Notificações com botão; recuperação após fechar o app.
**Não verificado ao vivo:** suspensão/hibernação real do PC (coberto pelos testes de prazo vencido e pelo
reagendamento em `Resume`); clique nos botões do toast (coberto por teste com sink falso).

Pendências levadas adiante:
- Custo de redesenho do widget expandido (benchmark da Fase 3) → Fase 7.
- Reabrir o app a partir de um toast antigo (precisa de ativador COM) → Fase 7, junto do instalador.

## Fase 4 — Agenda e lembretes ✅ (2026-11-06)
Entregue:
- Calendário local: visões **dia e semana** (grade de horas, eventos sobrepostos lado a lado, linha do
  "agora"), **mês** e **lista** (60 dias); criar com clique duplo num horário/dia; editor em gaveta.
- Eventos: dia inteiro ou com horário (inclusive atravessando a meia-noite), recorrência com presets
  (todo dia, dias úteis, semanal, quinzenal, mensal no dia / na Nª semana / última semana / último dia,
  anual) ou RRULE personalizado, término por vezes ou data, até 2 avisos, local, notas, categoria.
  Séries: editar a série inteira ou **só esta ocorrência**; excluir só esta / esta e as próximas / tudo.
- Recorrência própria (subconjunto do RFC 5545) com horário de parede + fuso (ADR-017).
- Tarefas: criação rápida, prazo com ou sem hora, prioridade, categoria, aviso, recorrência (concluir
  avança para a próxima data, sem empilhar atrasadas), filtros, grupos por prazo, adiar/reagendar.
- Lembretes: página com criação rápida ("daqui a 5 min", "às 15:30"), próximos 7 dias e histórico com o
  resultado de cada aviso. Toast com **adiar 5/10/30/60** (lista de escolha) e **concluir** (ADR-019).
- `ReminderScheduler` com chave estável e marca d'água (ADR-018). Categorias com cores (Configurações).
- Widget: painel **Agenda** (em andamento/próximos de hoje e amanhã, tarefas de hoje com concluir).
  Dashboard: "Próximo compromisso" e "Tarefas pendentes" com dados reais.
- Migração de banco 3; 139 testes (51 novos).

Aceite: **lembrete sobrevive a reinício e dispara ✅** (teste + verificado ao vivo: entregue às 16:30:00);
**vencimento perdido é entregue ao voltar como "atrasado" ✅** (teste + verificado ao vivo fechando o app
durante o horário do aviso); **recorrência com DST, fim de mês e exceções ✅** (testes).

**Não verificado ao vivo:** suspensão real do PC (mesmo caminho do app fechado + `Resume`); clicar em
"Adiar"/"Concluir" no toast (coberto por testes com sink falso; a lista de escolha depende do Windows).

Pendências levadas adiante:
- Arrastar eventos na grade para mudar horário/duração.
- ~~Importar/exportar `.ics`~~ → entregue na Fase 6.
- Toast antigo que reabre o app fechado (ativador COM) → Fase 7.

## Fase 5 — Informações ✅ (2026-11-06)
Entregue:
- **Notícias** só de feeds oficiais curados (24 fontes em 6 categorias, critérios em `docs/NEWS-SOURCES.md`,
  ADR-020), desligadas até o usuário ativar. GET condicional (ETag/If-Modified-Since), recuo exponencial,
  Retry-After, intervalo mínimo e limite de itens por fonte, retenção configurável.
- Parser RSS 0.9x/2.0, RSS 1.0 e Atom sem DTD/entidades externas; HTML → texto; links só http(s).
- Deduplicação por URL canônica e título normalizado; agrupamento por acontecimento (ADR-021).
- Página Notícias: categorias, Seguindo, Salvas, Favoritas, busca, só não lidas, marcar como lidas; cartões
  com fonte, autor, publicação e consulta; "Também em"; fontes (ocultar, adicionar validando, remover),
  assuntos seguidos com aviso opcional, intervalo e retenção.
- **Clima** Open-Meteo com cidade escolhida pelo nome (ADR-022): atual, hoje e 6 dias; última previsão
  guardada e exibida offline com o horário; temperatura no cabeçalho do widget.
- **Dashboard** com dados reais em todos os cartões; reordenar, largura normal/larga, ocultar e restaurar.
- Migração 4; 179 testes (40 novos).

Aceite: **notícias reais com fonte e horários de publicação e de consulta ✅** (verificado ao vivo: 1.152
artigos de 24 fontes); **clima real ✅** (verificado ao vivo); **offline mostra o último horário ✅** (teste;
o cartão exibe "Sem atualização recente — dados de … às …").

**Não verificado ao vivo:** comportamento com a rede desligada (coberto por testes); aviso de assunto seguido.

Pendências levadas adiante:
- Alertas meteorológicos oficiais (INMET/Defesa Civil) se surgir fonte pública documentada.
- Localização automática com consentimento.
- Arrastar cartões do dashboard (hoje: botões, acessíveis por teclado).

## Fase 6 — Integrações ✅ (2026-11-07)
Entregue:
- **Claude Code** pelos hooks oficiais (ADR-023): o próprio `DiaryGhost.exe --hook claude-code` (sem abrir
  interface) reduz o JSON do hook ao contrato v1 — evento, nome da pasta, ID curto da sessão, texto do
  aviso; prompts, transcrição e caminhos nunca saem — e entrega por named pipe restrito ao usuário
  (`CurrentUserOnly`). Instalação dos hooks por clique, com confirmação, backup e preservação do resto do
  `settings.json`; configuração manual exibida para copiar. Avisos de "resposta pronta" e "aguardando você",
  limite de 60 eventos/min, histórico de 7 dias.
- Painel **Atividade** no widget e cartão do Início com o estado das sessões.
- **Agendas do Google/Outlook por assinatura iCal** (ADR-024): sem login; endereço secreto cifrado com
  DPAPI; somente leitura; sincronização a cada 30 min com GET condicional; IDs estáveis (avisos não se
  repetem); categoria e aviso por agenda.
- **iCalendar**: leitor (TZID IANA/Windows, UTC, dia inteiro, DURATION, RRULE, EXDATE, RECURRENCE-ID,
  cancelados) e exportador (dobra de linhas, UNTIL em UTC, VALARM); importar `.ics` como eventos locais.
- Página **Atividades e integrações**. Migração 5. 202 testes (23 novos).

Verificado ao vivo: evento de teste e hooks simulados (JSON real de hook na entrada padrão) chegando à
página; assinatura da agenda pública de feriados do Google (Finados, Proclamação, Consciência Negra no mês).
Ocioso com o canal escutando: 0 ms de CPU / 30 s, 58 MB privados.
**Não verificado ao vivo:** instalação dos hooks no seu `~/.claude/settings.json` (testada em arquivos
temporários; não alterei o seu arquivo) e uma sessão real do Claude Code disparando os hooks.

Não entregue (ADR-024):
- OAuth com Google Calendar / Microsoft Graph (duas vias, conflitos) e Spotify Web API: exigem registrar o
  app no console de cada empresa para obter um *client ID*. A arquitetura (portas, `ISecretStore` com DPAPI)
  está pronta para recebê-los.

## Fase 7 — Refinamento ✅ (2026-11-07)
Entregue:
- **Instalação por usuário** (ADR-025): `DiaryGhost.exe --install` copia para `%LOCALAPPDATA%\Programs\DiaryGhost`,
  cria o atalho no Menu Iniciar e a entrada em "Aplicativos instalados"; rodar de novo atualiza no lugar.
  **Desinstalação limpa** (`--uninstall`, também pelo Windows): programa, atalho, inicialização com o Windows,
  registro de notificações, protocolo e os hooks do Claude Code instalados pelo app; os dados só são
  apagados se o usuário escolher. `scripts/publish.ps1` gera também um `.zip` para distribuir.
- **Notificação antiga reabre o app** (ADR-026): o clique no corpo do toast abre `diaryghost:<página>`,
  registrado no Windows; funciona com o app fechado. Links só navegam (lista fixa de páginas), nunca agem.
  Segunda execução repassa a página à instância aberta.
- **Acessibilidade:** `scripts/a11y-audit.ps1` (UI Automation, 9 páginas, 204 controles: todos com nome e
  foco por teclado — corrigidos os botões internos do controle deslizante); teste de contraste WCAG lendo
  `Tokens.xaml` (corrigido o cinza terciário de 3,6:1 para ≥ 4,5:1); tamanho do texto 100/115/130%.
- 220 testes (18 novos).

Verificado ao vivo: abrir por `diaryghost:tasks`, repassar `diaryghost:reminders` à instância aberta (um
só processo), link inválido ignorado; escala de texto; auditoria sem problemas.
**Não verificado ao vivo:** `--install`/`--uninstall` reais (testados com pasta e chave de registro
temporárias — não instalei nada no seu Windows); clique num toast real com o app fechado.

Não entregue (ADR-027):
- **Assinatura de código** (exige certificado de uma autoridade; sem ela o SmartScreen avisa e o Smart App
  Control pode bloquear o executável baixado).
- **Atualização automática** (sem servidor de distribuição; atualizar = rodar `--install` da versão nova).
- **Widget expandido com renderização por GPU** e **i18n** (só pt-BR).
