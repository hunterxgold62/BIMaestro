param([switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package=Join-Path $repo 'tmp/properties-custom-group-tests/manual-2025'
$plugin=Join-Path $package 'plugin/BIMaestro.dll'
$template=Join-Path $repo 'tmp/properties-custom-group-tests/2025-manual-template'
$source=Join-Path $repo 'tmp/history-stress/antibes-rapid-j14-restore-v11/Antibes-restaure.rvt'
$hashes=Get-Content -LiteralPath (Join-Path $package 'build-hashes.json') -Raw|ConvertFrom-Json
foreach($record in $hashes){
 if(!(Test-Path -LiteralPath $record.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $record.Path -Algorithm SHA256).Hash -ne $record.Hash){throw "Test package changed or missing: $($record.Path)"}
}
if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw 'The Antibes source model is unavailable.'}
$registration=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
[xml]$xml=Get-Content -LiteralPath $registration -Raw
$entry=@($xml.RevitAddIns.AddIn|Where-Object {$_.FullClassName -eq 'BIMaestroApp'})
if($entry.Count -ne 1 -or ([guid]$entry[0].AddInId) -ne [guid]'E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09'){throw 'Unexpected BIMaestro registration: nothing changed.'}
if($ValidateOnly){Write-Output 'Validated: Revit 2025 package, helper, Antibes source and BIMaestro registration. No launch or registration change.';return}
if(@(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0){throw 'Fermez Revit, apres avoir enregistre vos projets, puis relancez ce test.'}
$tag='-manual-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)
$run='2025'+$tag
$session=Join-Path $repo ('tmp/properties-custom-group-tests/'+$run)
New-Item -ItemType Directory -Path $session|Out-Null
foreach($name in @('BIMaestro.PropertiesCustomGroupProbe.dll','Newtonsoft.Json.dll','version.txt')){Copy-Item -LiteralPath (Join-Path $template $name) -Destination $session}
Copy-Item -LiteralPath $source -Destination (Join-Path $session 'PropertiesCustomGroupFixture.rvt')
[xml]$probe=Get-Content -LiteralPath (Join-Path $template 'BIMaestro.PropertiesCustomGroupProbe.addin') -Raw
$probe.RevitAddIns.AddIn.Assembly=Join-Path $session 'BIMaestro.PropertiesCustomGroupProbe.dll'
$probe.Save((Join-Path $session 'BIMaestro.PropertiesCustomGroupProbe.addin'))
[IO.File]::WriteAllText((Join-Path $session 'custom-title.txt'),'Mes paramètres métier',[Text.Encoding]::UTF8)
$original=[IO.File]::ReadAllBytes($registration)
[IO.File]::WriteAllBytes((Join-Path $session 'BIMaestro-registration-original.xml'),$original)
$temporaryHash=$null
try{
 $entry[0].Assembly=$plugin;$xml.Save($registration)
 $temporaryHash=(Get-FileHash -LiteralPath $registration).Hash
 & (Join-Path $PSScriptRoot 'Start.ps1') -OutputTag $tag -ReplaceLabRegistration
 foreach($command in @('fixture','prepare-plans','audit-fast','inventory','setup-combined','select-plan','compact-state')){
  & (Join-Path $PSScriptRoot 'Send.ps1') -Run $run -Command $command
  if($LASTEXITCODE -ne 0){throw "Test preparation stopped at $command. See $session"}
 }
 Write-Output "PRET : copie d'Antibes ouverte avec le prototype 2025. Votre fichier source est conserve. Dossier : $session"
}finally{
 if($temporaryHash -and (Get-FileHash -LiteralPath $registration).Hash -eq $temporaryHash){[IO.File]::WriteAllBytes($registration,$original)}
 else {Write-Warning "Le manifeste BIMaestro a change pendant le lancement. Sauvegarde : $session/BIMaestro-registration-original.xml"}
 $lab=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.PropertiesCustomGroupProbe.addin'
 if(Test-Path -LiteralPath $lab){
  [xml]$registered=Get-Content -LiteralPath $lab -Raw
  if($registered.RevitAddIns.AddIn.AddInId -eq '95BC4886-024F-44E2-A4D3-37EE3B27DC25' -and [IO.Path]::GetFullPath([string]$registered.RevitAddIns.AddIn.Assembly) -eq [IO.Path]::GetFullPath((Join-Path $session 'BIMaestro.PropertiesCustomGroupProbe.dll'))){Remove-Item -LiteralPath $lab}
 }
}
