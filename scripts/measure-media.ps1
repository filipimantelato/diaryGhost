<#
.SYNOPSIS
  Mede CPU e memória do widget com mídia TOCANDO (rode com Spotify/navegador reproduzindo algo).
  Estados medidos via UI Automation: compacto (indicador estatico, padrao), expandido no painel Mídia
  (progresso 1 Hz), expandido no Início e widget oculto (só bandeja). Pasta de dados isolada.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-media.ps1
#>
param(
  [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\win-x64\DiaryGhost.exe'),
  [int]$SampleSeconds = 30
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "Executavel nao encontrado: $Exe (rode scripts/publish.ps1 antes)" }
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$data = Join-Path $env:TEMP ('DesktopHubBenchMedia-' + [guid]::NewGuid().ToString('N'))
$cores = [Environment]::ProcessorCount

$p = Start-Process $Exe -ArgumentList "--data-dir `"$data`" --minimized" -PassThru
Start-Sleep 8
$widget = $A::RootElement.FindAll('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id))) |
  Where-Object { $_.Current.Name -like '*Widget*' }
function ById($id) { $widget.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id))) }
function WaitFor($id) {
  foreach ($i in 1..20) { $e = ById $id; if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "Elemento '$id' nao apareceu (widget oculto ou em outro estado?)"
}
function StateOf { if (-not $widget) { return '-' }; if (ById 'CompactText') { 'compacto' } elseif (ById 'MediaTitle') { 'midia' } elseif (ById 'HomeMediaTitle') { 'inicio' } else { 'oculto/outro' } }
function Sample($name) {
  Start-Sleep 3
  $before = StateOf
  $p.Refresh(); $c0 = $p.TotalProcessorTime
  Start-Sleep $SampleSeconds
  $p.Refresh(); $c1 = $p.TotalProcessorTime
  [pscustomobject]@{
    Estado          = $name
    'Confere'       = if ($widget) { "$before -> $(StateOf)" } else { '-' }
    'CPU media %'   = [math]::Round(($c1 - $c0).TotalMilliseconds / ($SampleSeconds * 1000 * $cores) * 100, 3)
    'CPU ms/30s'    = [math]::Round(($c1 - $c0).TotalMilliseconds, 1)
    'Privada MB'    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
  }
}
# Garante o estado pedido mesmo se alguém interagir com o widget durante a medição.
function Expand-To($tab, $marker) {
  if (ById 'CompactText') { (WaitFor 'Island').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  (WaitFor $tab).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $null = WaitFor $marker
}
$title = (WaitFor 'CompactText').Current.Name
$results = @()
$results += Sample 'Compacto (indicador estatico, padrao)'
Expand-To 'TabMedia' 'MediaTitle'
$results += Sample 'Expandido - Midia (progresso)'
Expand-To 'TabHome' 'HomeMediaTitle'
$results += Sample 'Expandido - Inicio'
$widget = $null
Start-Process $Exe -ArgumentList "--data-dir `"$data`" --quit" -Wait
$p.WaitForExit(10000) | Out-Null
$p2 = Start-Process $Exe -ArgumentList "--data-dir `"$data`" --minimized --no-widget" -PassThru
Start-Sleep 8
$p = $p2
$results += Sample 'Somente bandeja (servicos de midia ativos)'
Start-Process $Exe -ArgumentList "--data-dir `"$data`" --quit" -Wait
$p.WaitForExit(10000) | Out-Null

"Tocando durante a medicao: $title"
$results | Format-Table -AutoSize
Get-ChildItem (Join-Path $data 'logs\hub-*.log') | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 } |
  ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.message -like 'Widget:*' -or $_.level -in 'Warning','Error','Critical' } |
  ForEach-Object { "log: $($_.level) $($_.message)" }
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
