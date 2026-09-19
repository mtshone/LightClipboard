# Generates Assets\app.ico (multi-size PNG-compressed ICO) for LightClipboard.
# Usage: pwsh -File tools\make-icon.ps1
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\LightClipboard\Assets\app.ico')
)

Add-Type -AssemblyName System.Drawing

function New-ClipboardBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    # --- 圆角底板（渐变） ---
    $pad = $s * 0.055
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $r = $s * 0.26
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    $c2 = [System.Drawing.Color]::FromArgb(255, 99, 102, 241)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $g.FillPath($brush, $path)

    # --- 板夹（顶部小矩形） ---
    $cw = $s * 0.34
    $ch = $s * 0.13
    $cx = ($s - $cw) / 2
    $cy = $s * 0.155
    $clipRect = New-Object System.Drawing.RectangleF($cx, $cy, $cw, $ch)
    $clipR = $ch * 0.45
    $clipPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cd = $clipR * 2
    $clipPath.AddArc($clipRect.X, $clipRect.Y, $cd, $cd, 180, 90)
    $clipPath.AddArc($clipRect.Right - $cd, $clipRect.Y, $cd, $cd, 270, 90)
    $clipPath.AddArc($clipRect.Right - $cd, $clipRect.Bottom - $cd, $cd, $cd, 0, 90)
    $clipPath.AddArc($clipRect.X, $clipRect.Bottom - $cd, $cd, $cd, 90, 90)
    $clipPath.CloseFigure()
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillPath($white, $clipPath)

    # --- 内容线条 ---
    if ($size -ge 24) {
        $lineH = [Math]::Max(1.5, $s * 0.058)
        $lineX = $s * 0.28
        $lineW = $s * 0.44
        $ys = @(0.44, 0.575, 0.71)
        $alphas = @(255, 220, 150)
        for ($i = 0; $i -lt $ys.Count; $i++) {
            $w = if ($i -eq 2) { $lineW * 0.6 } else { $lineW }
            $lr = New-Object System.Drawing.RectangleF($lineX, ($s * $ys[$i]), $w, $lineH)
            $lb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alphas[$i], 255, 255, 255))
            $g.FillRectangle($lb, $lr)
            $lb.Dispose()
        }
    }

    $brush.Dispose(); $white.Dispose(); $path.Dispose(); $clipPath.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-ClipboardBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$pngs.Count)       # count
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)               # palette
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bpp
    $bw.Write([UInt32]$p.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Output "Wrote $OutFile ($((Get-Item $OutFile).Length) bytes, $($pngs.Count) sizes)"
