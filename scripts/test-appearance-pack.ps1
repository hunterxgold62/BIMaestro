$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'BIMaestro/bin/Release2024'
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Newtonsoft.Json.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'BIMaestro.dll'))
$out = Join-Path $root 'tmp/appearance-packs'
[void](New-Item -ItemType Directory -Force $out)
$pack = New-Object Couleur.AppearancePack
$pack.RibbonEnabled = $true
$pack.FullPanels = $true
$pack.Ribbon = [Couleur.RibbonColorPreferences]::GetDefaults()
$pack.Browser = [Couleur.ProjectBrowserColorPreferences]::GetDefaults()
$pack.Browser.IsEnabled = $true
$pack.Browser.BackgroundMode = "Encre dans l'eau"
$pack.Browser.BackgroundIntensity = 37
$pack.Browser.BackgroundAnimated = $false
$pack.Browser.IsActiveViewParentHighlightEnabled = $true
$pack.Browser.IsSheetViewSearchEnabled = $true
$pack.Icons = [Couleur.ProjectBrowserIcons]::Defaults()
$pack.Icons.Enabled = $true
$asset = New-Object Couleur.BrowserIconAsset
$asset.Id = 'test-custom'
$asset.Name = 'Image personnelle'
$stream = [IO.File]::OpenRead((Join-Path $root 'BIMaestro/Resources/BrowserIcons/architecture.png'))
try { $asset.Data = [Couleur.ProjectBrowserIcons]::EncodeImage($stream) } finally { $stream.Dispose() }
$pack.Icons.CustomAssets.Add($asset)
$rule = New-Object Couleur.BrowserIconRule
$rule.Name = 'ARC'
$rule.IconId = $asset.Id
$pack.Icons.Rules.Add($rule)
$path = Join-Path $out 'roundtrip.bimaestro-style.json'
[Couleur.AppearancePackFile]::Export($path,$pack)
$loaded = [Couleur.AppearancePackFile]::Import($path)
if ($loaded.Browser.BackgroundMode -ne "Encre dans l'eau" -or $loaded.Browser.BackgroundIntensity -ne 37 -or $loaded.Browser.BackgroundAnimated) { throw 'Atmosphere settings lost' }
if (!$loaded.RibbonEnabled -or !$loaded.FullPanels -or !$loaded.Icons.Enabled) { throw 'Activation lost' }
if (!$loaded.Browser.IsActiveViewParentHighlightEnabled -or !$loaded.Browser.IsSheetViewSearchEnabled) { throw 'Navigation settings lost' }
if ($loaded.Ribbon.Count -ne $pack.Ribbon.Count) { throw 'Ribbon panels lost' }
foreach($key in $pack.Ribbon.Keys) {
    foreach($property in @('BackgroundColor','BackgroundEndColor','TextColor','IsGradient','GradientDirection','BackgroundPattern','PatternStart','PatternEnd')) {
        if ($loaded.Ribbon[$key].$property -ne $pack.Ribbon[$key].$property) { throw "Ribbon mismatch: $key/$property" }
    }
}
if ($loaded.Icons.CustomAssets[0].Preview.PixelWidth -ne 64 -or $loaded.Icons.Rules[-1].Name -ne 'ARC') { throw 'Custom asset lost' }
$invalid = Join-Path $out 'invalid.json'
foreach($mutation in @('version','missing-image','invalid-image','missing-format')) {
    $json = Get-Content -Raw $path | ConvertFrom-Json
    switch($mutation) {
        'version' { $json.Version = 99 }
        'missing-image' { $json.Icons.Rules[0].IconId = 'absent' }
        'invalid-image' { $json.Icons.CustomAssets[0].Data = 'bm90LWEtcG5n' }
        'missing-format' { $json.PSObject.Properties.Remove('Format') }
    }
    $json | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $invalid
    $rejected = $false
    try { [void][Couleur.AppearancePackFile]::Import($invalid) } catch { $rejected = $true }
    if (!$rejected) { throw "Invalid pack accepted: $mutation" }
}
Write-Output 'PASS: ribbon colors, navigation, icons and custom image round-trip; incompatible and broken packs rejected.'
