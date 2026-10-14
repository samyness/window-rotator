param([string]$OutFile)
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0
    # red rounded square
    $r = 12 * $s; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(183, 28, 44))), $path)
    $white = [System.Drawing.Color]::White
    # rotation arc with arrow head
    $pen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.6, 5 * $s))
    $g.DrawArc($pen, 13*$s, 13*$s, 38*$s, 38*$s, -60, 280)
    $a = [Math]::PI * -60 / 180; $cx = 32*$s + 19*$s*[Math]::Cos($a); $cy = 32*$s + 19*$s*[Math]::Sin($a)
    $dx = [Math]::Sin($a); $dy = -[Math]::Cos($a); $nx = [Math]::Cos($a); $ny = [Math]::Sin($a)
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx + 10*$s*$dx), ($cy + 10*$s*$dy)),
        (New-Object System.Drawing.PointF ($cx + 8*$s*$nx - 2*$s*$dx), ($cy + 8*$s*$ny - 2*$s*$dy)),
        (New-Object System.Drawing.PointF ($cx - 8*$s*$nx - 2*$s*$dx), ($cy - 8*$s*$ny - 2*$s*$dy)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), $head)
    # window in the middle
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), 23*$s, 25*$s, 18*$s, 14*$s)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(183, 28, 44))), 25*$s, 29*$s, 14*$s, 8*$s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = $sizes | ForEach-Object { ,(New-Frame $_) }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
