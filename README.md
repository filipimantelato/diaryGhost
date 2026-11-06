# Personal Desktop Hub

Central pessoal residente para Windows 11: widget flutuante estilo "ilha", bandeja do sistema e
aplicação completa — leve, local-first e sem telemetria.

> **Estado:** Fase 1 (fundação) concluída. Mídia, produtividade, agenda, notícias, clima e integrações
> chegam nas próximas fases — ver [ROADMAP.md](ROADMAP.md). Módulos ainda não implementados dizem isso
> explicitamente na interface.

## O que já funciona
- **Widget flutuante**: compacto ⇄ expandido (duplo clique), arrastável, posição memorizada e validada
  entre monitores e mudanças de resolução, sempre visível opcional, bloqueio, opacidade, não rouba foco,
  some automaticamente durante jogos/apps em tela cheia, pode ser excluído de capturas de tela.
- **Bandeja**: abrir, mostrar/ocultar widget, sempre visível, configurações, sair.
- **Janela principal** com navegação lateral, dashboard e configurações funcionais.
- **Atalhos globais**: `Ctrl+Alt+H` (widget), `Ctrl+Alt+D` (abrir app).
- Fechar a janela mantém o app na bandeja (configurável); iniciar com o Windows (opcional).
- Dados em `%LOCALAPPDATA%\DesktopHub`: SQLite com migrações, backup e recuperação de corrupção; logs JSON.

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

### Argumentos
| Argumento | Efeito |
|---|---|
| `--show-main` | abre a janela principal |
| `--minimized` | inicia só com bandeja/widget (usado pela inicialização com o Windows) |
| `--no-widget` | não mostra o widget nesta execução |
| `--quit` | encerra graciosamente a instância em execução |
| `--data-dir <pasta>` | usa outra pasta de dados (testes, benchmarks); pode rodar ao lado da instância normal |

## Build de distribuição e benchmark
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1
```
Saída em `artifacts/publish/win-x64/DesktopHub.exe`. O instalador vem na Fase 7.

## Documentação
- [ARCHITECTURE.md](ARCHITECTURE.md) — arquitetura, fluxo de dados, modelo de dados, integrações e limitações
- [DECISIONS.md](DECISIONS.md) — decisões (stack, persistência, widget…)
- [ROADMAP.md](ROADMAP.md) — fases, dependências e critérios de aceitação
- [docs/DESIGN.md](docs/DESIGN.md) — design system e estados do widget
- [docs/benchmarks/](docs/benchmarks/) — medições reais
- [CHANGELOG.md](CHANGELOG.md)

## Privacidade
Sem telemetria. Nenhuma integração externa ativa na Fase 1. Tokens futuros serão cifrados com DPAPI.
