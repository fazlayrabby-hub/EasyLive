# Generates the EasyLive logo: assets/logo.png (256 px) and assets/EasyLive.ico (16..256 px, PNG entries).
# Pure System.Drawing, no external tools. Run:  powershell -NoProfile -File build\make-logo.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-LogoBitmap([int]$size) {
    # Draw big, then downscale: crisp edges at every icon size.
    $S = 1024
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    $m = 70; $w = $S - 2 * $m - 20; $h = $w; $r = 215
    $x = $m + 10; $y = $m

    # Soft drop shadow (stacked translucent rounded rects).
    for ($i = 18; $i -ge 1; $i--) {
        $grow = $i * 3.2
        $alpha = [int](9 + (18 - $i) * 0.7)
        $sp = New-RoundRect ($x - $grow) ($y + 28 - $grow + 10) ($w + 2 * $grow) ($h + 2 * $grow) ($r + $grow)
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 40, 10, 90))
        $g.FillPath($b, $sp); $b.Dispose(); $sp.Dispose()
    }

    # Badge body: cyan -> violet -> magenta.
    $badge = New-RoundRect $x $y $w $h $r
    $rect = New-Object System.Drawing.RectangleF $x, $y, $w, $h
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 34, 211, 238)), ([System.Drawing.Color]::FromArgb(255, 236, 72, 153)), 45
    $cb = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $cb.Colors = @([System.Drawing.Color]::FromArgb(255, 34, 211, 238), [System.Drawing.Color]::FromArgb(255, 139, 92, 246), [System.Drawing.Color]::FromArgb(255, 236, 72, 153))
    $cb.Positions = @(0.0, 0.5, 1.0)
    $grad.InterpolationColors = $cb
    $g.FillPath($grad, $badge)

    # Inner depth: darker bottom edge for a 3D feel.
    $g.SetClip($badge)
    $depth = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0, 0, 0, 0)), ([System.Drawing.Color]::FromArgb(110, 30, 0, 70)), 90
    $g.FillRectangle($depth, $rect)

    # Glossy highlight on the top half.
    $glossRect = New-Object System.Drawing.RectangleF ($x - 60), ($y - 330), ($w + 120), ($h * 0.62 + 330)
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse($glossRect)
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, $y)), ([System.Drawing.PointF]::new(0, $y + $h * 0.62)), ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($gloss, $gp)
    $g.ResetClip()

    # Bevel: light edge top-left, dark edge bottom-right.
    $bevelPen = New-Object System.Drawing.Pen (New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(200, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(110, 40, 0, 90)), 45), 14
    $inner = New-RoundRect ($x + 8) ($y + 8) ($w - 16) ($h - 16) ($r - 8)
    $g.DrawPath($bevelPen, $inner)

    # Play triangle with soft shadow.
    $cx = $x + $w * 0.37; $cy = $y + $h * 0.50; $t = $w * 0.25
    $tri = @(
        [System.Drawing.PointF]::new($cx - $t * 0.62, $cy - $t * 1.0),
        [System.Drawing.PointF]::new($cx - $t * 0.62, $cy + $t * 1.0),
        [System.Drawing.PointF]::new($cx + $t * 1.05, $cy))
    $shadowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 40, 0, 90))
    $shadowTri = $tri | ForEach-Object { [System.Drawing.PointF]::new($_.X + 8, $_.Y + 20) }
    $g.FillPolygon($shadowBrush, [System.Drawing.PointF[]]$shadowTri)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 52
    $pen.LineJoin = 'Round'
    $g.FillPolygon($white, [System.Drawing.PointF[]]$tri)
    $g.DrawPolygon($pen, [System.Drawing.PointF[]]$tri)

    # Broadcast arcs radiating to the right.
    $ax = $cx
    foreach ($k in 1, 2) {
        $rad = $t * (0.9 + 0.45 * $k)
        $alpha = if ($k -eq 1) { 245 } else { 175 }
        $ap = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255)), 40
        $ap.StartCap = 'Round'; $ap.EndCap = 'Round'
        $g.DrawArc($ap, [single]($ax - $rad), [single]($cy - $rad), [single](2 * $rad), [single](2 * $rad), -40, 80)
        $ap.Dispose()
    }
    $g.Dispose()

    $final = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = 'HighQualityBicubic'; $fg.PixelOffsetMode = 'HighQuality'; $fg.SmoothingMode = 'HighQuality'
    $fg.DrawImage($bmp, 0, 0, $size, $size)
    $fg.Dispose(); $bmp.Dispose()
    return $final
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $b = Get-LogoBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { $b.Save((Join-Path $out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose(); $ms.Dispose()
}

# ICO container with PNG-compressed entries.
$fs = [System.IO.File]::Create((Join-Path $out 'EasyLive.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Wrote $out\logo.png and $out\EasyLive.ico"


