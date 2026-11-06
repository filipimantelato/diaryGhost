<#
.SYNOPSIS
  Mede CPU e memória do Desktop Hub em três modos: só bandeja, widget visível e janela principal aberta.
  Usa uma pasta de dados isolada (não toca nos seus dados) e encerra cada execução com --quit.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure.ps1 -Exe src\DesktopHub.App\bin\Debug\net10.0-windows\DesktopHub.exe
#>
param(
  [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\win-x64\DesktopHub.exe'),
  [int]$WarmupSeconds = 8,
  [int]$SampleSeconds = 30
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "Executavel nao encontrado: $Exe (rode scripts/publish.ps1 antes)" }
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' } # so para builds de Debug
$data = Join-Path $env:TEMP ('DesktopHubBench-' + [guid]::NewGuid().ToString('N'))
$cores = [Environment]::ProcessorCount

$modes = [ordered]@{
  'Somente bandeja'       = '--minimized --no-widget'
  'Widget visivel'        = '--minimized'
  'Janela principal'      = '--show-main'
}
$results = foreach ($mode in $modes.Keys) {
  $p = Start-Process $Exe -ArgumentList "--data-dir `"$data`" $($modes[$mode])" -PassThru
  Start-Sleep $WarmupSeconds
  $p.Refresh(); $cpu0 = $p.TotalProcessorTime
  Start-Sleep $SampleSeconds
  $p.Refresh(); $cpu1 = $p.TotalProcessorTime
  [pscustomobject]@{
    Modo            = $mode
    'CPU media %'   = [math]::Round(($cpu1 - $cpu0).TotalMilliseconds / ($SampleSeconds * 1000 * $cores) * 100, 3)
    'CPU ms/30s'    = [math]::Round(($cpu1 - $cpu0).TotalMilliseconds, 1)
    'Working set MB'= [math]::Round($p.WorkingSet64 / 1MB, 1)
    'Privada MB'    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    Threads         = $p.Threads.Count
  }
  Start-Process $Exe -ArgumentList "--data-dir `"$data`" --quit" -Wait
  if (-not $p.WaitForExit(10000)) { $p.Kill(); Write-Warning "$mode nao encerrou com --quit" }
}
$results | Format-Table -AutoSize
$log = Get-ChildItem (Join-Path $data 'logs\*.log') | Select-Object -First 1
if ($log) {
  Get-Content $log.FullName -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.ElapsedMs } | ForEach-Object { "Inicializacao: $($_.ElapsedMs) ms" }
}
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
