param([string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [System.IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $outputPath) { throw 'Destination already exists; refusing to overwrite.' }
$bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
$bitmap.BeginInit()
$bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$bitmap.UriSource = [Uri]::new($sourcePath)
$bitmap.EndInit()
$converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
$stride = $converted.PixelWidth * 4
$pixels = [byte[]]::new($stride * $converted.PixelHeight)
$converted.CopyPixels($pixels, $stride, 0)
$original = $pixels.Clone()
$changed = 0
for ($i = 0; $i -lt $pixels.Length; $i += 4) {
    $b = [int]$pixels[$i]; $g = [int]$pixels[$i+1]; $r = [int]$pixels[$i+2]
    if ($g -gt $r -and $g -gt $b -and $pixels[$i+3] -gt 0) {
        $pixels[$i] = $r
        $pixels[$i+1] = $r
        $pixels[$i+2] = $g
        $changed++
    }
    if ($pixels[$i+3] -ne $original[$i+3]) { throw 'Alpha channel changed.' }
}
$result = [System.Windows.Media.Imaging.BitmapSource]::Create($converted.PixelWidth, $converted.PixelHeight,
    $converted.DpiX, $converted.DpiY, [System.Windows.Media.PixelFormats]::Bgra32, $null, $pixels, $stride)
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($result))
$stream = [System.IO.File]::Create($outputPath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output "Recolored $changed pixels; dimensions and every alpha byte unchanged. Output: $outputPath"
