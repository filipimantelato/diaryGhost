# Roadmap

Cada fase termina com um executÃ¡vel utilizÃ¡vel, testes passando e um benchmark registrado em
`docs/benchmarks/`. Um controle sÃ³ conta como pronto quando executa a operaÃ§Ã£o real â€” ou diz
explicitamente que depende de configuraÃ§Ã£o.

```mermaid
flowchart LR
  F1[Fase 1<br/>FundaÃ§Ã£o] --> F2[Fase 2<br/>Widget + mÃ­dia]
  F1 --> F3[Fase 3<br/>Produtividade + notificaÃ§Ãµes]
  F3 --> F4[Fase 4<br/>Agenda + lembretes]
  F1 --> F5[Fase 5<br/>NotÃ­cias + clima + dashboard]
  F3 --> F5
  F4 --> F6[Fase 6<br/>IntegraÃ§Ãµes]
  F5 --> F6
  F2 --> F7[Fase 7<br/>Refinamento + instalador]
  F6 --> F7
  F7 --> F8[Fase 8<br/>FinanÃ§as: fundaÃ§Ã£o]
  F8 --> F9[Fase 9<br/>FinanÃ§as: dashboard]
  F8 --> F10[Fase 10<br/>FinanÃ§as: cartÃµes]
  F9 --> F11[Fase 11<br/>FinanÃ§as: planejamento]
  F10 --> F11
  F11 --> F12[Fase 12<br/>FinanÃ§as: integraÃ§Ã£o]
  F12 --> F13[Fase 13<br/>FinanÃ§as: refinamento]
```
Fases 2 e 3 sÃ£o independentes entre si; a 3 vem antes da 4 porque a central de notificaÃ§Ãµes nasce nela.

---

## Fase 1 â€” FundaÃ§Ã£o âœ… (2026-11-06)
Entregue: soluÃ§Ã£o, arquitetura, SQLite com migraÃ§Ãµes/backup/recuperaÃ§Ã£o de corrupÃ§Ã£o, configuraÃ§Ãµes
persistentes, logs estruturados, segredos DPAPI, janela principal com navegaÃ§Ã£o, bandeja, widget
flutuante (compacto/expandido, arrastar, posiÃ§Ã£o memorizada e validada em multi-monitor, sempre
visÃ­vel, bloqueio, opacidade, sem roubar foco, oculto em tela cheia, exclusÃ£o de capturas),
atalhos globais, instÃ¢ncia Ãºnica, `--quit`, inicializaÃ§Ã£o com o Windows, 28 testes, benchmark.

CritÃ©rios de aceitaÃ§Ã£o do MVP atendidos nesta fase: 2, 3, 4, 5\*, 13, 14, 15.
\* Mover entre monitores: lÃ³gica testada; validaÃ§Ã£o manual com dois monitores pendente.

## Fase 2 â€” Widget e mÃ­dia âœ… (2026-11-06)
Entregue:
- Adaptador `GsmtcMediaSource` (`Windows.Media.Control`): sessÃµes, metadados, capa (â‰¤ 4 MB), estado,
  linha do tempo, comandos; orientado a eventos, com debounce e sem republicar o que nÃ£o mudou.
- `MediaService` + `MediaSelection`: seleÃ§Ã£o explÃ­cita (chips), fonte preferencial, histerese no automÃ¡tico
  (ADR-009), comandos sÃ³ para a sessÃ£o exibida, apenas comandos suportados.
- Volume geral do Windows via Core Audio, acompanhando mudanÃ§as externas e troca de dispositivo.
- Widget redesenhado a partir do vÃ­deo (`docs/VIDEO-ANALYSIS.md`): cabeÃ§alho, abas na base, painÃ©is
  InÃ­cio / MÃ­dia / Volume reais; Foco, Agenda e Atividade indicam a fase.
- PÃ¡gina MÃ­dia na janela principal, submenu de mÃ­dia na bandeja, cartÃ£o "Em reproduÃ§Ã£o" no dashboard.
- Ilha acionÃ¡vel por UI Automation (leitores de tela), log de causas de mudanÃ§a de layout,
  arquivo `crash.log` sÃ­ncrono para falhas fatais.
- 52 testes (24 novos, quase todos de mÃ­dia, incluindo a regressÃ£o do ADR-009); benchmark com mÃ­dia tocando.

CritÃ©rios do MVP: **6 âœ…** (controlar sessÃ£o real: pausar, retomar e avanÃ§ar no Spotify verificados).
**7 âš ï¸ parcial**: a troca entre sessÃµes estÃ¡ implementada e testada em unidade, e foi exercitada ao vivo com
duas sessÃµes, mas nÃ£o houve um teste controlado Spotify + Brave lado a lado (o Brave nÃ£o estava tocando).

PendÃªncias levadas adiante:
- Teste manual Spotify + Brave (ver "Como testar" no README).
- Volume por app (ADR-012).
- NavegaÃ§Ã£o por teclado dentro do widget expandido (hoje: UI Automation sim, foco por teclado nÃ£o).
- Investigar com profiler o custo residual do painel MÃ­dia (benchmark da Fase 2).
- BotÃ£o "abrir no app" da fonte.

## Identidade (entre as fases 2 e 3) âœ…
Nome **Diary Ghost** e logo original do fantasminha (ADR-013).

## Fase 3 â€” Produtividade e notificaÃ§Ãµes âœ… (2026-11-06)
Entregue:
- CronÃ´metro (voltas), temporizador (presets 5/10/15/25/30/60 e minutos livres), Pomodoro (duraÃ§Ãµes,
  ciclos, inÃ­cio automÃ¡tico opcional, histÃ³rico, estatÃ­sticas de hoje e 7 dias).
- Modelo de tempo por timestamps UTC, um Ãºnico timer, reagendamento ao retomar da suspensÃ£o (ADR-014);
  estado salvo a cada mudanÃ§a e retomado ao reiniciar; prazos vencidos com o app fechado concluÃ­dos como
  "atrasados".
- Central de notificaÃ§Ãµes (ADR-016) e toasts nativos com botÃµes (ADR-015).
- Widget: painel Foco (anel, status, ciclos, controles), cartÃ£o Foco real no InÃ­cio, compacto mostra o
  timer em andamento. PÃ¡gina Produtividade. NotificaÃ§Ãµes em ConfiguraÃ§Ãµes. Bandeja: Pomodoro, cronÃ´metro,
  pausar notificaÃ§Ãµes.
- 88 testes (30 novos de tempo, notificaÃ§Ã£o e persistÃªncia).

CritÃ©rios do MVP: **8 âœ…** (cronÃ´metro) e **9 âœ…** (temporizador com notificaÃ§Ã£o â€” verificado ao vivo, inclusive
com o app fechado durante o prazo).

Verificado ao vivo: toast na Central de NotificaÃ§Ãµes com botÃ£o; recuperaÃ§Ã£o apÃ³s fechar o app.
**NÃ£o verificado ao vivo:** suspensÃ£o/hibernaÃ§Ã£o real do PC (coberto pelos testes de prazo vencido e pelo
reagendamento em `Resume`); clique nos botÃµes do toast (coberto por teste com sink falso).

PendÃªncias levadas adiante:
- Custo de redesenho do widget expandido (benchmark da Fase 3) â†’ Fase 7.
- Reabrir o app a partir de um toast antigo (precisa de ativador COM) â†’ Fase 7, junto do instalador.

## Fase 4 â€” Agenda e lembretes âœ… (2026-11-06)
Entregue:
- CalendÃ¡rio local: visÃµes **dia e semana** (grade de horas, eventos sobrepostos lado a lado, linha do
  "agora"), **mÃªs** e **lista** (60 dias); criar com clique duplo num horÃ¡rio/dia; editor em gaveta.
- Eventos: dia inteiro ou com horÃ¡rio (inclusive atravessando a meia-noite), recorrÃªncia com presets
  (todo dia, dias Ãºteis, semanal, quinzenal, mensal no dia / na NÂª semana / Ãºltima semana / Ãºltimo dia,
  anual) ou RRULE personalizado, tÃ©rmino por vezes ou data, atÃ© 2 avisos, local, notas, categoria.
  SÃ©ries: editar a sÃ©rie inteira ou **sÃ³ esta ocorrÃªncia**; excluir sÃ³ esta / esta e as prÃ³ximas / tudo.
- RecorrÃªncia prÃ³pria (subconjunto do RFC 5545) com horÃ¡rio de parede + fuso (ADR-017).
- Tarefas: criaÃ§Ã£o rÃ¡pida, prazo com ou sem hora, prioridade, categoria, aviso, recorrÃªncia (concluir
  avanÃ§a para a prÃ³xima data, sem empilhar atrasadas), filtros, grupos por prazo, adiar/reagendar.
- Lembretes: pÃ¡gina com criaÃ§Ã£o rÃ¡pida ("daqui a 5 min", "Ã s 15:30"), prÃ³ximos 7 dias e histÃ³rico com o
  resultado de cada aviso. Toast com **adiar 5/10/30/60** (lista de escolha) e **concluir** (ADR-019).
- `ReminderScheduler` com chave estÃ¡vel e marca d'Ã¡gua (ADR-018). Categorias com cores (ConfiguraÃ§Ãµes).
- Widget: painel **Agenda** (em andamento/prÃ³ximos de hoje e amanhÃ£, tarefas de hoje com concluir).
  Dashboard: "PrÃ³ximo compromisso" e "Tarefas pendentes" com dados reais.
- MigraÃ§Ã£o de banco 3; 139 testes (51 novos).

Aceite: **lembrete sobrevive a reinÃ­cio e dispara âœ…** (teste + verificado ao vivo: entregue Ã s 16:30:00);
**vencimento perdido Ã© entregue ao voltar como "atrasado" âœ…** (teste + verificado ao vivo fechando o app
durante o horÃ¡rio do aviso); **recorrÃªncia com DST, fim de mÃªs e exceÃ§Ãµes âœ…** (testes).

**NÃ£o verificado ao vivo:** suspensÃ£o real do PC (mesmo caminho do app fechado + `Resume`); clicar em
"Adiar"/"Concluir" no toast (coberto por testes com sink falso; a lista de escolha depende do Windows).

PendÃªncias levadas adiante:
- Arrastar eventos na grade para mudar horÃ¡rio/duraÃ§Ã£o.
- ~~Importar/exportar `.ics`~~ â†’ entregue na Fase 6.
- Toast antigo que reabre o app fechado (ativador COM) â†’ Fase 7.

## Fase 5 â€” InformaÃ§Ãµes âœ… (2026-11-06)
Entregue:
- **NotÃ­cias** sÃ³ de feeds oficiais curados (24 fontes em 6 categorias, critÃ©rios em `docs/NEWS-SOURCES.md`,
  ADR-020), desligadas atÃ© o usuÃ¡rio ativar. GET condicional (ETag/If-Modified-Since), recuo exponencial,
  Retry-After, intervalo mÃ­nimo e limite de itens por fonte, retenÃ§Ã£o configurÃ¡vel.
- Parser RSS 0.9x/2.0, RSS 1.0 e Atom sem DTD/entidades externas; HTML â†’ texto; links sÃ³ http(s).
- DeduplicaÃ§Ã£o por URL canÃ´nica e tÃ­tulo normalizado; agrupamento por acontecimento (ADR-021).
- PÃ¡gina NotÃ­cias: categorias, Seguindo, Salvas, Favoritas, busca, sÃ³ nÃ£o lidas, marcar como lidas; cartÃµes
  com fonte, autor, publicaÃ§Ã£o e consulta; "TambÃ©m em"; fontes (ocultar, adicionar validando, remover),
  assuntos seguidos com aviso opcional, intervalo e retenÃ§Ã£o.
- **Clima** Open-Meteo com cidade escolhida pelo nome (ADR-022): atual, hoje e 6 dias; Ãºltima previsÃ£o
  guardada e exibida offline com o horÃ¡rio; temperatura no cabeÃ§alho do widget.
- **Dashboard** com dados reais em todos os cartÃµes; reordenar, largura normal/larga, ocultar e restaurar.
- MigraÃ§Ã£o 4; 179 testes (40 novos).

Aceite: **notÃ­cias reais com fonte e horÃ¡rios de publicaÃ§Ã£o e de consulta âœ…** (verificado ao vivo: 1.152
artigos de 24 fontes); **clima real âœ…** (verificado ao vivo); **offline mostra o Ãºltimo horÃ¡rio âœ…** (teste;
o cartÃ£o exibe "Sem atualizaÃ§Ã£o recente â€” dados de â€¦ Ã s â€¦").

**NÃ£o verificado ao vivo:** comportamento com a rede desligada (coberto por testes); aviso de assunto seguido.

PendÃªncias levadas adiante:
- Alertas meteorolÃ³gicos oficiais (INMET/Defesa Civil) se surgir fonte pÃºblica documentada.
- LocalizaÃ§Ã£o automÃ¡tica com consentimento.
- Arrastar cartÃµes do dashboard (hoje: botÃµes, acessÃ­veis por teclado).

## Fase 6 â€” IntegraÃ§Ãµes âœ… (2026-11-07)
Entregue:
- **Claude Code** pelos hooks oficiais (ADR-023): o prÃ³prio `DiaryGhost.exe --hook claude-code` (sem abrir
  interface) reduz o JSON do hook ao contrato v1 â€” evento, nome da pasta, ID curto da sessÃ£o, texto do
  aviso; prompts, transcriÃ§Ã£o e caminhos nunca saem â€” e entrega por named pipe restrito ao usuÃ¡rio
  (`CurrentUserOnly`). InstalaÃ§Ã£o dos hooks por clique, com confirmaÃ§Ã£o, backup e preservaÃ§Ã£o do resto do
  `settings.json`; configuraÃ§Ã£o manual exibida para copiar. Avisos de "resposta pronta" e "aguardando vocÃª",
  limite de 60 eventos/min, histÃ³rico de 7 dias.
- Painel **Atividade** no widget e cartÃ£o do InÃ­cio com o estado das sessÃµes.
- **Agendas do Google/Outlook por assinatura iCal** (ADR-024): sem login; endereÃ§o secreto cifrado com
  DPAPI; somente leitura; sincronizaÃ§Ã£o a cada 30 min com GET condicional; IDs estÃ¡veis (avisos nÃ£o se
  repetem); categoria e aviso por agenda.
- **iCalendar**: leitor (TZID IANA/Windows, UTC, dia inteiro, DURATION, RRULE, EXDATE, RECURRENCE-ID,
  cancelados) e exportador (dobra de linhas, UNTIL em UTC, VALARM); importar `.ics` como eventos locais.
- PÃ¡gina **Atividades e integraÃ§Ãµes**. MigraÃ§Ã£o 5. 202 testes (23 novos).

Verificado ao vivo: evento de teste e hooks simulados (JSON real de hook na entrada padrÃ£o) chegando Ã 
pÃ¡gina; assinatura da agenda pÃºblica de feriados do Google (Finados, ProclamaÃ§Ã£o, ConsciÃªncia Negra no mÃªs).
Ocioso com o canal escutando: 0 ms de CPU / 30 s, 58 MB privados.
**NÃ£o verificado ao vivo:** instalaÃ§Ã£o dos hooks no seu `~/.claude/settings.json` (testada em arquivos
temporÃ¡rios; nÃ£o alterei o seu arquivo) e uma sessÃ£o real do Claude Code disparando os hooks.

NÃ£o entregue (ADR-024):
- OAuth com Google Calendar / Microsoft Graph (duas vias, conflitos) e Spotify Web API: exigem registrar o
  app no console de cada empresa para obter um *client ID*. A arquitetura (portas, `ISecretStore` com DPAPI)
  estÃ¡ pronta para recebÃª-los.

## Fase 7 â€” Refinamento âœ… (2026-11-07)
Entregue:
- **InstalaÃ§Ã£o por usuÃ¡rio** (ADR-025): `DiaryGhost.exe --install` copia para `%LOCALAPPDATA%\Programs\DiaryGhost`,
  cria o atalho no Menu Iniciar e a entrada em "Aplicativos instalados"; rodar de novo atualiza no lugar.
  **DesinstalaÃ§Ã£o limpa** (`--uninstall`, tambÃ©m pelo Windows): programa, atalho, inicializaÃ§Ã£o com o Windows,
  registro de notificaÃ§Ãµes, protocolo e os hooks do Claude Code instalados pelo app; os dados sÃ³ sÃ£o
  apagados se o usuÃ¡rio escolher. `scripts/publish.ps1` gera tambÃ©m um `.zip` para distribuir.
- **NotificaÃ§Ã£o antiga reabre o app** (ADR-026): o clique no corpo do toast abre `diaryghost:<pÃ¡gina>`,
  registrado no Windows; funciona com o app fechado. Links sÃ³ navegam (lista fixa de pÃ¡ginas), nunca agem.
  Segunda execuÃ§Ã£o repassa a pÃ¡gina Ã  instÃ¢ncia aberta.
- **Acessibilidade:** `scripts/a11y-audit.ps1` (UI Automation, 9 pÃ¡ginas, 204 controles: todos com nome e
  foco por teclado â€” corrigidos os botÃµes internos do controle deslizante); teste de contraste WCAG lendo
  `Tokens.xaml` (corrigido o cinza terciÃ¡rio de 3,6:1 para â‰¥ 4,5:1); tamanho do texto 100/115/130%.
- 220 testes (18 novos).

Verificado ao vivo: abrir por `diaryghost:tasks`, repassar `diaryghost:reminders` Ã  instÃ¢ncia aberta (um
sÃ³ processo), link invÃ¡lido ignorado; escala de texto; auditoria sem problemas.
**NÃ£o verificado ao vivo:** `--install`/`--uninstall` reais (testados com pasta e chave de registro
temporÃ¡rias â€” nÃ£o instalei nada no seu Windows); clique num toast real com o app fechado.

NÃ£o entregue (ADR-027):
- **Assinatura de cÃ³digo** (exige certificado de uma autoridade; sem ela o SmartScreen avisa e o Smart App
  Control pode bloquear o executÃ¡vel baixado).
- **AtualizaÃ§Ã£o automÃ¡tica** (sem servidor de distribuiÃ§Ã£o; atualizar = rodar `--install` da versÃ£o nova).
- **Widget expandido com renderizaÃ§Ã£o por GPU** e **i18n** (sÃ³ pt-BR).

---

# Diary Ghost Finance

MÃ³dulo de finanÃ§as pessoais local-first. A proposta completa (anÃ¡lise da referÃªncia, arquitetura, modelo de
dados e regras de contabilizaÃ§Ã£o, interface, seguranÃ§a e testes) estÃ¡ em [docs/FINANCE.md](docs/FINANCE.md).
Proposta aprovada em 2026-11-11.

## Fase 8 â€” FundaÃ§Ã£o financeira âœ… (2026-11-11)
Entregue:
- `Money` (centavos em `long`, aritmÃ©tica verificada, leitura de "1.234,56"/"1234.56", formataÃ§Ã£o pt-BR, rateio
  com o resto na 1Âª parte), `finance.db` com migraÃ§Ã£o prÃ³pria, backup e quarentena (ADR-028).
- Contas (6 tipos, moeda, cor, saldo inicial com data, disponÃ­vel ou nÃ£o, arquivar), categorias padrÃ£o com
  subcategorias (um nÃ­vel), receitas, despesas, transferÃªncias (soma zero), estornos ligados Ã  despesa, ajustes
  de saldo (categoria de sistema), confirmados ou pendentes, etiquetas, forma de pagamento.
- Saldos derivados; resumo do mÃªs (saldo em contas, patrimÃ´nio, receitas, despesas, resultado), filtros e busca,
  duplicar, excluir com desfazer, modo privacidade.
- 275 testes (55 novos: dinheiro, invariantes contÃ¡beis, validaÃ§Ãµes, persistÃªncia, restriÃ§Ãµes do banco,
  atomicidade, logs sem valores, mÃ³dulo sem rede).

CritÃ©rios do MVP financeiro: **1 âœ…** (conta), **2 âœ…** (receitas e despesas), **3 âœ…** (saldo e histÃ³rico),
**4 âœ…** (categorias personalizadas), **13 âœ…** (fechar e reabrir sem perder dados), **14 âœ…** (sem internet: o
mÃ³dulo nÃ£o usa a rede).

Verificado ao vivo (instÃ¢ncia isolada): 2 contas, receita, despesa com subcategoria e etiquetas, despesa
pendente futura, transferÃªncia e estorno criados pela interface; saldos conferidos Ã  mÃ£o (9.187,53 â†’ 9.200,00
apÃ³s estorno de 12,47; 11.000,00 na poupanÃ§a); transferÃªncia fora de receitas e despesas; busca por etiqueta;
excluir e desfazer; modo privacidade; fechar e reabrir com tudo preservado; auditoria de acessibilidade (240
controles na varredura padrÃ£o + 56 com dados e editor aberto, todos com nome).

PendÃªncias levadas adiante:
- Painel lateral de ediÃ§Ã£o divide a largura com a lista; em janelas pequenas a lista fica baixa (rolÃ¡vel).
- Desempenho com 50 mil lanÃ§amentos (meta definida na proposta) â†’ Fase 13.

## Fase 9 â€” Dashboard financeiro âœ… (2026-11-11)
Entregue:
- `FinanceMetrics`: visÃ£o do mÃªs com data de corte (hoje, ou o Ãºltimo dia de um mÃªs passado); **saldo previsto
  decomposto de forma exata** (saldo hoje + a receber âˆ’ a pagar Â± transferÃªncias/ajustes = previsto, com teste);
  patrimÃ´nio; pendentes atrasados; comparaÃ§Ã£o com o mÃªs anterior (omitida quando nenhuma conta existia nele);
  receitas Ã— despesas por mÃªs; gasto por categoria principal somando subcategorias e descontando estornos; saldo
  do disponÃ­vel dia a dia (confirmado atÃ© hoje, previsto depois); pendentes atÃ© o fim do mÃªs.
- Aba **VisÃ£o geral** (padrÃ£o da pÃ¡gina): cartÃ£o do saldo previsto com barra segmentada e equaÃ§Ã£o, pendentes,
  4 indicadores com â–²â–¼ e % vs. mÃªs anterior, grÃ¡ficos de receitas Ã— despesas (6 meses), gastos por categoria
  (barras horizontais) e saldo ao longo do mÃªs (linha contÃ­nua/tracejada) com os pontos-chave em texto.
  "Ver como tabela" troca todos os grÃ¡ficos por tabelas. Cada cartÃ£o traz a definiÃ§Ã£o do nÃºmero (dica e texto de
  ajuda para leitores de tela). Layout em colunas ou empilhado conforme a largura.
- **Modo privacidade salvo** nas configuraÃ§Ãµes (vale para a pÃ¡gina e o dashboard, entre sessÃµes).
- CartÃ£o **FinanÃ§as** no Dashboard principal: oculto por padrÃ£o (valores financeiros sÃ³ aparecem por escolha em
  "Personalizar"); saldo, previsto, despesas do mÃªs, prÃ³ximos pendentes; respeita o modo privacidade.
- 285 testes (10 novos, com um cenÃ¡rio de novembro calculado Ã  mÃ£o).

CritÃ©rio do MVP financeiro: **8 âœ…** (dashboard com valores calculados corretamente).

Verificado ao vivo (mesma instÃ¢ncia de teste da Fase 8): previsto 18.400,00 = 20.200,00 âˆ’ 1.800,00 (aluguel
pendente); gastos Moradia 1.800,00 (86%) e AlimentaÃ§Ã£o 300,00 (14%, jÃ¡ com o estorno); saldo dia a dia
12.500,00 â†’ 20.500,00 (salÃ¡rio) â†’ 20.187,53 (mercado) â†’ 20.200,00 (estorno) â†’ 18.400,00 previsto; tabela com os
mesmos nÃºmeros; cartÃ£o no Dashboard e "Abrir FinanÃ§as"; modo privacidade persistindo apÃ³s reiniciar e mascarando
o Dashboard; auditoria de acessibilidade sem problemas; 0 ms de CPU em 30 s com a VisÃ£o geral aberta.

PendÃªncias levadas adiante:
- Faturas de cartÃ£o ainda nÃ£o entram no previsto nem no patrimÃ´nio â†’ Fase 10.
- "Reservado" (metas) ainda nÃ£o existe; o disponÃ­vel Ã© igual ao saldo em contas â†’ Fase 11.

| Fase | Entrega | CritÃ©rios do MVP financeiro |
|---|---|---|
| 8 â€” FundaÃ§Ã£o (A) âœ… | `Money` em centavos, `finance.db`, contas, categorias, lanÃ§amentos, transferÃªncias, ajustes, saldos derivados, pÃ¡gina LanÃ§amentos | 1, 2, 3, 4, 13, 14 |
| 9 â€” Dashboard (B) âœ… | indicadores com definiÃ§Ã£o formal, VisÃ£o geral por mÃªs, grÃ¡ficos, comparaÃ§Ã£o mensal, modo privacidade, cartÃµes no dashboard | 8 |
| 10 â€” CartÃµes (C) | ciclos com vigÃªncia, compras, parcelas, faturas, limite, pagamentos totais e parciais, estornos | 5, 6, 7 |
| 11 â€” Planejamento (D) | orÃ§amentos, recorrÃªncias, contas e assinaturas, metas (real Ã— planejado), projeÃ§Ãµes e simulador | 9, 10 |
| 12 â€” IntegraÃ§Ã£o (E) | lembretes, sobreposiÃ§Ã£o no calendÃ¡rio, painel do widget (valores ocultos), importaÃ§Ã£o CSV/OFX | 11, 12 |
| 13 â€” Refinamento (F) | relatÃ³rios e CSV, anexos, backup cifrado, avaliaÃ§Ã£o de cifragem e Windows Hello, desempenho, acessibilidade | â€” |
