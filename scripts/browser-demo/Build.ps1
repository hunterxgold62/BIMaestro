param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunFolder = 'browser-icons-variety')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('tmp/' + $RunFolder)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assembly = Join-Path $output 'BIMaestro.BrowserDemo.dll'
$api = 'C:/Program Files/Autodesk/Revit 2024'
$wpf = 'C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe' /nologo /langversion:9 /target:library "/out:$assembly" "/reference:$api/RevitAPI.dll" "/reference:$api/RevitAPIUI.dll" "/reference:$wpf/PresentationCore.dll" "/reference:$wpf/WindowsBase.dll" (Join-Path $PSScriptRoot 'BrowserDemoApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée' }
$manifest = '<RevitAddIns><AddIn Type="Application"><Name>BIMaestro browser demo</Name><Assembly>' + [Security.SecurityElement]::Escape($assembly) + '</Assembly><AddInId>5BC9A3E5-2F16-42C2-8490-D85C0F95607F</AddInId><FullClassName>BrowserDemoApp</FullClassName><VendorId>BMTS</VendorId></AddIn></RevitAddIns>'
[IO.File]::WriteAllText((Join-Path $output 'ZZ.BIMaestro.BrowserDemo.addin'), $manifest)
Write-Output $output
