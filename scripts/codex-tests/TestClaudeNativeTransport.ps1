param([Parameter(Mandatory = $true)][string]$ClaudeExecutable)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path $repoRoot ('tmp/codex-tests/claude-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$claudePath = [IO.Path]::GetFullPath($ClaudeExecutable)
if (!(Test-Path -LiteralPath $claudePath)) { throw 'Pass the official Windows Claude Code executable.' }
$compiler = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$compileJson = Join-Path $repoRoot 'BIMaestro/bin/Release/Newtonsoft.Json.dll'
$runtimeJson = Join-Path $env:USERPROFILE '.nuget/packages/newtonsoft.json/13.0.3/lib/netstandard2.0/Newtonsoft.Json.dll'
$testExe = Join-Path $testRoot 'ClaudeNativeTransportProbe.exe'
& $compiler /nologo /langversion:9 /target:exe "/out:$testExe" "/reference:$compileJson" `
    (Join-Path $repoRoot 'BIMaestro/commands/Codex/ClaudeClient.cs') `
    (Join-Path $repoRoot 'BIMaestro/commands/Codex/CodexToolRecovery.cs') `
    (Join-Path $PSScriptRoot 'ClaudeNativeTransportProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transport test compilation failed.' }
Copy-Item -LiteralPath $runtimeJson -Destination (Join-Path $testRoot 'Newtonsoft.Json.dll')
[IO.File]::WriteAllText((Join-Path $testRoot 'ClaudeNativeTransportProbe.runtimeconfig.json'),
    '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}')
& $claudePath --version
foreach ($targetRuntime in @('48', '8')) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = if ($targetRuntime -eq '8') { (Get-Command dotnet).Source } else { $testExe }
    if ($targetRuntime -eq '8') { $start.ArgumentList.Add($testExe) }
    $start.ArgumentList.Add($claudePath)
    $start.ArgumentList.Add((Join-Path $testRoot "run$targetRuntime"))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Native CLI test exceeded 90 seconds.' }
    Write-Output $stdout.GetAwaiter().GetResult()
    Write-Output $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Native CLI transport failed on runtime $targetRuntime." }
    $process.Dispose()
}
