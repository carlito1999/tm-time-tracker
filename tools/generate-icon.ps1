# Generates assets/app.ico as a multi-resolution stopwatch silhouette.
# Re-run only when the design changes; the committed .ico is the source of truth at build time.

Add-Type -AssemblyName System.Drawing

$sizes  = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$outIco = Join-Path $PSScriptRoot '..\assets\app.ico'
$outDir = Split-Path $outIco -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$bg   = [System.Drawing.Color]::FromArgb(255, 14, 124, 134)   # teal
$fg   = [System.Drawing.Color]::White                          # stopwatch body
$hand = [System.Drawing.Color]::FromArgb(255, 245, 158,  11)   # amber sweep hand

function New-StopwatchBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad    = [Math]::Max(1, [int]($size * 0.06))
    $rect   = [System.Drawing.Rectangle]::new($pad, $pad, $size - 2*$pad, $size - 2*$pad)
    $radius = [int]($size * 0.18)
    $d      = $radius * 2
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X,                $rect.Y,                $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d,       $rect.Y,                $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d,       $rect.Bottom - $d,      $d, $d,   0, 90)
    $path.AddArc($rect.X,                $rect.Bottom - $d,      $d, $d,  90, 90)
    $path.CloseFigure()
    $brushBg = New-Object System.Drawing.SolidBrush $bg
    $g.FillPath($brushBg, $path)
    $path.Dispose()

    $brushFg = New-Object System.Drawing.SolidBrush $fg

    $cx = [single]($size / 2.0)
    $cy = [single]($size / 2.0 + $size * 0.04)
    $r  = [single]($size * 0.30)

    $crownW = [single]($size * 0.18)
    $crownH = [single]($size * 0.07)
    $crownRect = [System.Drawing.RectangleF]::new(
        $cx - $crownW/2, $cy - $r - $crownH * 0.85, $crownW, $crownH)
    $g.FillRectangle($brushFg, $crownRect)

    $sbW = [single]($size * 0.06)
    $sbH = [single]($size * 0.10)
    $sbAngle = -45
    $sbCx = $cx + ($r + $sbH * 0.25) * [Math]::Cos($sbAngle * [Math]::PI / 180)
    $sbCy = $cy + ($r + $sbH * 0.25) * [Math]::Sin($sbAngle * [Math]::PI / 180)
    $state = $g.Save()
    $g.TranslateTransform([single]$sbCx, [single]$sbCy)
    $g.RotateTransform($sbAngle)
    $g.FillRectangle($brushFg, -$sbW/2, -$sbH/2, $sbW, $sbH)
    $g.Restore($state)

    $bodyRect = [System.Drawing.RectangleF]::new($cx - $r, $cy - $r, 2*$r, 2*$r)
    $g.FillEllipse($brushFg, $bodyRect)

    $innerPad = [single][Math]::Max(1.0, $size * 0.035)
    $innerRect = [System.Drawing.RectangleF]::new(
        $bodyRect.X + $innerPad, $bodyRect.Y + $innerPad,
        $bodyRect.Width  - 2*$innerPad,
        $bodyRect.Height - 2*$innerPad)
    $g.FillEllipse($brushBg, $innerRect)

    $tickLen = [single]($r * 0.20)
    $tickW   = [single][Math]::Max(1.0, $size * 0.045)
    $penTick = New-Object System.Drawing.Pen($fg, $tickW)
    $penTick.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penTick.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($deg in -90, 0, 90, 180) {
        $rad    = $deg * [Math]::PI / 180
        $rOuter = $r - $innerPad - $tickW * 0.5
        $rInner = $rOuter - $tickLen
        $x1 = $cx + $rInner * [Math]::Cos($rad)
        $y1 = $cy + $rInner * [Math]::Sin($rad)
        $x2 = $cx + $rOuter * [Math]::Cos($rad)
        $y2 = $cy + $rOuter * [Math]::Sin($rad)
        $g.DrawLine($penTick, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
    }
    $penTick.Dispose()

    $handAngle = -60 * [Math]::PI / 180
    $handLen   = [single]($r * 0.68)
    $handW     = [single][Math]::Max(1.5, $size * 0.055)
    $penHand = New-Object System.Drawing.Pen($hand, $handW)
    $penHand.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penHand.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $hx = $cx + $handLen * [Math]::Cos($handAngle)
    $hy = $cy + $handLen * [Math]::Sin($handAngle)
    $g.DrawLine($penHand, $cx, $cy, [single]$hx, [single]$hy)
    $penHand.Dispose()

    $dotR = [single][Math]::Max(1.0, $size * 0.05)
    $brushHand = New-Object System.Drawing.SolidBrush $hand
    $g.FillEllipse($brushHand, $cx - $dotR, $cy - $dotR, 2*$dotR, 2*$dotR)
    $brushHand.Dispose()

    $brushFg.Dispose()
    $brushBg.Dispose()
    $g.Dispose()
    return $bmp
}

$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-StopwatchBitmap $size
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += ,$ms.ToArray()
    $ms.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$dataOffset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size  = $sizes[$i]
    $bytes = $pngs[$i]
    $dim   = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $bytes.Length
}
foreach ($bytes in $pngs) { $bw.Write($bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $ico.ToArray())
$bw.Dispose()
$ico.Dispose()

Write-Host "Wrote $outIco ($((Get-Item $outIco).Length) bytes, $($sizes.Count) sizes)"
