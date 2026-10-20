param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$relative=switch($RevitVersion){'2023'{'BIMaestro/bin/Debug'} '2024'{'BIMaestro/bin/Release2024'} default{"BIMaestro/bin/Revit$RevitVersion/Release"}}
$source=Join-Path $repo $relative
$dll=Join-Path $source 'BIMaestro.dll'
if(!(Test-Path -LiteralPath $dll)){throw 'Build this Revit version first.'}
$hash=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$binRoot=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIMaestro/Bin'))
$destination=[IO.Path]::GetFullPath((Join-Path $binRoot ('Revit'+$RevitVersion+'-HomeTest-'+$hash.Substring(0,12))))
if(!$destination.StartsWith($binRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid destination.'}
$manifest=Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitVersion/BIMaestro.addin"
[xml]$xml=Get-Content -LiteralPath $manifest -Raw
$entry=@($xml.RevitAddIns.AddIn|Where-Object FullClassName -eq 'BIMaestroApp')
if($entry.Count -ne 1){throw 'Unexpected manifest.'}
New-Item -ItemType Directory -Path $destination -Force|Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
$installed=Join-Path $destination 'BIMaestro.dll'
if((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $hash){throw 'Invalid installed hash.'}
$backup=$manifest+'.before-home-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.bak'
Copy-Item -LiteralPath $manifest -Destination $backup
$entry[0].Assembly=$installed
$xml.Save($manifest)
[xml]$verified=Get-Content -LiteralPath $manifest -Raw
if([string]$verified.RevitAddIns.AddIn.Assembly -ne $installed){throw 'Registration verification failed.'}
[pscustomobject]@{Revit=$RevitVersion;Assembly=$installed;SHA256=$hash;Backup=$backup}|ConvertTo-Json
