# Deterministic format/size derivation only. Never edits or redraws source artwork.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source=Join-Path $root 'assets/branding/LS_Overlay_icon.png'
$output=Join-Path $root 'src/GachaOverlay.App/Assets/Branding'
$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new($source)
$streams=[Collections.Generic.List[byte[]]]::new()
$sizes=@(16,24,32,48,64,128,256)
try {
    foreach($size in $sizes) {
        $image=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[Drawing.Graphics]::FromImage($image)
        $memory=[IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale=[Math]::Min($size/$bitmap.Width,$size/$bitmap.Height)
            $width=[single]($bitmap.Width*$scale)
            $height=[single]($bitmap.Height*$scale)
            $rect=[Drawing.RectangleF]::new(($size-$width)/2,($size-$height)/2,$width,$height)
            $graphics.DrawImage($bitmap,$rect)
            $image.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
            $streams.Add($memory.ToArray())
            if($size -eq 256) { $image.Save((Join-Path $output 'LSOverlay-AppIcon.png'),[Drawing.Imaging.ImageFormat]::Png) }
        } finally { $memory.Dispose(); $graphics.Dispose(); $image.Dispose() }
    }
    $file=[IO.File]::Create((Join-Path $output 'LSOverlay-AppIcon.ico'))
    $writer=[IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++) {
            $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$streams[$i].Length); $writer.Write([uint32]$offset)
            $offset+=$streams[$i].Length
        }
        foreach($bytes in $streams){$writer.Write($bytes)}
    } finally { $writer.Dispose() }
} finally { $bitmap.Dispose() }
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash){throw 'Source artwork changed'}
Write-Output 'LS icon derived: 16/24/32/48/64/128/256; source hash unchanged.'
