[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../../artifacts/m10/visual-review' }
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$path = (Resolve-Path (Join-Path $PSScriptRoot '../../src/GachaOverlay.App/Assets/Branding/LSOverlay-AppIcon.ico')).Path
$bytes = [IO.File]::ReadAllBytes($path)
$atlas = [Drawing.Bitmap]::new(1000,350)
$graphics = [Drawing.Graphics]::FromImage($atlas)
$font = [Drawing.Font]::new('Segoe UI',10)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(35,39,47))
    for($i=0;$i -lt 7;$i++) {
        $entry = 6 + 16*$i
        $length = [BitConverter]::ToInt32($bytes,$entry+8)
        $offset = [BitConverter]::ToInt32($bytes,$entry+12)
        $stream = [IO.MemoryStream]::new($bytes,$offset,$length)
        $image = [Drawing.Image]::FromStream($stream)
        try {
            $x = 15 + 105*$i
            $graphics.DrawString(("$($image.Width) px"),$font,[Drawing.Brushes]::White,$x,8)
            $graphics.DrawImageUnscaled($image,$x,35)
            if($i -lt 3) {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $graphics.DrawImage($image,[Drawing.Rectangle]::new($x,170,96,96))
            }
        } finally {$image.Dispose();$stream.Dispose()}
    }
    $atlas.Save((Join-Path $OutputDirectory 'icon-sizes.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally {$font.Dispose();$graphics.Dispose();$atlas.Dispose()}
Write-Host 'Mechanical ICO frame preview generated. Source PNGs were not modified.'
