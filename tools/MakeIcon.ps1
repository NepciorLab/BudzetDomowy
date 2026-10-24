# Builds a multi-size Windows icon (.ico) from a square PNG.
#   .\tools\MakeIcon.ps1                      -> Assets\BudzetDomowy.png  ->  Assets\BudzetDomowy.ico
#   .\tools\MakeIcon.ps1 -Source my.png       -> copies my.png over the master PNG first
# Must stay plain ASCII (Windows PowerShell 5.1).
param(
    [string]$Source = "",
    [string]$Assets = (Join-Path $PSScriptRoot "..\BudzetDomowy.App\Assets")
)

Add-Type -AssemblyName System.Drawing

$master = Join-Path $Assets "BudzetDomowy.png"
$icoPath = Join-Path $Assets "BudzetDomowy.ico"
if ($Source -ne "") { Copy-Item -LiteralPath $Source -Destination $master -Force }
if (-not (Test-Path $master)) { throw "Missing $master" }

$sizes = 16, 24, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $master))
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # keep the aspect ratio, centre the picture
    $scale = [Math]::Min($s / $src.Width, $s / $src.Height)
    $w = $src.Width * $scale; $h = $src.Height * $scale
    $g.DrawImage($src, ($s - $w) / 2, ($s - $h) / 2, $w, $h)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Host "Wrote $icoPath ($($images.Count) sizes)"
