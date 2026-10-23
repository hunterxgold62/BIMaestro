param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName,
    [Parameter(Mandatory)][string]$ProductDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$product=[IO.Path]::GetFullPath((Join-Path $repo $ProductDirectory))
$boundary=[IO.Path]::GetFullPath((Join-Path $repo 'tmp/history-stress')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(!$product.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Product outside isolated test workspace.'}
$dll=Join-Path $product 'BIMaestro.dll'
if((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $ExpectedSha256){throw 'Compiled product hash differs.'}
$run=Join-Path $repo ('tmp/history-stress/'+$RunName)
if(!(Test-Path -LiteralPath (Join-Path $run 'compiled-evidence.json'))){throw 'Prepare the exact isolated run first.'}
$manifest=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
$xml=New-Object Xml.XmlDocument
$xml.PreserveWhitespace=$true; $xml.XmlResolver=$null; $xml.Load($manifest)
$nodes=@($xml.SelectNodes("/RevitAddIns/AddIn[FullClassName='BIMaestroApp']/Assembly"))
if($nodes.Count -ne 1){throw 'Application registration is ambiguous.'}
$previous=$nodes[0].InnerText
$previousSha=(Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash
if([IO.Path]::GetFullPath($previous) -eq $dll){throw 'Test registration already active; preserve and inspect before relaunch.'}
$manifestSha=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
$nodes[0].InnerText=$dll
$temporary=$manifest+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
try {
    $xml.Save($temporary)
    if((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestSha){throw 'Another task changed registration; no replacement.'}
    [IO.File]::Replace($temporary,$manifest,$manifest+'.before-history-diag-'+[guid]::NewGuid().ToString('N')+'.bak')
} finally { if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary} }
[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');Product=$dll;Sha256=$ExpectedSha256;PreviousAssembly=$previous;PreviousSha256=$previousSha;Executable='C:/Program Files/Autodesk/Revit 2025/Revit.exe'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'diagnostic-registration.json') -Encoding UTF8
# These are fixed reviewed scripts. No generated code or installer is executed.
& (Join-Path $PSScriptRoot 'LaunchIsolatedDurable.ps1') -RunName $RunName -ExpectedProductSha256 $ExpectedSha256 -PreviousAssembly $previous -PreviousSha256 $previousSha
