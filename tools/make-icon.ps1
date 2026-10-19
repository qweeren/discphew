# Regenerates assets\discphew.ico (a PNG-compressed multi-size icon) from code.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-DiscPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, $size * 0.04)
    $d = $size - 2 * $pad
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $d, $d

    # Iridescent body: a path gradient whose rim colors sweep around the hue wheel.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse($rect)
    $brush = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $brush.CenterColor = [System.Drawing.Color]::FromArgb(255, 210, 216, 228)
    $hues = @(
        [System.Drawing.Color]::FromArgb(255, 108, 200, 240),
        [System.Drawing.Color]::FromArgb(255, 183, 148, 246),
        [System.Drawing.Color]::FromArgb(255, 240, 160, 200),
        [System.Drawing.Color]::FromArgb(255, 250, 220, 140),
        [System.Drawing.Color]::FromArgb(255, 130, 230, 190)
    )
    $brush.SurroundColors = $hues
    $g.FillEllipse($brush, $rect)

    # Track rings.
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 20, 22, 26)), ([Math]::Max(1, $size / 64))
    foreach ($f in 0.78, 0.62) {
        $rd = $d * $f; $o = ($size - $rd) / 2
        $g.DrawEllipse($ringPen, $o, $o, $rd, $rd)
    }
    # Hub and hole.
    $hub = $d * 0.36; $o = ($size - $hub) / 2
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 238, 244))), $o, $o, $hub, $hub)
    $hole = $d * 0.16; $o = ($size - $hole) / 2
    $g.CompositingMode = 'SourceCopy'
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Transparent)), $o, $o, $hole, $hole)
    $g.CompositingMode = 'SourceOver'
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 30, 34, 40)), ([Math]::Max(1, $size / 48))
    $g.DrawEllipse($edge, $rect)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { ,(New-DiscPng $s) }
$out = Join-Path $PSScriptRoot '..\assets\discphew.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$bytes.Length); $w.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) { $w.Write($bytes) }
$w.Close()
Write-Host "Wrote $out"
