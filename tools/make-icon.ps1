# Builds the Command Center emblem from the Generals Online launcher icon (GPL-3.0): the eagle and shield without the
# "GENERALS ONLINE" lettering, with the blue turned red. Writes CommandCenter\Assets\logo.png (home page) and
# CommandCenter\Assets\app.ico (program icon). Run with Windows PowerShell:
#   powershell -STA -File tools\make-icon.ps1 -Source <Launcher\Assets\appicon.ico from the Generals Online launcher>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Output,
    [string]$Logo
)

$assets = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'CommandCenter\Assets'
if (-not $Output) { $Output = Join-Path $assets 'app.ico' }
if (-not $Logo) { $Logo = Join-Path $assets 'logo.png' }

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# The largest picture in the source icon (256 x 256)
$decoder = New-Object System.Windows.Media.Imaging.IconBitmapDecoder((New-Object Uri ([System.IO.Path]::GetFullPath($Source))), 'None', 'OnLoad')
$frame = $decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
$frame = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
$size = $frame.PixelWidth
$pixels = New-Object byte[] ($size * $size * 4)
$frame.CopyPixels($pixels, $size * 4, 0)

# The emblem ends where the lettering starts
$top = 30; $bottom = 176

# Blue (and the blue-grey shading of the shield) turns red; the silver wings and the gold eagle stay as they are
$left = $size; $right = 0
for ($y = $top; $y -le $bottom; $y++) {
    for ($x = 0; $x -lt $size; $x++) {
        $i = ($y * $size + $x) * 4
        if ($pixels[$i + 3] -lt 8) { continue }
        if ($x -lt $left) { $left = $x }
        if ($x -gt $right) { $right = $x }
        $b = $pixels[$i] / 255.0; $g = $pixels[$i + 1] / 255.0; $r = $pixels[$i + 2] / 255.0
        $max = [Math]::Max($r, [Math]::Max($g, $b)); $min = [Math]::Min($r, [Math]::Min($g, $b))
        $delta = $max - $min
        if ($max -le 0 -or $delta / $max -lt 0.3 -or $b -ne $max) { continue }
        $hue = 60 * ((($r - $g) / $delta) + 4)
        if ($hue -lt 180 -or $hue -gt 260) { continue }
        # Same brightness and strength, red hue
        $pixels[$i + 2] = [byte][Math]::Round($max * 255)
        $pixels[$i + 1] = [byte][Math]::Round($min * 255 * 0.9)
        $pixels[$i] = [byte][Math]::Round($min * 255 * 0.9)
    }
}

# The lettering covers the bottom of the shield, so the tip is drawn again: a black line closes the tail feathers,
# then the red shield narrows to a point
$edge = $bottom
$rowLeft = 0; while ($pixels[($edge * $size + $rowLeft) * 4 + 3] -lt 128) { $rowLeft++ }
$rowRight = $size - 1; while ($pixels[($edge * $size + $rowRight) * 4 + 3] -lt 128) { $rowRight-- }
$red = $pixels[(($edge * $size + $rowLeft + 2) * 4)..(($edge * $size + $rowLeft + 2) * 4 + 3)]
$tipRows = 14
for ($k = 1; $k -le $tipRows + 2; $k++) {
    $y = $edge + $k
    [Array]::Clear($pixels, $y * $size * 4, $size * 4)
    $inset = [Math]::Max(0, ($k - 2) * (($rowRight - $rowLeft) / 2.0) / $tipRows)
    $l = $rowLeft + $inset; $r = $rowRight - $inset
    for ($x = [int][Math]::Floor($l); $x -le [int][Math]::Ceiling($r); $x++) {
        $cover = [Math]::Max(0, [Math]::Min(1, [Math]::Min($x + 1 - $l, $r + 1 - $x)))
        if ($cover -le 0) { continue }
        $i = ($y * $size + $x) * 4
        $line = $k -le 2 -and $x -ge $rowLeft + 5 -and $x -le $rowRight - 5
        $pixels[$i] = if ($line) { 0 } else { $red[0] }
        $pixels[$i + 1] = if ($line) { 0 } else { $red[1] }
        $pixels[$i + 2] = if ($line) { 0 } else { $red[2] }
        $pixels[$i + 3] = [byte][Math]::Round(255 * $cover)
    }
}
$bottom = $edge + $tipRows + 2

# Square picture with the emblem in the middle
$width = $right - $left + 1; $height = $bottom - $top + 1
$side = [Math]::Max($width, $height) + 8
$square = New-Object byte[] ($side * $side * 4)
$offsetX = [int](($side - $width) / 2); $offsetY = [int](($side - $height) / 2)
for ($y = 0; $y -lt $height; $y++) {
    [Array]::Copy($pixels, (($top + $y) * $size + $left) * 4, $square, (($offsetY + $y) * $side + $offsetX) * 4, $width * 4)
}
$emblem = [System.Windows.Media.Imaging.BitmapSource]::Create($side, $side, 96, 96, [System.Windows.Media.PixelFormats]::Bgra32, $null, $square, $side * 4)

function Png([System.Windows.Media.Imaging.BitmapSource]$bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

function Render([int]$target) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, 'HighQuality')
    $dc = $visual.RenderOpen()
    $dc.DrawImage($emblem, (New-Object System.Windows.Rect(0, 0, $target, $target)))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($target, $target, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return ,(Png $bitmap)
}

[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Logo), (Render 256))
"Wrote $Logo"

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Render $s) }

# ICO file: header, one directory entry per image, then the PNG data
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Output), $out.ToArray())
"Wrote $Output ($($out.Length) bytes)"
