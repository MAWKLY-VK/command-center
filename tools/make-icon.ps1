# Draws the Command Center emblem (the shield on the home page) at the usual icon sizes and packs
# them into CommandCenter\Assets\app.ico. Run with Windows PowerShell:  powershell -STA -File tools\make-icon.ps1
param([string]$Output = (Join-Path $PSScriptRoot '..\CommandCenter\Assets\app.ico'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    # The emblem is drawn on a 200 x 220 canvas; keep it centred in a square
    $scale = $size / 224.0
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform((($size - 200 * $scale) / 2), (2 * $scale))))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

    $outer = [System.Windows.Media.Geometry]::Parse('M100,6 L190,34 L190,104 C190,160 150,196 100,214 C50,196 10,160 10,104 L10,34 Z')
    $inner = [System.Windows.Media.Geometry]::Parse('M100,20 L176,44 L176,104 C176,150 142,182 100,198 C58,182 24,150 24,104 L24,44 Z')
    $star = [System.Windows.Media.Geometry]::Parse('M100,52 L109.4,78 L137,79 L115.2,96 L123,122.5 L100,107 L77,122.5 L84.8,96 L63,79 L90.6,78 Z')
    $chevron1 = [System.Windows.Media.Geometry]::Parse('M60,136 L100,154 L140,136 L140,149 L100,167 L60,149 Z')
    $chevron2 = [System.Windows.Media.Geometry]::Parse('M66,158 L100,173 L134,158 L134,169 L100,184 L66,169 Z')

    $navy = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(0x0A, 0x16, 0x60), [System.Windows.Media.Color]::FromRgb(0x02, 0x06, 0x18), 90)
    $dc.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x29, 0x80, 0xFF))), $null, $outer)
    $dc.DrawGeometry($navy, $null, $inner)
    $gold = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0xFE, 0xCD, 0x03))
    if ($size -lt 40) {
        # Small icons: a bigger star and no chevrons, so the shape stays readable
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform(1.45, 1.45, 100, 100)))
        $dc.DrawGeometry($gold, $null, $star)
        $dc.Pop()
    } else {
        $silver = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0xC8, 0xD0, 0xDA))
        $dc.DrawGeometry($gold, $null, $star)
        $dc.DrawGeometry($silver, $null, $chevron1)
        $dc.DrawGeometry($silver, $null, $chevron2)
    }
    $dc.Pop(); $dc.Pop(); $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

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
