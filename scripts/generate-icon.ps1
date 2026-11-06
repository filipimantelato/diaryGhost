<#
.SYNOPSIS
  Gera o logo do Diary Ghost desenhado por código (arte original, sem imagens de terceiros):
  um fantasminha branco arredondado, com olhos ovais, boca em "o", bracinhos e barra ondulada,
  sobre fundo cinza com um brilho amarelo suave.
  Saídas: assets/app.ico (16–256 px) e assets/logo.png (256 px, fundo transparente, para a UI).
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-icon.ps1
#>
param(
  [string]$AssetsDir = (Join-Path $PSScriptRoot '..\assets'),
  [string]$Accent = '#C9A94E'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$C = { param($hex, $a = 255) [System.Drawing.Color]::FromArgb($a, [System.Drawing.ColorTranslator]::FromHtml($hex)) }

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [single]($r * 2)
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); return $p
}

# Silhueta do fantasma num quadro de 1024 × 1024.
function New-GhostPath {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.StartFigure()
  $p.AddArc(292, 160, 440, 480, 180, 180)                                  # cabeça (cúpula)
  $p.AddBezier(732, 400, 734, 460, 740, 492, 748, 516)                     # lado direito
  $p.AddBezier(748, 516, 812, 470, 884, 474, 886, 528)                     # braço direito, inclinado para cima
  $p.AddBezier(886, 528, 888, 584, 822, 604, 774, 612)                     # braço direito (volta)
  $p.AddBezier(774, 612, 796, 680, 822, 730, 846, 770)                     # saia abrindo para fora
  # barra ondulada (da direita para a esquerda), 5 ondas
  $p.AddBezier(846, 770, 826, 836, 744, 838, 716, 790)
  $p.AddBezier(716, 790, 690, 840, 610, 842, 586, 792)
  $p.AddBezier(586, 792, 560, 844, 464, 844, 438, 792)
  $p.AddBezier(438, 792, 414, 842, 334, 840, 308, 790)
  $p.AddBezier(308, 790, 280, 838, 198, 836, 178, 770)
  $p.AddBezier(178, 770, 202, 730, 228, 680, 250, 612)                     # saia, lado esquerdo
  $p.AddBezier(250, 612, 202, 604, 136, 584, 138, 528)                     # braço esquerdo (sai)
  $p.AddBezier(138, 528, 140, 474, 212, 470, 276, 516)                     # braço esquerdo (volta)
  $p.AddBezier(276, 516, 284, 492, 290, 460, 292, 400)
  $p.CloseFigure()
  return $p
}

function Render-Master([bool]$withBackground) {
  $bmp = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.CompositingQuality = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)

  if ($withBackground) {
    $bg = New-RoundedRect 0 0 1023 1023 230
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, 1024), (& $C '#262626'), (& $C '#161616'))
    $g.FillPath($bgBrush, $bg)
    # brilho amarelo suave atrás do fantasma
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $glowPath.AddEllipse(150, 330, 724, 640)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = (& $C $Accent 70); $glow.SurroundColors = @((& $C $Accent 0))
    $g.FillPath($glow, $glowPath)
  }

  # sombra no "chão"
  $shadowPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $shadowPath.AddEllipse(300, 850, 424, 70)
  $shadow = New-Object System.Drawing.Drawing2D.PathGradientBrush($shadowPath)
  $shadow.CenterColor = (& $C '#000000' 110); $shadow.SurroundColors = @((& $C '#000000' 0))
  $g.FillPath($shadow, $shadowPath)

  # corpo: branco no topo, levemente quente (amarelado) na barra
  $ghost = New-GhostPath
  $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 150), (New-Object System.Drawing.Point 0, 850), (& $C '#FFFFFF'), (& $C '#E9E2CF'))
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
  $blend.Colors = @((& $C '#FFFFFF'), (& $C '#F6F4F0'), (& $C '#E6DBBE')); $blend.Positions = @([single]0, [single]0.55, [single]1)
  $body.InterpolationColors = $blend
  $g.FillPath($body, $ghost)

  # volume: sombreamento lateral e brilho no topo, recortados na silhueta
  $g.SetClip($ghost)
  $sidePath = New-Object System.Drawing.Drawing2D.GraphicsPath; $sidePath.AddEllipse(560, 260, 420, 640)
  $side = New-Object System.Drawing.Drawing2D.PathGradientBrush($sidePath)
  $side.CenterColor = (& $C '#B9B2A0' 70); $side.SurroundColors = @((& $C '#B9B2A0' 0))
  $g.FillPath($side, $sidePath)
  $hiPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $hiPath.AddEllipse(340, 180, 250, 200)
  $hi = New-Object System.Drawing.Drawing2D.PathGradientBrush($hiPath)
  $hi.CenterColor = (& $C '#FFFFFF' 200); $hi.SurroundColors = @((& $C '#FFFFFF' 0))
  $g.FillPath($hi, $hiPath)
  $rimPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $rimPath.AddEllipse(200, 700, 624, 200)
  $rim = New-Object System.Drawing.Drawing2D.PathGradientBrush($rimPath)
  $rim.CenterColor = (& $C $Accent 60); $rim.SurroundColors = @((& $C $Accent 0))
  $g.FillPath($rim, $rimPath)
  $g.ResetClip()

  # olhos e boca (com leve reflexo)
  $dark = New-Object System.Drawing.SolidBrush (& $C '#1B1B1F')
  $g.FillEllipse($dark, 400, 380, 76, 104)
  $g.FillEllipse($dark, 548, 380, 76, 104)
  $g.FillEllipse($dark, 480, 512, 64, 88)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush (& $C '#5A4A4A')), 494, 560, 36, 30)   # interior da boca
  $shine = New-Object System.Drawing.SolidBrush (& $C '#FFFFFF' 150)
  $g.FillEllipse($shine, 420, 396, 18, 22); $g.FillEllipse($shine, 568, 396, 18, 22)

  $g.Dispose()
  return $bmp
}

function Resize([System.Drawing.Bitmap]$src, [int]$size) {
  $dst = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($dst)
  $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.DrawImage($src, 0, 0, $size, $size); $g.Dispose()
  return $dst
}

New-Item -ItemType Directory -Force $AssetsDir | Out-Null
$assets = (Resolve-Path $AssetsDir).Path

$icon = Render-Master $true
$logo = Render-Master $false
(Resize $logo 256).Save((Join-Path $assets 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(Resize $icon 512).Save((Join-Path $assets 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO com quadros PNG (suportado desde o Windows Vista)
$frames = foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
  $ms = New-Object System.IO.MemoryStream
  (Resize $icon $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  , @{ Size = $s; Bytes = $ms.ToArray() }
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
  $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())
Write-Host "Gerados: app.ico, logo.png, icon-preview.png em $assets"
