# Draws VideoTrimmer.ico: a kept span between a green in handle and a red out handle, like the timeline.
# Run from Windows PowerShell or pwsh: .\assets\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    $background = New-RoundedPath (8 * $s) (8 * $s) (240 * $s) (240 * $s) (52 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 38, 46))), $background)

    # Filmstrip frames, dimmed outside the kept span.
    $frame = [System.Drawing.Color]::FromArgb(70, 78, 94)
    foreach ($i in 0..3) {
        $fx = (34 + $i * 50) * $s
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $frame), $fx, 78 * $s, 38 * $s, 70 * $s)
    }

    # Kept span and waveform.
    $accent = [System.Drawing.Color]::FromArgb(76, 141, 255)
    $span = New-RoundedPath (74 * $s) (64 * $s) (108 * $s) (128 * $s) (10 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, $accent))), $span)
    $g.DrawPath((New-Object System.Drawing.Pen $accent, ([Math]::Max(1, 8 * $s))), $span)
    if ($size -ge 32) {
        $wave = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(242, 244, 248))
        $heights = 10, 24, 16, 34, 22, 12, 28, 18
        for ($i = 0; $i -lt $heights.Count; $i++) {
            $h = $heights[$i] * $s
            $g.FillRectangle($wave, (88 + $i * 11) * $s, 168 * $s - $h / 2, 6 * $s, $h)
        }
    }

    # In and out handles.
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(61, 220, 151))), (New-RoundedPath (56 * $s) (48 * $s) (28 * $s) (160 * $s) (8 * $s)))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 107, 107))), (New-RoundedPath (172 * $s) (48 * $s) (28 * $s) (160 * $s) (8 * $s)))

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return $stream
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add((New-IconPng $size).ToArray()) }
$out = Join-Path $PSScriptRoot 'VideoTrimmer.ico'
$file = [System.IO.File]::Create($out)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($png in $images) { $writer.Write($png) }
$writer.Dispose()
Write-Output "Wrote $out"
