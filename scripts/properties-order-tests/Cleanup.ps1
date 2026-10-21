param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$labRoot=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/properties-order-tests'))+[IO.Path]::DirectorySeparatorChar
foreach($year in 2023..2027){
 $manifest="C:/Users/plemert/AppData/Roaming/Autodesk/Revit/Addins/$year/BIMaestro.PropertiesOrderProbe.addin"
 if(!(Test-Path -LiteralPath $manifest)){continue}
 [xml]$xml=Get-Content -LiteralPath $manifest -Raw
 $addin=$xml.RevitAddIns.AddIn
 $assembly=[IO.Path]::GetFullPath([string]$addin.Assembly)
 if($addin.AddInId -ne '16F3166F-AF76-444F-BBF9-CE851BD98221' -or !$assembly.StartsWith($labRoot,[StringComparison]::OrdinalIgnoreCase)){
  throw "Refusing an unowned registration: $manifest"
 }
 Remove-Item -LiteralPath $manifest
 Write-Output "Removed isolated probe registration $year"
}
