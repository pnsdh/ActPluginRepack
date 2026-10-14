# 앱 아이콘(app.ico)을 만든다: 파란 둥근 사각형 + 흰 스패너. 크기별 PNG 를 ICO 하나에 담는다 (Vista+ PNG 항목).
# 다시 만들 때: powershell -File make-icon.ps1  (같은 폴더에 app.ico, app-256.png)
Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # 배경: 둥근 사각형, 대각선 그라데이션 (강조색 #3B82F6 계열)
    $r = 56; $m = 8; $w = 256 - 2 * $m
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc($m, $m, $r, $r, 180, 90); $bg.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $bg.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $bg.AddArc($m, $m + $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 256, 256),
        ([System.Drawing.Color]::FromArgb(255, 0x60, 0xA5, 0xFA)), ([System.Drawing.Color]::FromArgb(255, 0x1D, 0x4E, 0xD8))
    $g.FillPath($brush, $bg)

    # 스패너: 세로로 그린 뒤 45도 돌린다 (머리 = 원 - 입, 손잡이 = 둥근 막대)
    $g.TranslateTransform(128, 128); $g.RotateTransform(45); $g.TranslateTransform(-128, -128)
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddEllipse(78, 26, 100, 100)
    $region = New-Object System.Drawing.Region $head
    $jaw = New-Object System.Drawing.Drawing2D.GraphicsPath
    $jaw.FillMode = 'Winding'   # 사각형과 원이 겹친 곳도 입으로 (기본 Alternate 면 겹친 곳이 다시 채워진다)
    $jaw.AddRectangle((New-Object System.Drawing.RectangleF 108, 0, 40, 62))
    $jaw.AddEllipse(108, 42, 40, 40)
    $region.Exclude($jaw)
    $handle = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hw = 40; $hx = 128 - $hw / 2; $top = 110; $bottom = 238
    $handle.AddArc($hx, $bottom - $hw, $hw, $hw, 0, 180); $handle.AddLine($hx, $bottom - $hw / 2, $hx, $top)
    $handle.AddLine($hx + $hw, $top, $hx + $hw, $bottom - $hw / 2); $handle.CloseFigure()
    $region.Union($handle)
    $g.FillRegion([System.Drawing.Brushes]::White, $region)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $out 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO: 머리(6) + 항목(16 x n) + PNG 들
$fs = [System.IO.File]::Create((Join-Path $out 'app.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
"app.ico ($($sizes -join ', '))"
