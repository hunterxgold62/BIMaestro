$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'BIMaestro/bin/Release2024/Newtonsoft.Json.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'BIMaestro/bin/Release2024/BIMaestro.dll'))
$out=Join-Path $root 'tmp/atmospheres'
[void](New-Item -ItemType Directory -Force $out)
$method=[Couleur.ProjectBrowserColoring].GetMethod('CreateBrowserThemeScript',[Reflection.BindingFlags]'NonPublic,Static')
$profileType=[Couleur.ProjectBrowserColorProfilePreferences]
$serialize=$profileType.GetMethod('Serialize',[Reflection.BindingFlags]'NonPublic,Static')
$deserialize=$profileType.GetMethod('Deserialize',[Reflection.BindingFlags]'NonPublic,Static')
foreach($mode in @('Verre dépoli',"Plan d'architecte","Encre dans l'eau")) {
    $settings=[Couleur.ProjectBrowserColorPreferences]::GetDefaults()
    $settings.IsEnabled=$true
    $settings.BackgroundMode=$mode
    $settings.BackgroundIntensity=60
    $settings.BackgroundAnimated=$false
    if($mode -eq "Plan d'architecte") {
        $settings.BackgroundColor=[Windows.Media.Color]::FromRgb(17,37,63)
        $settings.TextColor=[Windows.Media.Color]::FromRgb(226,239,252)
    }
    $saved=$serialize.Invoke($null,@($settings))
    $loaded=$deserialize.Invoke($null,([object[]](,$saved)))
    if($loaded.BackgroundMode -ne $mode -or $loaded.BackgroundIntensity -ne 60 -or $loaded.BackgroundAnimated) {throw 'Profile round trip failed'}
    $id=@{'Verre dépoli'='frosted'; "Plan d'architecte"='blueprint'; "Encre dans l'eau"='ink'}[$mode]
    $script=$method.Invoke($null,@($settings))
    if($script.Contains('__BIMAESTRO_')) {throw 'Unresolved script token'}
    [IO.File]::WriteAllText((Join-Path $out "$id.js"),$script)
}
Write-Output 'PASS: generated scripts and profile round trips for all three atmospheres.'
