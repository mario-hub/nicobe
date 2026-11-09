$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assets=Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
$source=[System.Drawing.Bitmap]::new((Join-Path $assets 'nicobe-source-white-outline.png'))
$frames=[System.Collections.Generic.List[byte[]]]::new()
$sizes=@(16,20,24,32,40,48,64,128,256)
try {
    foreach($size in $sizes) {
        $bitmap=[System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$size,$size))
        $stream=[System.IO.MemoryStream]::new()
        $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
        if($size -eq 256 -or $size -eq 32 -or $size -eq 16){$bitmap.Save((Join-Path $assets "nicobe-$size.png"),[System.Drawing.Imaging.ImageFormat]::Png)}
        $stream.Dispose();$graphics.Dispose();$bitmap.Dispose()
    }
    $output=[System.IO.File]::Create((Join-Path $assets 'nicobe.ico'))
    $writer=[System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++) {
            $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
            $offset+=$frames[$i].Length
        }
        foreach($frame in $frames){$writer.Write($frame)}
    } finally {$writer.Dispose();$output.Dispose()}
} finally {$source.Dispose()}
Get-Item -LiteralPath (Join-Path $assets 'nicobe.ico') | Select-Object Name,Length
