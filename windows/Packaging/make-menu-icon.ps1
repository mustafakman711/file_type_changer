# Regenerates Resources/Brand/NashiMenu.ico (Explorer menu and verb icon) from
# NashiIcon.png. PNG-compressed frames; Windows Vista and later read them.
param([string]$Source = "$PSScriptRoot/../../Resources/Brand/NashiIcon.png",
      [string]$Target = "$PSScriptRoot/../../Resources/Brand/NashiMenu.ico")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$original = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
  $frames = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($original, 0, 0, $size, $size); $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream; $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
  }
} finally { $original.Dispose() }
$out = New-Object System.IO.MemoryStream; $writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
  $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
  $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
$writer.Flush(); [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Target), $out.ToArray())
Write-Host "Wrote $Target ($($out.Length) bytes)"
