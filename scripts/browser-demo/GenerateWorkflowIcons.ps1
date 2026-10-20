$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder = Join-Path $repo 'BIMaestro/Resources/BrowserIcons'
# Flat vector masters, rasterized to the same transparent PNG format as the existing library.
$icons = @(
 @('existing','#557D9E','M8,56 L8,12 44,12 44,56 Z M17,20 L17,28 25,28 25,20 Z M31,20 L31,28 39,28 39,20 Z M17,34 L17,42 25,42 25,34 Z M31,34 L31,42 39,42 39,34 Z M25,48 L25,56 33,56 33,48 Z'),
 @('demolition','#DC6046','M10,45 L35,20 42,27 17,52 Z M29,12 L37,4 58,25 50,33 Z'),
 @('new-project','#27A97C','M7,30 L29,10 51,30 46,36 44,34 44,56 14,56 14,34 12,36 Z M25,40 L25,56 34,56 34,40 Z M50,3 L53,10 61,13 53,16 50,24 47,16 40,13 47,10 Z'),
 @('study','#5D82D1','M26,6 L38,6 36,19 56,55 48,59 32,29 16,59 8,55 28,19 Z M18,37 L46,37 46,43 18,43 Z'),
 @('validation','#29A36D','M32,4 A28,28 0 1 1 31.99,4 Z M13,31 L25,43 51,20 45,14 25,32 19,25 Z'),
 @('publication','#BF68CC','M7,39 L15,39 15,51 49,51 49,39 57,39 57,59 7,59 Z M27,38 L27,19 18,28 12,22 32,3 52,22 46,28 37,19 37,38 Z'),
 @('archive','#BA8240','M5,9 L59,9 59,21 5,21 Z M10,25 L54,25 54,57 10,57 Z M23,31 L23,38 41,38 41,31 Z'),
 @('levels','#4B91C2','M5,17 L32,4 59,17 32,30 Z M5,28 L32,41 59,28 59,37 32,50 5,37 Z M5,43 L32,56 59,43 59,51 32,64 5,51 Z'),
 @('zone','#D76A93','M32,3 C9,3 5,23 15,38 L32,61 49,38 C59,23 55,3 32,3 Z M32,14 A10,10 0 1 1 31.99,14 Z'),
 @('garden','#63AA56','M9,49 C2,20 24,5 58,6 C58,35 45,58 19,52 L10,61 4,55 36,25 31,20 Z'),
 @('room','#CA9A4C','M12,5 L52,5 52,59 44,59 44,13 20,13 20,59 12,59 Z M24,17 L40,21 40,58 24,52 Z M34,34 L34,40 38,40 38,34 Z'),
 @('reception','#D3A440','M7,44 C7,27 16,16 28,14 L28,7 36,7 36,14 C48,16 57,27 57,44 Z M4,49 L60,49 60,57 4,57 Z'),
 @('restaurant','#D5814F','M7,5 L13,5 13,21 17,21 17,5 23,5 23,21 27,21 27,5 33,5 33,25 24,33 24,59 16,59 16,33 7,25 Z M47,5 C62,5 64,30 54,34 L54,59 46,59 46,34 C36,30 36,5 47,5 Z'),
 @('roof','#6F88BC','M4,32 L31,5 60,32 54,38 31,17 10,38 Z M10,42 L54,42 54,52 10,52 Z M43,7 L53,7 53,22 43,12 Z'),
 @('details','#BA67A0','M5,19 L59,19 59,47 5,47 Z M12,19 L12,32 18,32 18,19 Z M25,19 L25,29 31,29 31,19 Z M38,19 L38,32 44,32 44,19 Z M51,19 L51,29 56,29 56,19 Z'),
 @('presentation','#6687CA','M5,7 L59,7 59,41 36,41 36,48 50,57 45,63 32,54 19,63 14,57 28,48 28,41 5,41 Z M13,15 L13,33 51,33 51,15 Z')
)
foreach ($icon in $icons) {
 $geometry = [Windows.Media.Geometry]::Parse('F0 ' + $icon[2])
 $visual = [Windows.Media.DrawingVisual]::new()
 $dc = $visual.RenderOpen()
 $dc.DrawGeometry([Windows.Media.BrushConverter]::new().ConvertFromString($icon[1]),$null,$geometry)
 $dc.Close()
 $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(64,64,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $file = [IO.File]::Create((Join-Path $folder ($icon[0]+'.png')))
 try { $encoder.Save($file) } finally { $file.Dispose() }
 $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><path fill="'+$icon[1]+'" fill-rule="evenodd" d="'+$icon[2]+'"/></svg>'
 [IO.File]::WriteAllText((Join-Path $folder ($icon[0]+'.svg')), $svg)
}
Write-Output ('Generated ' + $icons.Count + ' transparent 64px icons with vector masters.')
