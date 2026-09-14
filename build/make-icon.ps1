<#
.SYNOPSIS
    Regenerates src\NetTrace.App\Assets\NetTrace.ico from code.

.DESCRIPTION
    The icon carries the same colour pairing as the rest of the UI: a cool arrow for bytes coming
    down, a warm arrow for bytes going up, on the app's dark ground. Drawn as vectors at every
    size rather than scaled from one bitmap, so the 16 px taskbar and title-bar versions get
    strokes snapped to whole pixels instead of a grey smear.

    Small entries are written as 32-bit DIBs and the 256 px entry as PNG, which is the layout
    both the Win32 resource loader and WPF's icon decoder handle without surprises.

.PARAMETER PreviewPath
    Optional folder to also write each size out as a PNG, for eyeballing.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\NetTrace.App\Assets\NetTrace.ico'),
    [string] $PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Tokens.xaml
$ground  = [System.Drawing.ColorTranslator]::FromHtml('#10141A')
$raised  = [System.Drawing.ColorTranslator]::FromHtml('#232A35')
$line    = [System.Drawing.ColorTranslator]::FromHtml('#2A323E')
$down    = [System.Drawing.ColorTranslator]::FromHtml('#4FB6CE')
$up      = [System.Drawing.ColorTranslator]::FromHtml('#E3A857')

$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-RoundedRect([single] $x, [single] $y, [single] $w, [single] $h, [single] $r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Arrow([single] $cx, [single] $tail, [single] $tip, [single] $headHalf, [single] $headLen) {
    # One open figure: shaft plus chevron, so the round joins meet cleanly at the tip.
    $dir = [math]::Sign($tip - $tail)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddLine($cx, $tail, $cx, $tip)
    $path.StartFigure()
    $path.AddLines([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx - $headHalf, $tip - $dir * $headLen),
        [System.Drawing.PointF]::new($cx, $tip),
        [System.Drawing.PointF]::new($cx + $headHalf, $tip - $dir * $headLen)))
    $path
}

function Render-Icon([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $small = $size -le 24

    # Tile. Small sizes use the full square so the glyph has every pixel it can get.
    $inset  = if ($small) { 0 } else { [math]::Round(10 * $s) }
    $radius = [math]::Max(2.5, 52 * $s)
    $tile = New-RoundedRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) $radius
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, $size), $raised, $ground)
    $g.FillPath($fill, $tile)
    if ($size -ge 32) {
        $edge = New-Object System.Drawing.Pen($line, [single][math]::Max(1, 4 * $s))
        $g.DrawPath($edge, $tile)
        $edge.Dispose()
    }

    # Arrows. Up to 32 px the geometry is hand-placed: whole-pixel stroke widths, even strokes
    # centred on pixel edges and odd ones on pixel centres, so shafts render as solid columns.
    $grid = @{
        16 = @{ Stroke = 2; Down = 5;    Up = 11;   Top = 3;   Bottom = 13;   Head = 2.5 }
        20 = @{ Stroke = 2; Down = 7;    Up = 13;   Top = 4;   Bottom = 16;   Head = 3 }
        24 = @{ Stroke = 3; Down = 8.5;  Up = 15.5; Top = 5.5; Bottom = 18.5; Head = 3.5 }
        32 = @{ Stroke = 3; Down = 11.5; Up = 20.5; Top = 8.5; Bottom = 23.5; Head = 4.5 }
    }
    if ($grid.ContainsKey($size)) {
        $p = $grid[$size]
        $stroke = $p.Stroke; $cxDown = $p.Down; $cxUp = $p.Up
        $top = $p.Top; $bottom = $p.Bottom; $headHalf = $p.Head; $headLen = $p.Head
    } else {
        $stroke = 24 * $s
        $cxDown = 94 * $s; $cxUp = 162 * $s
        $top = 62 * $s; $bottom = 194 * $s
        $headHalf = 36 * $s; $headLen = 36 * $s
    }

    foreach ($spec in @(
        @{ Color = $down; Path = (New-Arrow $cxDown $top $bottom $headHalf $headLen) },
        @{ Color = $up;   Path = (New-Arrow $cxUp $bottom $top $headHalf $headLen) })) {
        $pen = New-Object System.Drawing.Pen($spec.Color, [single]$stroke)
        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($pen, $spec.Path)
        $pen.Dispose(); $spec.Path.Dispose()
    }

    $fill.Dispose(); $tile.Dispose(); $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap] $bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER; height is doubled to cover the AND mask that follows the colour rows.
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int]($n * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $maskStride = [int]([math]::Ceiling($n / 32.0) * 4)
    $w.Write([int]($n * $n * 4 + $maskStride * $n))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    # Alpha carries transparency; an all-zero AND mask keeps every pixel.
    $w.Write((New-Object byte[] ($maskStride * $n)))
    $w.Flush()
    # The comma stops PowerShell unrolling the array into the pipeline as loose objects.
    , $ms.ToArray()
}

$entries = foreach ($size in $sizes) {
    $bmp = Render-Icon $size
    if ($PreviewPath) {
        New-Item -ItemType Directory -Force -Path $PreviewPath | Out-Null
        $bmp.Save((Join-Path $PreviewPath "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes }
}

New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath) | Out-Null
$fs = [System.IO.File]::Create($OutputPath)
$w = New-Object System.IO.BinaryWriter $fs
try {
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]$e.Bytes.Length); $w.Write([int]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $w.Write([byte[]]$e.Bytes) }
} finally {
    $w.Dispose()
}

Write-Host "Wrote $((Resolve-Path $OutputPath).Path) ($($sizes -join ', ') px)" -ForegroundColor Green
