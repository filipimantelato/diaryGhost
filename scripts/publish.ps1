<#
.SYNOPSIS
  Build reproduzível de Release: executável self-contained (não exige .NET instalado) com ReadyToRun.
  Saída: artifacts/publish/win-x64/DiaryGhost.exe
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
#>
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Procura um dotnet que tenha o SDK 10 (o do PATH pode ser só o runtime).
$candidates = @(
  (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
  (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\dotnet\dotnet.exe'),
  (Join-Path $env:USERPROFILE 'AppData\Local\Microsoft\dotnet\dotnet.exe'),
  (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
  (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) -match '^10\.' } | Select-Object -First 1
if (-not $dotnet) { throw ".NET SDK 10 nao encontrado (procurado em: $($candidates -join ', ')). Instale com: winget install Microsoft.DotNet.SDK.10" }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
Write-Host "Usando $dotnet"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$out = Join-Path $root "artifacts\publish\$Runtime"
& $dotnet test (Join-Path $root 'DesktopHub.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam; publicacao cancelada.' }
& $dotnet publish (Join-Path $root 'src\DesktopHub.App\DesktopHub.App.csproj') -c Release -r $Runtime --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'Falha na publicacao.' }
$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Publicado em {0} ({1:N1} MB)" -f $out, $size)
