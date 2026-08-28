[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputIco,

    [Parameter(Mandatory = $true)]
    [string]$PreviewPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param([System.Drawing.RectangleF]$Bounds, [float]$Radius)
    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$bitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$tile = [System.Drawing.RectangleF]::new(14, 14, 228, 228)
$tilePath = New-RoundedPath -Bounds $tile -Radius 56
$gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    $tile,
    [System.Drawing.Color]::FromArgb(255, 34, 49, 67),
    [System.Drawing.Color]::FromArgb(255, 12, 21, 32),
    90
)
$border = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 91, 121, 145), 7)
$graphics.FillPath($gradient, $tilePath)
$graphics.DrawPath($border, $tilePath)

$pulse = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(45, 137),
    [System.Drawing.PointF]::new(79, 137),
    [System.Drawing.PointF]::new(101, 87),
    [System.Drawing.PointF]::new(132, 181),
    [System.Drawing.PointF]::new(158, 119),
    [System.Drawing.PointF]::new(204, 119)
)
$pulsePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 240, 246, 250), 16)
$pulsePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pulsePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pulsePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$graphics.DrawLines($pulsePen, $pulse)

$glow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(90, 47, 211, 129))
$dot = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 47, 211, 129))
$ring = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 239, 249, 245), 5)
$graphics.FillEllipse($glow, 166, 166, 82, 82)
$graphics.FillEllipse($dot, 181, 181, 52, 52)
$graphics.DrawEllipse($ring, 181, 181, 52, 52)

$previewDirectory = Split-Path -Parent $PreviewPng
$icoDirectory = Split-Path -Parent $OutputIco
[System.IO.Directory]::CreateDirectory($previewDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($icoDirectory) | Out-Null
$bitmap.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)

$memory = [System.IO.MemoryStream]::new()
$bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $memory.ToArray()
$file = [System.IO.File]::Open($OutputIco, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Dispose()
$file.Dispose()
$memory.Dispose()

$ring.Dispose()
$dot.Dispose()
$glow.Dispose()
$pulsePen.Dispose()
$border.Dispose()
$gradient.Dispose()
$tilePath.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

