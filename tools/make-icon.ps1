# Builds src\KarSlides.App\app.ico (16..256 px, PNG-compressed entries) and the web favicon from logo.png.
# Run after changing logo.png: powershell -File tools\make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$logo = [System.Drawing.Image]::FromFile((Join-Path $root "logo.png"))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($logo, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$logo.Dispose()

# ICO: ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes) per image + PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))  # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)                     # palette, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                # planes, bits per pixel
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
$ico = Join-Path $root "src\KarSlides.App\app.ico"
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())
Copy-Item $ico (Join-Path $root "src\KarSlides.Web\favicon.ico") -Force
Copy-Item (Join-Path $root "logo.png") (Join-Path $root "src\KarSlides.Web\logo.png") -Force
"Icon: $ico ($([math]::Round((Get-Item $ico).Length / 1KB)) KB, $($sizes -join ', ') px)"
