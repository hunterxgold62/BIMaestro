param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
    [Parameter(Mandatory)][ValidateSet('mixed','latest','network')][string]$Fixture,
    [switch]$Capsule,
    [switch]$Enqueue
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$service=Join-Path $repo 'tmp/history-native-service'
$source=Join-Path $repo (@{mixed='tmp/history-native-mixed-v5';latest='tmp/history-native-latest';network='tmp/history-native-network-v2'}[$Fixture])
$case=Join-Path $service ('cases/'+$Name)
if(Test-Path -LiteralPath $case){throw 'Test case already exists.'}
$null=New-Item -ItemType Directory -Path (Join-Path $case 'product')
foreach($file in @('opening.rvt','ids.json')){Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $case $file)}
$product=Join-Path $case 'product/BIMaestro.dll'
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/bin/Revit2025/Debug/BIMaestro.dll') -Destination $product
[IO.File]::WriteAllText((Join-Path $case 'product-path.txt'),$product)
[IO.File]::WriteAllText((Join-Path $case 'native-test.txt'),'yes')
if($Fixture-ne'network'){[IO.File]::WriteAllText((Join-Path $case 'mixed-test.txt'),'yes')}
if($Capsule){[IO.File]::WriteAllText((Join-Path $case 'capsule-test.txt'),'yes')}
[IO.File]::WriteAllText((Join-Path $case 'inputs.json'),(@{
    ProductSha256=(Get-FileHash -LiteralPath $product).Hash
    SourceSha256=(Get-FileHash -LiteralPath (Join-Path $case 'opening.rvt')).Hash
    Fixture=$Fixture;Capsule=[bool]$Capsule;CreatedUtc=[DateTime]::UtcNow.ToString('O')
}|ConvertTo-Json))
if($Enqueue){
    $command=Join-Path $service 'command.txt'
    if(Test-Path -LiteralPath $command){throw 'Another test case is queued.'}
    [IO.File]::WriteAllText($command,$Name)
}
Write-Output $case
