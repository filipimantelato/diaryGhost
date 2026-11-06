# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/); versões seguem SemVer.

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
