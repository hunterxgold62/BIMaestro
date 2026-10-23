param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedProductSha256,
    [Parameter(Mandatory)][string]$PreviousAssembly,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PreviousSha256
)
$ErrorActionPreference = 'Stop'
$manifest = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk/Revit/Addins/2025/BIMaestro.addin'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run = Join-Path $repo ('tmp/history-stress/' + $RunName)
if ((Get-FileHash -LiteralPath $PreviousAssembly -Algorithm SHA256).Hash -ne $PreviousSha256) { throw 'Previous version changed; refusing launch.' }
$before = [Xml.XmlDocument]::new(); $before.PreserveWhitespace=$true; $before.XmlResolver=$null; $before.Load($manifest)
$nodes = @($before.SelectNodes("/RevitAddIns/AddIn[FullClassName='BIMaestroApp']/Assembly"))
if($nodes.Count -ne 1 -or (Get-FileHash -LiteralPath $nodes[0].InnerText -Algorithm SHA256).Hash -ne $ExpectedProductSha256) { throw 'Registration does not match the isolated test build.' }
$testAssembly = $nodes[0].InnerText
try {
    & (Join-Path $PSScriptRoot 'Launch.ps1') -RunName $RunName -OpenWhenReady -ExpectedProductSha256 $ExpectedProductSha256
} finally {
    $xml = [Xml.XmlDocument]::new(); $xml.PreserveWhitespace=$true; $xml.XmlResolver=$null; $xml.Load($manifest)
    $current = @($xml.SelectNodes("/RevitAddIns/AddIn[FullClassName='BIMaestroApp']/Assembly"))
    if($current.Count -ne 1 -or $current[0].InnerText -cne $testAssembly) { throw 'Registration changed during test launch; automatic restoration refused.' }
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    $current[0].InnerText = $PreviousAssembly
    $temp = $manifest + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $xml.Save($temp)
        if((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestHash) { throw 'Concurrent registration update; restoration refused.' }
        [IO.File]::Replace($temp,$manifest,$manifest+'.before-durable-test-restore-'+[guid]::NewGuid().ToString('N')+'.bak')
    } finally { if(Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    [ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');TestAssembly=$testAssembly;DefaultAssemblyRestored=$PreviousAssembly;PreviousSha256=$PreviousSha256;RunningTestInstanceUnaffected=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'default-registration-restored.json') -Encoding UTF8
}
