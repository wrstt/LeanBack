# Generates leanback.ico (16/32/48/256 PNG-compressed entries).
#
# The mark is the product: a wide bar that fades out (bulk you can reinstall), a solid
# bar (what's actually yours), and a chevron carrying it down to storage. Centred and
# narrowing so it reads as a funnel rather than a hamburger menu.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outPath  = Join-Path $PSScriptRoot 'LeanBack.WinUI\Assets\leanback.ico'
$previews = Join-Path $PSScriptRoot '..'
$sizes = 16, 32, 48, 256
$pngs = @()

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    if ($d -gt $h) { $d = $h }
    if ($d -gt $w) { $d = $w }
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'

    # ---- squircle in brand amber: distinctive against a taskbar of blue dev tools
    $pad = [Math]::Max(0, [int]($size * 0.02))
    $inner = $size - (2 * $pad)
    $rect = New-Object System.Drawing.Rectangle -ArgumentList @($pad, $pad, $inner, $inner)
    $tile = New-RoundedPath $pad $pad $inner $inner ($size * 0.25)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, [System.Drawing.ColorTranslator]::FromHtml('#FDB92E'),
               [System.Drawing.ColorTranslator]::FromHtml('#C2690A'), 55)
    $g.FillPath($brush, $tile)

    # ---- marks, in deep ink
    $ink     = [System.Drawing.ColorTranslator]::FromHtml('#2A1603')
    $inkSoft = [System.Drawing.Color]::FromArgb(105, $ink.R, $ink.G, $ink.B)

    $barH = $size * 0.105
    $r    = $barH / 2.0

    # widest bar, faded — the reinstallable bulk that gets left behind
    $b1 = New-RoundedPath ($size * 0.185) ($size * 0.245) ($size * 0.63) $barH $r
    $sb1 = New-Object System.Drawing.SolidBrush($inkSoft)
    $g.FillPath($sb1, $b1)

    # narrower solid bar — what only you have
    $b2 = New-RoundedPath ($size * 0.295) ($size * 0.435) ($size * 0.41) $barH $r
    $sb2 = New-Object System.Drawing.SolidBrush($ink)
    $g.FillPath($sb2, $b2)

    # chevron down — carried to storage
    $pen = New-Object System.Drawing.Pen($ink, [single]$barH)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $cx = $size / 2.0
    $ax = $size * 0.145
    $yTop = $size * 0.645
    $yTip = $size * 0.775
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF([single]($cx - $ax), [single]$yTop)),
        (New-Object System.Drawing.PointF([single]$cx,         [single]$yTip)),
        (New-Object System.Drawing.PointF([single]($cx + $ax), [single]$yTop))
    ))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $size; Data = $ms.ToArray() }

    if ($size -eq 256) {
        $bmp.Save((Join-Path $PSScriptRoot 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $g.Dispose(); $bmp.Dispose(); $pen.Dispose(); $brush.Dispose()
    $tile.Dispose(); $b1.Dispose(); $b2.Dispose(); $sb1.Dispose(); $sb2.Dispose(); $ms.Dispose()
}

# ICO container with PNG entries (valid since Vista)
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Data.Length); $bw.Write([uint32]$offset)
    $offset += $p.Data.Length
}
foreach ($p in $pngs) { $bw.Write($p.Data) }
$bw.Flush(); $bw.Close()
Write-Output "Wrote $outPath ($((Get-Item $outPath).Length) bytes)"
