param([switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package=Join-Path $repo 'tmp/properties-custom-group-tests/groups-ui-2025-final'
$dll=Join-Path $package 'plugin/BIMaestro.dll'
$exe='C:/Program Files/Autodesk/Revit 2025/Revit.exe'
foreach($item in @(Get-Content -LiteralPath (Join-Path $package 'package-hashes.json') -Raw|ConvertFrom-Json)){
 if(!(Test-Path -LiteralPath $item.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw "Compilation absente ou modifiee : $($item.Path)"}
}
$registration=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
[xml]$xml=Get-Content -LiteralPath $registration -Raw
$entry=@($xml.RevitAddIns.AddIn|Where-Object {$_.FullClassName -eq 'BIMaestroApp'})
if($entry.Count -ne 1 -or ([guid]$entry[0].AddInId) -ne [guid]'E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09'){throw 'Manifeste BIMaestro inattendu : aucune modification.'}
if(!(Test-Path -LiteralPath $exe)){throw 'Revit 2025 est absent.'}
if($ValidateOnly){Write-Output 'Compilation et manifeste 2025 verifies. Aucun lancement ni changement de manifeste.';return}
if([IntPtr]::Size -ne 8){throw 'Lancer ce script avec PowerShell 64 bits.'}
$mutex=[Threading.Mutex]::new($false,('Local\BIMaestro.ConfigurableGroupsTest2025.'+$env:USERNAME));$locked=$false
try{
 $locked=$mutex.WaitOne(0);if(!$locked){throw 'Un lancement de test est deja en cours.'}
 $running=@(Get-Process -Name Revit -ErrorAction SilentlyContinue|Where-Object {try{!$_.Path -or [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($exe)}catch{$true}})
 if($running.Count){throw 'Enregistrez vos projets et fermez Revit 2025, puis relancez ce fichier.'}
 $lab=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIMaestro.PropertiesCustomGroupProbe.addin'
 if(Test-Path -LiteralPath $lab){
  [xml]$registered=Get-Content -LiteralPath $lab -Raw
  $boundary=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/properties-custom-group-tests'))+[IO.Path]::DirectorySeparatorChar
  if($registered.RevitAddIns.AddIn.AddInId -ne '95BC4886-024F-44E2-A4D3-37EE3B27DC25' -or !([IO.Path]::GetFullPath([string]$registered.RevitAddIns.AddIn.Assembly)).StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Un laboratoire non reconnu est enregistre ; lancement annule.'}
  Remove-Item -LiteralPath $lab
 }
 $original=[IO.File]::ReadAllBytes($registration)
 $backup=Join-Path $package ('manifest-original-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N')+'.xml')
 [IO.File]::WriteAllBytes($backup,$original);$temporaryHash=$null
 try{
  $entry[0].Assembly=$dll;$xml.Save($registration);$temporaryHash=(Get-FileHash -LiteralPath $registration).Hash
  $process=Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
  $deadline=[DateTime]::UtcNow.AddMinutes(3);$loaded=$false
  while([DateTime]::UtcNow -lt $deadline){
   $process.Refresh();if($process.HasExited){throw 'Revit a ete ferme pendant le chargement.'}
   try{$loaded=@($process.Modules|Where-Object {$_.FileName -and [string]::Equals([IO.Path]::GetFullPath($_.FileName),[IO.Path]::GetFullPath($dll),[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0}catch{$loaded=$false}
   if($loaded){break};Start-Sleep -Milliseconds 500
  }
  if(!$loaded){throw 'La DLL de test chargee ne peut pas etre confirmee. Traitez manuellement toute demande de confiance Revit, puis relancez le test si necessaire.'}
  [pscustomobject]@{PID=$process.Id;DLL=$dll;LoadedAt=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $package ('loaded-'+$process.Id+'.json'))
  Write-Output 'Version de test chargee. Ouvrez votre projet, puis Couleur > Proprietes > Creer un groupe.'
 }finally{
  if($temporaryHash -and (Get-FileHash -LiteralPath $registration).Hash -eq $temporaryHash){[IO.File]::WriteAllBytes($registration,$original)}
  else{Write-Warning "Le manifeste a change. Sa sauvegarde est conservee ici : $backup"}
 }
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
