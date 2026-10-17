# Draws the LidAwake coffee-cup icon and writes a multi-size .ico file.
# Small sizes are stored as 32-bit DIBs (safest for System.Drawing.Icon), 256 px as PNG.
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\LidAwake.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = [System.Drawing.Color]::FromArgb(255, 217, 119, 87)   # warm coral
    $white = [System.Drawing.Color]::White

    if ($s -le 20) {
        # pixel-snapped design on a 16x16 grid so the tray icon stays crisp
        $g.ScaleTransform($s / 16.0, $s / 16.0)
        $g.FillPath((New-Object System.Drawing.SolidBrush $bg), (New-RoundRect 0.5 0.5 15 15 3.5))
        $g.FillPath((New-Object System.Drawing.SolidBrush $white), (New-RoundRect 3 6 7 7 1.5))
        $g.DrawArc((New-Object System.Drawing.Pen $white, 1.7), 8.0, 7.2, 4.6, 4.0, -90, 180)
        $steam = New-Object System.Drawing.Pen $white, 1.2
        $g.DrawLine($steam, 5.0, 2.2, 5.0, 4.2)
        $g.DrawLine($steam, 8.0, 2.2, 8.0, 4.2)
        $g.Dispose()
        return $bmp
    }

    $g.ScaleTransform($s / 32.0, $s / 32.0)   # draw on a 32x32 grid
    $bgPath = New-RoundRect 1 1 30 30 7
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $bgPath)

    # mug body
    $mug = New-RoundRect 7 13 13.5 13 3
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $mug)

    # handle
    $pen = New-Object System.Drawing.Pen $white, 2.6
    $g.DrawArc($pen, 16.5, 15.0, 8.0, 7.5, -90, 180)

    # steam (only where it stays readable)
    if ($s -ge 24) {
        $steam = New-Object System.Drawing.Pen $white, 1.8
        $steam.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $steam.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($x in 11.0, 16.0) {
            $g.DrawBezier($steam, $x, 10.5, ($x - 2), 8.5, ($x + 2), 6.5, $x, 4.5)
        }
    }
    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER (height is doubled: XOR + AND mask)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write([int]($s * $s * 4 + $maskRow * $s))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskRow * $s)))   # AND mask unused (alpha wins)
    $bw.Flush()
    $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    } else {
        , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()
Write-Host "Icon written: $((Resolve-Path $Out).Path)"
