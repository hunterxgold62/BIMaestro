param([switch]$RealCodex)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testOutput = Join-Path $repoRoot 'tmp/codex-tests/bin'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compiler = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$jsonAssembly = Join-Path $repoRoot 'BIMaestro/bin/Release/Newtonsoft.Json.dll'
if (!(Test-Path -LiteralPath $jsonAssembly)) { throw 'Compiler BIMaestro en Release avant ce test.' }
$testExe = Join-Path $testOutput 'CodexProtocolTests.exe'
& $compiler /nologo /langversion:9 /target:exe "/out:$testExe" "/reference:$jsonAssembly" (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexClient.cs') (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexShapeBatch.cs') (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexFamilyDesign.cs') (Join-Path $PSScriptRoot 'FamilyDesignTests.cs') (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation des tests échouée.' }
Copy-Item -LiteralPath $jsonAssembly -Destination $testOutput -Force
$referenceRoot = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$referenceArgs = @('PresentationCore','PresentationFramework','WindowsBase','System.Xaml') | ForEach-Object { '/reference:' + (Join-Path $referenceRoot ($_ + '.dll')) }
$windowTestExe = Join-Path $testOutput 'CodexWindowTests.exe'
& $compiler /nologo /langversion:9 /target:exe "/out:$windowTestExe" "/reference:$jsonAssembly" @referenceArgs (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexClient.cs') (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexImageAttachment.cs') (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexWindow.cs') (Join-Path $PSScriptRoot 'WindowTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation des tests interface échouée.' }
Push-Location $repoRoot
try {
    # Normalize duplicate PATH/Path entries emitted by some terminal hosts before
    # starting the .NET Framework test program (ProcessStartInfo rejects duplicates).
    $testStart = [Diagnostics.ProcessStartInfo]::new($testExe)
    $testStart.UseShellExecute = $false
    $testStart.CreateNoWindow = $true
    $testStart.RedirectStandardOutput = $true
    $testStart.RedirectStandardError = $true
    $testStart.Environment.Clear()
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) { $testStart.Environment[$entry.Key] = $entry.Value }
    if ($RealCodex) { $testStart.ArgumentList.Add((Get-Command codex.exe -ErrorAction Stop).Source) }
    $testProcess = [Diagnostics.Process]::Start($testStart)
    $testStdout = $testProcess.StandardOutput.ReadToEndAsync()
    $testStderr = $testProcess.StandardError.ReadToEndAsync()
    $testProcess.WaitForExit()
    Write-Output $testStdout.GetAwaiter().GetResult()
    Write-Output $testStderr.GetAwaiter().GetResult()
    if ($testProcess.ExitCode -ne 0) { throw 'Tests Codex échoués.' }
    $testStart.FileName = $windowTestExe
    $testStart.ArgumentList.Clear()
    $testProcess = [Diagnostics.Process]::Start($testStart)
    $testStdout = $testProcess.StandardOutput.ReadToEndAsync()
    $testStderr = $testProcess.StandardError.ReadToEndAsync()
    $testProcess.WaitForExit()
    Write-Output $testStdout.GetAwaiter().GetResult()
    Write-Output $testStderr.GetAwaiter().GetResult()
    if ($testProcess.ExitCode -ne 0) { throw 'Tests interface Codex échoués.' }
} finally { Pop-Location }
