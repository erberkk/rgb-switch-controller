Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\app.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $pad = [Math]::Max(1, $s * 0.03)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0x8B, 0x5C, 0xF6)), ([System.Drawing.Color]::FromArgb(0x22, 0xD3, 0xEE)), 45.0
    $g.FillEllipse($brush, $rect)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]($s * 0.09))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $r = $s * 0.24
    $g.DrawArc($pen, [float]($s / 2 - $r), [float]($s / 2 - $r + $s * 0.03), [float](2 * $r), [float](2 * $r), -60, 300)
    $g.DrawLine($pen, [float]($s / 2), [float]($s * 0.2), [float]($s / 2), [float]($s * 0.48))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"wrote $out"
