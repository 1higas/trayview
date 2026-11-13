# Renders src\app.ico: a blue tile with a white globe (the Segoe Fluent Icons glyph, as used for
# pages without a favicon) and a small "window" badge in the corner.
# Each size is drawn separately and snapped to whole pixels so the small sizes stay sharp.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Preview dir]
param([string]$Preview)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Glyph([string]$text, [float]$cx, [float]$cy, [float]$size) {
    $family = New-Object Drawing.FontFamily 'Segoe Fluent Icons'
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddString($text, $family, 0, 100, (New-Object Drawing.PointF 0, 0), [Drawing.StringFormat]::GenericTypographic)
    $b = $path.GetBounds()
    $scale = $size / [Math]::Max($b.Width, $b.Height)
    $m = New-Object Drawing.Drawing2D.Matrix
    $m.Translate($cx, $cy)
    $m.Scale($scale, $scale)
    $m.Translate(-$b.X - $b.Width / 2, -$b.Y - $b.Height / 2)
    $path.Transform($m)
    $path
}

function Render([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    $m = [Math]::Round($s * 0.04)
    $tile = RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ([Math]::Max(2.5, $s * 0.22))
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), ([Drawing.Color]::FromArgb(0x4C, 0xC2, 0xFF)), ([Drawing.Color]::FromArgb(0x00, 0x5F, 0xB8))
    $g.FillPath($grad, $tile)

    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $globeSize = if ($s -le 20) { $s * 0.66 } else { $s * 0.6 }
    $g.FillPath($white, (Glyph ([string][char]0xE774) ($s / 2) ($s / 2) $globeSize))
    $g.Dispose()
    $bmp
}

# PNG-compressed entries (supported by Windows for every size) keep the file small.
function Entry([Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

# 40 and 64 are left out: Windows scales them from 48 and 256 with no visible loss.
$sizes = 16, 20, 24, 32, 48, 256
$images = foreach ($s in $sizes) { $b = Render $s; if ($Preview) { $b.Save((Join-Path $Preview "icon$s.png")) }; , (Entry $b) }

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\src\app.ico'), $out.ToArray())
'wrote src\app.ico ({0} bytes)' -f $out.Length
