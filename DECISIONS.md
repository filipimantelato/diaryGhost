# Registro de decisões arquiteturais (ADR)

Formato: contexto → decisão → consequências. Decisões só mudam com um novo ADR que substitua o anterior.

---

## ADR-001 — Stack: .NET 10 (LTS) + WPF
**Data:** 2026-11-06 · **Status:** aceita

### Alternativas avaliadas

| Critério | **WPF / .NET 10** | WinUI 3 / Windows App SDK | Tauri 2 (Rust + WebView2) | Electron | Avalonia 11 |
|---|---|---|---|---|---|
| RAM ociosa típica | ~50–70 MB privada (medido: §benchmarks) | maior (runtime WinAppSDK + XAML islands) | ~WebView2 por janela (processos de renderer, GPU, utilitários) | a maior (Chromium completo) | similar ao WPF |
| Integração nativa | P/Invoke direto + WinRT projetado (`Windows.Media.Control`) | WinRT nativo | crate `windows` (excelente), mas UI fala com nativo via IPC | módulos nativos/N-API | P/Invoke ok; WinRT viável |
| Widget transparente / sem foco | **janelas em camadas maduras**, `WS_EX_NOACTIVATE` trivial | transparência por pixel limitada/complexa | possível (janela transparente do WebView2), mas um WebView por janela | possível, pesado | bom |
| Bandeja | `NotifyIcon` (WinForms, oficial) | não nativo (libs terceiras) | plugin oficial | nativo | nativo |
| Qualidade visual | estilização total via XAML (exige escrever estilos) | Fluent nativo, Mica | HTML/CSS: máxima liberdade | HTML/CSS | boa, estilo próprio |
| Distribuição | exe self-contained; MSIX opcional | exige runtime do WinAppSDK ou empacotamento | instalador pequeno (WebView2 já no Win11) | grande | self-contained |
| Maturidade/manutenção | muito madura; open source; suporte no .NET 10 | em evolução, issues conhecidas | madura | madura | madura |
| "Navegador disfarçado"? | não | não | **sim** (WebView2) | **sim** | não |

### Decisão
WPF sobre .NET 10 LTS (suporte até nov/2028). Motivos determinantes:
1. O widget exige janela em camadas com transparência por pixel, cliques passando pelo vazio e sem
   roubar foco — o WPF faz isso de forma estável há anos.
2. Acesso direto a Win32 e WinRT (sessões de mídia, toasts, hooks de eventos, DPAPI) sem IPC.
3. Requisito explícito de não ser "um navegador disfarçado" exclui Tauri e Electron.
4. WinUI 3 seria a escolha "mais moderna", mas transparência e bandeja são exatamente seus pontos fracos.

### Consequências
- Precisamos escrever nosso próprio design system em XAML (feito em `Theme/`).
- Pacote self-contained é grande (~159 MB descompactado) porque inclui runtime, WPF e WinForms.
  Alternativa a avaliar na Fase 7: instalador que instala o runtime compartilhado do .NET.
- Trimming/AOT não são suportados para WPF; ReadyToRun compensa o tempo de inicialização.

---

## ADR-002 — Monólito modular com portas e adaptadores
**Status:** aceita

Um processo, três projetos (`Core`, `Infrastructure`, `App`). O Core define portas (ex.: `ISettingsStore`,
`ISecretStore`) e eventos; a Infrastructure implementa; o App compõe. Sem microsserviços, sem processos
auxiliares, sem broker. Comunicação entre módulos por `IEventBus` em processo.
Consequência: refatorar fronteiras é barato; se um dia um módulo precisar de processo próprio
(ex.: plugin não confiável), a porta já existe.

---

## ADR-003 — SQLite via Microsoft.Data.Sqlite, sem ORM; migrações próprias
**Status:** aceita

EF Core traria custo de inicialização e memória relevante para um app residente, e nossas consultas são
simples. Migrações são SQL versionado em código com `PRAGMA user_version`, uma transação por migração,
backup `VACUUM INTO` antes de migrar banco existente e recusa de bancos de versão mais nova.
Corrupção detectada por `PRAGMA quick_check` → arquivo movido para `corrupt/`, nunca apagado.
Versão fixada em 10.0.14, que traz `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 (a 2.1.11 tem o alerta GHSA-2m69-gcr7-jv3q).

---

## ADR-004 — Widget como janela em camadas de tamanho fixo
**Status:** aceita (revisar após medições da Fase 2)

Alternativas: (a) redimensionar o HWND a cada quadro — causa tremulação no WPF; (b) cantos do DWM
(`DWMWA_WINDOW_CORNER_PREFERENCE`) — raio fixo e sem opacidade; (c) **janela transparente fixa (440×176)
com a ilha animando dentro** — escolhida. Pixels com alfa 0 não recebem cliques, então a área vazia não
atrapalha. Custo: composição por software numa área pequena, apenas quando há mudança. Medido: 0 ms de
CPU parado.

---

## ADR-005 — WinForms apenas para `NotifyIcon` e `Screen`
**Status:** aceita

WPF não tem ícone de bandeja. Opções: biblioteca terceira (H.NotifyIcon), P/Invoke de `Shell_NotifyIcon`
escrito à mão, ou `System.Windows.Forms.NotifyIcon` (parte do próprio .NET). Escolhido o último: oficial,
sem dependência externa. `Screen.AllScreens` reaproveitado para áreas de trabalho por monitor.

---

## ADR-006 — Logs JSON Lines próprios em vez de Serilog
**Status:** aceita

Um provedor de ~150 linhas sobre `Microsoft.Extensions.Logging` atende: estruturado, rotação diária,
retenção, escrita em segundo plano com fila limitada. Serilog seria várias dependências a mais.
Política: mensagens via `[LoggerMessage]` (sem alocação quando o nível está desligado) e nunca registrar
segredos — somente campos explícitos do template são serializados.

---

## ADR-007 — Segredos com DPAPI (escopo do usuário) dentro do SQLite
**Status:** aceita

Credential Manager tem limite de tamanho por credencial e API mais verbosa; DPAPI `CurrentUser` com
entropia própria do app é simples e só a mesma conta do Windows decifra. Falha de decifração é tratada
como "integração desconectada — reautenticar".

---

## ADR-008 — Integração com Claude Code via hooks + named pipe
**Status:** proposta (implementação na Fase 6)

O Claude Code oferece hooks oficiais configurados em `settings.json` (eventos como `Notification`, `Stop`,
`PreToolUse`, `PostToolUse`, `SessionStart`), que executam um comando recebendo JSON pelo stdin.
O hub fornecerá `hub-notify.exe`, que valida e repassa esse JSON por um named pipe local com ACL restrita
ao usuário atual. Não há API oficial para observar conversas do Claude no navegador ou no app desktop —
isso fica explicitamente fora de escopo.

---

## ADR-009 — Seleção de sessão de mídia explícita, com histerese no modo automático
**Data:** 2026-11-06 · **Status:** aceita

Prioridade: sessão fixada pelo usuário → fonte preferencial → sessão que **acabou de começar** a tocar →
sessão exibida anteriormente → qualquer sessão tocando → "atual" do Windows → primeira.
Comandos levam sempre o ID da sessão exibida; se ela fechou, o comando falha (nunca é redirecionado).

**Por que a histerese:** no teste ao vivo, com Spotify e uma segunda sessão tocando, pausar o Spotify fez a
regra "mostrar quem toca" trocar a sessão exibida, e o clique seguinte ("retomar") pausou o outro app —
exatamente o comportamento que a especificação proíbe. Agora a exibição só muda por uma transição
"começou a tocar". Voltar ao chip **Auto** resolve do zero (pedido explícito do usuário).
Coberto por testes de regressão em `MediaTests.cs`.

## ADR-010 — Todo acesso ao GSMTC em threads MTA
**Status:** aceita

Observado: obter o `GlobalSystemMediaTransportControlsSessionManager` na thread de UI (STA) e usá-lo a
partir de eventos (threads do pool) gerou `RPC_E_WRONG_THREAD` (0x8001010E). O adaptador agora obtém o
gerenciador e executa comandos via `Task.Run`; eventos do Windows já chegam no pool. O Core Audio, ao
contrário, fica inteiro na thread de UI (callbacks só leem a estrutura recebida, sem chamadas COM).

## ADR-011 — Estrutura do widget a partir do vídeo; animação contínua opcional
**Status:** aceita

Adotada a estrutura observada (cabeçalho título + data/hora, corpo por painel, abas na base, altura que
segue o conteúdo, chips de fonte) com identidade visual própria — ver `docs/VIDEO-ANALYSIS.md`.
O equalizador animado do vídeo fica **desligado por padrão**: medido em ~0,18% de CPU contínuos contra
~0,01% com o indicador estático. Progresso de mídia a 1 Hz só com o ponteiro sobre a ilha.

## ADR-012 — Volume: global agora, por app depois
**Status:** aceita

Implementado o volume mestre do dispositivo padrão (`IAudioEndpointVolume`), como no vídeo, rotulado
como "volume geral, não o de um app". Volume por app (`IAudioSessionManager2`/`ISimpleAudioVolume`)
exige associar a sessão de mídia ao processo de áudio; para apps Win32 o AUMID é o nome do executável
("Spotify.exe"), mas navegadores usam IDs próprios e vários processos — a associação seria heurística.
Fica para quando houver um teste real com navegador, para não exibir um controle que atue no app errado.

## ADR-013 — Nome "Diary Ghost" e logo original
**Data:** 2026-11-06 · **Status:** aceita

O produto passa a se chamar **Diary Ghost**. Mudou tudo o que o usuário vê: títulos, bandeja, executável
(`DiaryGhost.exe`), pasta de dados (`%LOCALAPPDATA%\DiaryGhost`, com a pasta antiga `DesktopHub` movida
uma única vez — ou mantida, se estiver em uso), entrada de inicialização com o Windows e nome da
instância única. **Não** mudaram namespaces, projetos e a entropia do DPAPI: são internos, e renomeá-los
só geraria ruído no histórico (e, no caso do DPAPI, tornaria segredos já salvos ilegíveis).

O logo é um fantasminha **desenhado por código** (`scripts/generate-icon.ps1`), inspirado no estilo da
referência enviada pelo usuário, mas sem reutilizar a imagem — que tem cara de banco de imagens e
provavelmente tem licença restrita. Assim o logo é original e pode ser distribuído.

## ADR-014 — Modelo de tempo: UTC de parede, um único timer, reagendamento
**Data:** 2026-11-06 · **Status:** aceita

Cronômetro, temporizador e Pomodoro são estados imutáveis com `acumulado` + `rodando desde (UTC)`; o tempo
exibido é calculado na hora. Nenhum tique por segundo no serviço: um único `ITimer` do `TimeProvider`
dorme até o prazo mais próximo. Como timers do .NET usam um relógio que pode não avançar durante a
suspensão, o app reagenda em `PowerModeChanged.Resume` e em `TimeChanged`. UTC (e não o relógio
monotônico) porque precisa sobreviver a reinícios e contar o tempo em que o PC dormiu; o custo aceito é
que ajustar o relógio manualmente afeta a contagem.
Conclusões com mais de 5 s de atraso são "atrasadas": registradas com o horário real do prazo,
notificadas com esse horário, e o Pomodoro **não** inicia a próxima fase sozinho (o usuário não estava lá).

## ADR-015 — Toasts nativos para app sem pacote
**Status:** aceita

Escolhido `Windows.UI.Notifications` com um AUMID próprio (`DiaryGhost.App`) registrado por usuário em
`HKCU\Software\Classes\AppUserModelId` (nome e ícone) e `SetCurrentProcessExplicitAppUserModelID`.
Alternativa descartada: Windows App SDK (`AppNotificationManager`), que exigiria distribuir o runtime do
WinAppSDK (dezenas de MB) só para isso. Limitação conhecida: sem ativador COM, clicar num toast que ficou
na Central depois que o app fechou não reabre o app; com o app aberto, cliques e botões funcionam
(evento `Activated`). Verificado: o toast aparece na Central de Notificações com o botão "Repetir".

## ADR-016 — Central de notificações com política pura
**Status:** aceita

Todo módulo notifica via `NotificationCenter`. A decisão (`NotificationPolicy.Decide`) é uma função pura,
coberta por testes: categoria desligada → duplicata (10 min) → pausa manual (só Critical) → horário de
silêncio e tela cheia (só High/Critical) → limite por categoria (Low/Normal 5, High 20 por 10 min).
Tudo vai para o histórico (30 dias), inclusive o que foi suprimido, com o motivo — visível em
Configurações. Textos de notificação são escapados no XML do toast (testado contra injeção).

## ADR-017 — Recorrência: subconjunto próprio do RRULE, horário de parede + fuso
**Status:** aceita (resolve a pendência "Ical.Net vs. implementação mínima")

Implementamos o subconjunto do RFC 5545 que um calendário pessoal usa — FREQ diário/semanal/mensal/anual,
INTERVAL, BYDAY (com ordinal: "2ª terça", "última sexta"), BYMONTHDAY (negativos = do fim do mês),
BYMONTH, BYSETPOS ("último dia útil"), COUNT, UNTIL e exceções (EXDATE) — em ~300 linhas puras e testadas,
em vez de adicionar o Ical.Net (dependência grande, com fusos próprios). Regras não suportadas (por hora,
por minuto, WKST ≠ MO) são **recusadas com mensagem**, nunca interpretadas pela metade.

- Eventos guardam o início em **horário de parede** + ID de fuso: "toda segunda às 09:00" continua às 09:00
  depois de uma mudança de horário de verão (testado com um fuso de teste com DST).
- Conversão para UTC segue o RFC: horário inexistente (salto) usa o deslocamento de antes (02:30 → 03:30);
  horário ambíguo usa a primeira ocorrência.
- Datas inválidas são puladas (31 de abril, 29/02 em ano comum), como no RFC; "último dia do mês" é
  `BYMONTHDAY=-1`, oferecido na interface.
- Eventos de dia inteiro e tarefas sem hora são "flutuantes" (valem no fuso atual do usuário).
- O formato é o do iCalendar, então importação/sincronização (Fase 6) reaproveita o mesmo modelo; se
  aparecer uma regra fora do subconjunto, a decisão será revista ali.

## ADR-018 — Lembretes calculados, com registro e marca d'água
**Status:** aceita

Avisos não são materializados: são calculados a partir de eventos e tarefas (`ReminderScheduler.TriggersBetween`).
Cada aviso tem uma **chave estável** (origem + instante da ocorrência + antecedência). O registro
(`reminder_log`) guarda o que já foi entregue/adiado/dispensado, e a **marca d'água** guarda até quando
os avisos foram avaliados. Cada avaliação cobre (marca d'água, agora]:

- Nada se repete após reiniciar (chave já registrada) e nada se perde com o app fechado ou o PC suspenso:
  o que venceu é entregue ao voltar, como **atrasado** (com o horário original). Limite de 7 dias para trás;
  mais de 3 atrasados viram um resumo único.
- Aviso criado depois do seu horário (ex.: reunião em 5 min com aviso de 10) sai na hora, sem "atrasado";
  se o compromisso já começou, é ignorado.
- Um único timer dorme até o próximo aviso (máx. 6 h, por segurança) e a avaliação é refeita ao retomar
  da suspensão, quando o relógio/fuso muda e quando a agenda muda. Sem varredura periódica.
- Editar o horário de um evento gera chaves novas: o aviso do novo horário é entregue normalmente.

## ADR-019 — Adiar pelo próprio toast com lista de escolha
**Status:** aceita

Cinco botões ("5 min", "10 min", …, "Concluir") ficam ilegíveis no toast. Usamos o elemento `<input
type="selection">` do Windows (adiar por 5/10/30/60 min) + botões "Adiar" e "Concluir"; o valor escolhido
chega em `UserInput` e é repassado como `snooze:10`. O XML continua montado com escape (testado).

## ADR-020 — Notícias: só feeds oficiais curados, ativação explícita
**Status:** aceita

- Fontes curadas por critérios públicos (docs/NEWS-SOURCES.md): veículos com redação identificada e órgãos
  públicos/científicos, sempre pelo **feed RSS/Atom oficial**. Nada de raspagem, agregadores ou redes sociais.
  O usuário pode ocultar curadas e adicionar as suas (validadas antes de entrar).
- O módulo começa **desligado**: buscar notícias é acesso à rede e só acontece depois que o usuário ativa.
  As requisições se identificam como "DiaryGhost", sem dados do usuário.
- Conteúdo de feed é tratado como não confiável: XML sem DTD/entidades externas (sem XXE), limite de 5 MB e
  20 s por resposta, HTML convertido em texto (nada é renderizado), links só http(s), abertos no navegador.
- Coleta: um timer até a próxima fonte vencida; GET condicional (ETag/If-Modified-Since; 304 não baixa nada);
  recuo exponencial 15 min → 6 h em falhas, respeitando Retry-After; "Atualizar" manual não repete uma fonte
  consultada há menos de 2 min; até 100 itens por busca; retenção configurável (padrão 14 dias; salvos e
  favoritos ficam).

## ADR-021 — Deduplicação e agrupamento conservadores
**Status:** aceita

Repetido = mesma chave do item na fonte, mesma **URL canônica** (sem esquema, "www.", fragmento, barra final e
parâmetros de rastreamento) ou mesmo **título normalizado** (sem acentos/pontuação) nas últimas 48 h → não entra.
Mesmo acontecimento = fontes diferentes, 48 h, ≥ 3 palavras relevantes em comum e Jaccard ≥ 0,45 → mesmo grupo.
Limiar alto de propósito: juntar notícias diferentes é pior que mostrar duas separadas. Sem modelos de
linguagem nem serviços externos — tudo local e testado.

## ADR-022 — Clima: Open-Meteo com local escolhido pelo usuário
**Status:** aceita

Open-Meteo: gratuito, sem chave nem conta, previsão atual/horária/7 dias e busca de cidades. O usuário busca
a cidade pelo nome; **não** usamos localização automática (exigiria consentimento e uma API de localização do
Windows — fica para quando houver demanda). Sem local, nenhuma requisição. Atualiza a cada 30 min; a última
previsão fica guardada e é exibida offline com o horário da consulta e aviso de desatualizada (> 2 h).
**Alertas do INMET:** investigados; o portal não respondeu durante o desenvolvimento e não há API pública
documentada e estável para avisos — não implementado para não exibir alertas de procedência frágil.

## ADR-023 — Claude Code: o próprio executável como hook, canal local restrito ao usuário
**Status:** aceita (substitui o `hub-notify` separado previsto no ADR-008)

Em vez de um segundo executável, `DiaryGhost.exe --hook claude-code` trata o hook antes de inicializar o WPF
(`Program.Main`): lê o JSON da entrada padrão (até 64 KB, 1 s), reduz ao contrato v1 e envia por named pipe.
Sai sempre com código 0 — um hook nunca atrasa nem quebra o Claude Code (sem o app aberto, a mensagem é
descartada em ≤ 0,8 s).
- **Privacidade por construção:** o remetente descarta prompt, caminho da transcrição, entradas/saídas de
  ferramentas e o caminho da pasta (fica só o nome); o receptor valida de novo (versão, origem, tamanhos).
- **Segurança:** pipe com `PipeOptions.CurrentUserOnly` dos dois lados; nome derivado do SID (e da pasta de
  dados de teste). Limite de 60 mensagens/min.
- **settings.json:** só por clique, com confirmação mostrando o arquivo e o comando, backup antes de gravar,
  JSON inválido nunca é sobrescrito, só as entradas do Diary Ghost (marcadas pelo comando) são tocadas.

## ADR-024 — Agendas externas por assinatura iCal; OAuth adiado
**Status:** aceita

Google Agenda e Outlook publicam cada agenda num endereço iCal (privado). Assinar esse endereço dá leitura
completa sem login, sem senha e sem registrar um app — e cabe no princípio de "nenhuma credencial pedida".
O endereço é tratado como segredo (DPAPI, nunca em log nem na tela). Somente leitura: não há conflitos a
resolver; edições são feitas na origem.
OAuth (duas vias com Google Calendar API / Microsoft Graph) e Spotify Web API exigem um *client ID*
registrado pelo publicador do app em cada console; sem isso não é possível entregar nem testar de verdade.
Ficam para quando houver registro — `ISecretStore` (DPAPI) e o padrão de serviços com porta já os comportam.

## ADR-025 — Instalação por usuário feita pelo próprio executável
**Status:** aceita (resolve a pendência "Velopack vs. MSIX vs. Inno Setup")

`DiaryGhost.exe --install` / `--uninstall`, sem ferramenta de terceiros e sem administrador: pasta em
`%LOCALAPPDATA%\Programs`, atalho via `WScript.Shell`, entrada em `HKCU\...\Uninstall` (aparece em
"Aplicativos instalados", que chama `--uninstall`). Reinstalar atualiza no lugar e remove arquivos antigos.
A remoção da pasta só aceita o caminho de instalação conhecido; dados do usuário só saem com confirmação.
- **MSIX** daria atualizações e identidade de pacote, mas exige assinatura (sem certificado não instala).
- **Velopack/Squirrel** dão atualização automática, mas pressupõem um servidor de distribuição que não existe.
- **Inno Setup/WiX** acrescentam uma ferramenta de build sem ganho real para um app por usuário.
Atualização: manual (`--install` da versão nova). Revisitar quando houver onde publicar versões.

## ADR-026 — Reabrir o app por notificação: protocolo `diaryghost:` em vez de ativador COM
**Status:** aceita

Toasts de app sem pacote só reabrem um app fechado com um ativador COM registrado (CLSID + atalho com
`ToastActivatorCLSID`) — complexo e frágil sem MSIX. Alternativa documentada: `activationType="protocol"`.
O corpo do toast abre `diaryghost:<página>`; o Windows inicia o app (ou a segunda execução repassa a página à
instância aberta por memória compartilhada). **Links são entrada não confiável** (qualquer página web pode
tentar abrir um): só navegam para uma lista fixa de páginas, nunca executam ações. Botões (adiar, concluir)
continuam em primeiro plano e funcionam com o app aberto.

## ADR-027 — Fora do escopo desta versão: assinatura, auto-atualização, GPU no widget, i18n
**Status:** aceita

- **Assinatura de código:** exige certificado de uma autoridade (pago e vinculado a identidade). Sem ela, o
  SmartScreen avisa no primeiro uso e o Smart App Control pode bloquear executáveis baixados. Compilar
  localmente (como neste projeto) não é afetado da mesma forma.
- **Widget por GPU:** o custo atual medido (0,20% de CPU com o Foco expandido rodando) está dentro do
  orçamento; trocar a janela em camadas por composição DWM é uma reescrita do widget sem ganho percebido hoje.
- **i18n:** só pt-BR. Textos estão no código; extrair para recursos `.resx` é o caminho quando houver demanda.

## ADR-028 — Finanças em banco próprio (`finance.db`)
**Status:** aceita (2026-11-11; ver [docs/FINANCE.md](docs/FINANCE.md))

O módulo financeiro usa um arquivo SQLite separado, com o mesmo `Migrator` e uma lista própria de migrações.
Isso permite backup e exportação protegidos só do financeiro, cifragem futura só deste arquivo e
independência do módulo. Custo: dois bancos para abrir e verificar, mas nenhuma consulta precisa cruzar os dois.

## ADR-029 — Sem cifragem do banco no MVP; BitLocker e backups cifrados
**Status:** aceita (2026-11-11)

SQLCipher com chave DPAPI não protege contra o usuário logado nem contra malware na sessão; só cobre o que o
BitLocker já cobre. No MVP: ACL da pasta de dados, recomendação de BitLocker e backups `.dgfin` com
AES-256-GCM e chave derivada de senha (PBKDF2-SHA256). Na Fase 13, reavaliar a cifragem com senha própria e o
bloqueio por Windows Hello (barreira de interface, não criptografia).

## ADR-030 — Contabilização por partidas: competência, obrigação e caixa separadas
**Status:** aceita (2026-11-11)

Cada transação tem 1 ou 2 partidas, em contas ou cartões. Compra no cartão = despesa + dívida. Pagamento de
fatura = transferência da conta para o cartão, sem nova despesa. Saldos nunca são armazenados, só derivados.
Valores em `long` (centavos). Nas parcelas, o resto da divisão vai na 1ª. Os ciclos de cartão têm regras de
vigência, para que mudar o fechamento ou o vencimento não mova faturas passadas.

## ADR-031 — Uma moeda base, sem conversão
**Status:** aceita (2026-11-11)

Contas podem ter outra moeda, mas os totais consolidados usam só a moeda base; as demais aparecem separadas.
Converter exigiria cotações de um serviço externo, o que contraria o módulo sem rede.

## ADR-032 — Cartões: competência por parcela, ciclo mensal e carregamento só após o vencimento
**Data:** 2026-11-11 · **Status:** aceita

- **Parcelas** são lançamentos de despesa ligados à compra, com data = compra + (k−1) meses. Orçamento e relatórios
  contam o gasto no mês em que a parcela "acontece", que é como o usuário lembra dele; a fatura de cada parcela fica
  gravada na própria partida, então o calendário de faturas não precisa ser recalculado para o passado.
- **Pagamento de fatura** é uma transferência conta → cartão (não há tipo novo nem despesa nova): a mesma regra das
  transferências garante que compra + pagamento contam uma despesa só.
- **Ciclo**: cada mês tem exatamente um fechamento, pela regra vigente no dia 1 daquele mês. Trocar os dias cria uma
  regra a partir do mês seguinte ao da fatura aberta; faturas fechadas e a aberta não mudam, e só as partidas de
  faturas futuras são remanejadas.
- **Carregamento**: o restante de uma fatura passa para a seguinte só depois do vencimento (pagamento parcial ou
  atraso); crédito de pagamento a maior também. Antes disso, a fatura ainda vai ser paga e as futuras não a somam.
- **Juros e encargos** não são calculados (dependem do contrato); o usuário os lança quando aparecem na fatura.
- `fin_postings.card_id` foi criado na migração 1 sem chave estrangeira; como o SQLite não adiciona FK por ALTER,
  gatilhos garantem que o cartão existe, que partida de cartão tem fatura e que cartão com lançamentos não é apagado.

## ADR-033 — Planejamento: recorrências próprias, previstas até confirmar, e "Reservado" só com dinheiro real
**Data:** 2026-11-11 · **Status:** aceita

- **Recorrências financeiras não reusam o RRULE da Agenda** (diferente do previsto na proposta): no RFC 5545 "todo dia
  31" pula os meses sem dia 31, e conta de dia 31 precisa vencer no último dia do mês. O modelo é próprio e simples:
  semanal, mensal ou anual, a cada N períodos, dia limitado ao fim do mês, fim opcional.
- **Ocorrências são calculadas**, nunca gravadas como lançamentos. Só a confirmação cria o lançamento real, com
  `(recurring_id, occurrence_date)` único no banco. Atrasadas do mês atual entram no previsto (ainda vão sair);
  atrasadas de meses anteriores não (provavelmente pagas sem confirmar) — aparecem como "atrasada" para o usuário decidir.
- **Assinatura no cartão** entra no previsto pelo vencimento da fatura em que cairia; confirmar cria uma compra à vista.
- **Orçamento** usa a mesma competência dos relatórios (parcela na data da parcela, ADR-032), não "o mês da fatura"
  como dizia a proposta.
- **Reservado** soma só contribuições reais de metas ativas cujo dinheiro está em contas do disponível. Meta com conta
  fora do disponível (ex.: investimento) não reserva de novo. Planejado nunca entra; arquivar a meta libera a reserva.
- **Projeções** medem o "disponível livre" (contas do disponível − faturas − reservado − contribuições planejadas),
  calculado por eventos (sem recalcular saldos dia a dia). "Quanto posso reservar" usa o menor ponto do período, porque
  reservar hoje baixa todos os dias seguintes.

## Decisões pendentes
- Nenhuma no momento.
