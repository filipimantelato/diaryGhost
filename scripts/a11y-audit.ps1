<#
.SYNOPSIS
  Auditoria de acessibilidade: abre o Diary Ghost com dados isolados, visita cada página e lista controles
  interativos sem nome acessível (o que o Narrador leria como "botão" sem dizer qual).
  Também confere se cada controle interativo pode receber foco pelo teclado.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/a11y-audit.ps1
#>
param(
  [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DesktopHub.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\DiaryGhost.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
$data = Join-Path $env:TEMP ('DiaryGhostA11y-' + [guid]::NewGuid().ToString('N'))
$p = Start-Process $Exe -ArgumentList "--data-dir `"$data`" --show-main --no-widget" -PassThru
Start-Sleep 5

$ae = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]
$ct = [System.Windows.Automation.ControlType]
$interactive = @($ct::Button, $ct::CheckBox, $ct::ComboBox, $ct::Edit, $ct::RadioButton, $ct::Slider, $ct::Hyperlink, $ct::ListItem)

function Window {
  $cond = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $p.Id)
  foreach ($w in $ae::RootElement.FindAll($tree::Children, $cond)) { if ($w.Current.Name -eq 'Diary Ghost') { return $w } }
  throw 'Janela principal não encontrada'
}

$pages = 'Dashboard', 'Notícias', 'Calendário', 'Tarefas', 'Lembretes', 'Mídia', 'Produtividade', 'Atividades e integrações', 'Configurações'
$problems = 0
$checked = 0
foreach ($page in $pages) {
  $nav = (Window).FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $page)))
  $sel = $null
  if ($nav -and $nav.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) { $sel.Select() }
  Start-Sleep -Milliseconds 900
  $all = (Window).FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $missing = @()
  foreach ($el in $all) {
    $c = $el.Current
    if ($interactive -notcontains $c.ControlType -or $c.IsOffscreen) { continue }
    $checked++
    if ([string]::IsNullOrWhiteSpace($c.Name)) {
      $missing += "  - $($c.ControlType.ProgrammaticName -replace 'ControlType\.','') (AutomationId '$($c.AutomationId)', classe $($c.ClassName))"
    }
    # Partes internas de um controle deslizante (o próprio Slider recebe o foco e responde às setas).
    elseif (-not $c.IsKeyboardFocusable -and $c.IsEnabled -and $c.ControlType -ne $ct::ListItem -and $c.ClassName -notin @('RepeatButton', 'Thumb')) {
      $missing += "  - sem foco por teclado: $($c.ControlType.ProgrammaticName -replace 'ControlType\.','') '$($c.Name)'"
    }
  }
  if ($missing.Count -gt 0) {
    Write-Host "$page — $($missing.Count) problema(s):"
    $missing | Select-Object -Unique | ForEach-Object { Write-Host $_ }
    $problems += $missing.Count
  } else {
    Write-Host "$page — ok"
  }
}
Stop-Process -Id $p.Id
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("{0} controles verificados, {1} problema(s)." -f $checked, $problems)
if ($problems -gt 0) { exit 1 }
