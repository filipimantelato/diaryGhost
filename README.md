<img src="assets/logo.png" width="96" alt="Logo do Diary Ghost: um fantasminha branco" />

# Diary Ghost

Central pessoal residente para Windows 11: widget flutuante estilo "ilha", bandeja do sistema e
aplicação completa — leve, local-first e sem telemetria.

> **Estado:** Fases 1 (fundação), 2 (widget e mídia) e 3 (produtividade e notificações) concluídas.
> Agenda, notícias, clima e integrações chegam nas próximas fases — ver [ROADMAP.md](ROADMAP.md).
> Módulos ainda não implementados dizem isso explicitamente na interface.

## O que já funciona
- **Widget flutuante** (estrutura baseada no [vídeo de referência](docs/VIDEO-ANALYSIS.md)): compacto
  com o timer em andamento ou a mídia atual; expandido com abas Início, Mídia, Foco, Volume (e Agenda/Atividade indicando a fase);
  clique expande, arrastar move, clique direito abre o menu; posição memorizada e validada entre
  monitores; sempre visível opcional, bloqueio, opacidade, não rouba foco, some em tela cheia, pode ser
  excluído de capturas de tela.
- **Mídia**: controla Spotify, navegadores e outros players que expõem os controles de mídia do Windows,
  sem login. Você escolhe a sessão (chips); comandos nunca vão para outra. Volume geral do Windows.
- **Produtividade**: Pomodoro (durações e ciclos configuráveis, início automático opcional), temporizador
  (presets de 5 a 60 min ou minutos livres) e cronômetro com voltas; estatísticas e histórico. Tudo
  continua com a janela fechada e é retomado após reiniciar ou suspender — prazos vencidos com o app
  fechado são avisados como "atrasados", com o horário real.
- **Notificações nativas do Windows** com botões (Repetir, Iniciar), pausa manual, horário de silêncio,
  sem interromper jogos em tela cheia, deduplicação, limite de frequência e histórico.
- **Bandeja**: abrir, mostrar/ocultar widget, sempre visível, mídia, Pomodoro, cronômetro, pausar notificações, configurações, sair.
- **Janela principal** com navegação lateral, dashboard e configurações funcionais.
- **Atalhos globais**: `Ctrl+Alt+H` (widget), `Ctrl+Alt+D` (abrir app).
- Fechar a janela mantém o app na bandeja (configurável); iniciar com o Windows (opcional).
- Dados em `%LOCALAPPDATA%\DiaryGhost`: SQLite com migrações, backup e recuperação de corrupção; logs JSON.

## Requisitos
- Windows 10 2004+ ou Windows 11 (x64).
- Para **usar** o build publicado: nada (self-contained).
- Para **desenvolver**: .NET SDK 10.

```bash
winget install Microsoft.DotNet.SDK.10
```

## Desenvolvimento
```bash
dotnet build DesktopHub.slnx
```
```bash
dotnet test DesktopHub.slnx
```
```bash
dotnet run --project src/DesktopHub.App -- --show-main
```

Se o SDK estiver instalado só no seu perfil (sem admin), o executável de Debug precisa saber onde está
o runtime:
```bash
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
```

> **Smart App Control:** com ele ativo, o Windows pode bloquear DLLs de teste recém-compiladas em Debug
> ("Uma política de Controle de Aplicativo bloqueou este arquivo"). Rode os testes em Release
> (`dotnet test DesktopHub.slnx -c Release`), que passaram normalmente. Não desligue a proteção por isso.

### Argumentos
| Argumento | Efeito |
|---|---|
| `--show-main` | abre a janela principal |
| `--minimized` | inicia só com bandeja/widget (usado pela inicialização com o Windows) |
| `--no-widget` | não mostra o widget nesta execução |
| `--quit` | encerra graciosamente a instância em execução |
| `--data-dir <pasta>` | usa outra pasta de dados (testes, benchmarks); pode rodar ao lado da instância normal |

### Como testar a troca de sessões (Spotify + Brave)
1. Toque uma música no Spotify e um vídeo numa aba do Brave.
2. Expanda o widget → aba Mídia: devem aparecer os chips **Auto**, **Spotify** e **Brave**.
3. Clique em **Spotify** e pause: só o Spotify pausa. Clique em **Brave** e pause: só o vídeo pausa.
4. Feche a aba do Brave com o chip Brave selecionado: o widget mostra outra sessão e nenhum comando é
   enviado ao Spotify por engano.

## Build de distribuição e benchmark
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1
```
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-media.ps1
```
Saída em `artifacts/publish/win-x64/DiaryGhost.exe`. O instalador vem na Fase 7.

## Documentação
- [ARCHITECTURE.md](ARCHITECTURE.md) — arquitetura, fluxo de dados, modelo de dados, integrações e limitações
- [DECISIONS.md](DECISIONS.md) — decisões (stack, persistência, widget…)
- [ROADMAP.md](ROADMAP.md) — fases, dependências e critérios de aceitação
- [docs/DESIGN.md](docs/DESIGN.md) — design system e estados do widget
- [docs/benchmarks/](docs/benchmarks/) — medições reais
- [CHANGELOG.md](CHANGELOG.md)

## Privacidade
Sem telemetria. Nenhuma integração externa ativa na Fase 1. Tokens futuros serão cifrados com DPAPI.
