<#
.SYNOPSIS
  Gera assets/app.ico (16–256 px) desenhando o ícone por código: um quadrado escuro arredondado
  com uma "ilha" (pílula) na cor de destaque. Rodar novamente substitui o arquivo.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-icon.ps1
#>
param(
  [string]$OutFile = (Join-Path $PSScriptRoot '..\assets\app.ico'),
  [string]$Accent = '#8B5CF6'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [single]($r * 2)
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$accentColor = [System.Drawing.ColorTranslator]::FromHtml($Accent)
$frames = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)

  $bg = New-RoundedPath 0 0 ($s - 0.5) ($s - 0.5) ([single]($s * 0.22))
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#1B1E25'))), $bg)

  # Ilha: pílula horizontal no terço superior + ponto de atividade
  $iw = [single]($s * 0.70); $ih = [single]([Math]::Max(3, $s * 0.26))
  $ix = [single](($s - $iw) / 2); $iy = [single]($s * 0.30)
  $pill = New-RoundedPath $ix $iy $iw $ih ([single]($ih / 2))
  $g.FillPath((New-Object System.Drawing.SolidBrush $accentColor), $pill)

  if ($s -ge 24) {
    $dot = [single]($ih * 0.42)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F1F3F7'))),
      [single]($ix + $iw - $ih * 0.5 - $dot / 2), [single]($iy + ($ih - $dot) / 2), $dot, $dot)
    # Linhas de "conteúdo" abaixo da ilha
    $lh = [single]([Math]::Max(1, $s * 0.07))
    $lineBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#9BA3B2'))
    $g.FillPath($lineBrush, (New-RoundedPath $ix ([single]($s * 0.66)) ([single]($iw * 0.8)) $lh ([single]($lh / 2))))
    $g.FillPath($lineBrush, (New-RoundedPath $ix ([single]($s * 0.78)) ([single]($iw * 0.5)) $lh ([single]($lh / 2))))
  }
  $g.Dispose()

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
  $bmp.Dispose()
}

# Formato ICO com quadros PNG (suportado desde o Windows Vista)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
  $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes((Resolve-Path $dir).Path + '\' + (Split-Path -Leaf $OutFile), $out.ToArray())
Write-Host "Ícone gerado: $OutFile ($($out.Length) bytes)"
