# 生成程序图标 TouchToggle.ico（多尺寸，PNG 压缩），以及安装向导用的图片
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 在 size×size 的画布上画图标：蓝色渐变圆角方块 + 白色屏幕 + 指尖触点
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    $tile = New-RoundRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(58, 150, 221)), ([System.Drawing.Color]::FromArgb(0, 84, 166)), 60
    $g.FillPath($grad, $tile)

    $white = [System.Drawing.Color]::White
    $penW = [Math]::Max(1.6, 16 * $s)
    $pen = New-Object System.Drawing.Pen $white, $penW
    $pen.LineJoin = 'Round'
    # 屏幕外框
    $screen = New-RoundRect (48 * $s) (60 * $s) (160 * $s) (110 * $s) (16 * $s)
    $g.DrawPath($pen, $screen)
    # 支架
    $brush = New-Object System.Drawing.SolidBrush $white
    $g.FillPath($brush, (New-RoundRect (104 * $s) (176 * $s) (48 * $s) (14 * $s) (4 * $s)))
    $g.FillPath($brush, (New-RoundRect (84 * $s) (190 * $s) (88 * $s) (14 * $s) (7 * $s)))
    # 指尖触点 + 涟漪（小尺寸只画触点）
    $cx = 128 * $s; $cy = 115 * $s
    $dot = 34 * $s
    $g.FillEllipse($brush, $cx - $dot / 2, $cy - $dot / 2, $dot, $dot)
    if ($size -ge 32) {
        $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ([Math]::Max(1.2, 7 * $s))
        $rr = 62 * $s
        $g.DrawEllipse($ring, $cx - $rr / 2, $cy - $rr / 2, $rr, $rr)
    }
    $g.Dispose()
    return $bmp
}

# 写 ICO：每个尺寸存一张 PNG（Windows Vista 起支持）
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'TouchToggle.ico'), $out.ToArray())

# 安装向导右上角的小图（Inno Setup 推荐 55×55 及其高 DPI 倍数）
foreach ($sz in 55, 83, 110) {
    $bmp = New-IconBitmap $sz
    $canvas = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::White)
    $g.DrawImage($bmp, 0, 0, $sz, $sz)
    $g.Dispose()
    $canvas.Save((Join-Path $PSScriptRoot "wizard-small-$sz.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $canvas.Dispose(); $bmp.Dispose()
}
# 预览
(New-IconBitmap 256).Save((Join-Path $PSScriptRoot 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"ok"
