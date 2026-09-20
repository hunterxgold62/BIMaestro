param([string]$ProbePath)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$snapshotSource = Get-Content -Raw -LiteralPath (Join-Path $repoPath 'BIMaestro/commands/Codex/CodexCommunitySnapshot.cs')
$testSource = @'
public static class CommunitySnapshotTest
{
    public static long Probe(string path)
    {
        using (var snapshot = BIMaestro.Codex.CodexCommunitySnapshot.Read(path, 20000000)) return snapshot.Length;
    }
    public static void Run(string path)
    {
        var original = new byte[1024];
        original[0] = 42;
        System.IO.File.WriteAllBytes(path, original);
        using (var revitHandle = new System.IO.FileStream(path, System.IO.FileMode.Open,
            System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite))
        {
            bool oldReaderBlocked = false;
            try { using (var old = System.IO.File.OpenRead(path)) { } }
            catch (System.IO.IOException) { oldReaderBlocked = true; }
            if (!oldReaderBlocked) throw new System.Exception("Test did not reproduce the Windows sharing conflict.");
            using (var snapshot = BIMaestro.Codex.CodexCommunitySnapshot.Read(path, 20000000))
            {
                if (snapshot.Length != 1024 || snapshot.ReadByte() != 42) throw new System.Exception("Incorrect snapshot bytes.");
                revitHandle.WriteByte(99); revitHandle.Flush();
                snapshot.Position = 0;
                if (snapshot.ReadByte() != 42) throw new System.Exception("Snapshot changed during a later save.");
            }
        }
        bool tooLarge = false;
        try { using (var snapshot = BIMaestro.Codex.CodexCommunitySnapshot.Read(path, 512)) { } }
        catch (System.InvalidOperationException) { tooLarge = true; }
        if (!tooLarge) throw new System.Exception("Oversize file accepted.");
        using (var exclusive = new System.IO.FileStream(path, System.IO.FileMode.Open,
            System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
        {
            bool blocked = false;
            try { using (var snapshot = BIMaestro.Codex.CodexCommunitySnapshot.Read(path, 20000000)) { } }
            catch (System.IO.IOException) { blocked = true; }
            if (!blocked) throw new System.Exception("Exclusive lock was ignored.");
        }
    }
}
'@
Add-Type -TypeDefinition ($snapshotSource + [Environment]::NewLine + $testSource)
$testFolder = Join-Path $repoPath 'tmp/community-snapshot-tests'
New-Item -ItemType Directory -Path $testFolder -Force | Out-Null
$testFile = Join-Path $testFolder ([Guid]::NewGuid().ToString('N') + '.bin')
try {
    [CommunitySnapshotTest]::Run($testFile)
    Write-Output 'PASS: Windows sharing conflict reproduced, shared snapshot succeeds, saved bytes remain stable, oversized and exclusively locked files refused.'
    if ($ProbePath) { Write-Output ('Saved RFA read successfully: ' + [CommunitySnapshotTest]::Probe($ProbePath) + ' bytes. No upload performed.') }
} finally {
    if (Test-Path -LiteralPath $testFile) { Remove-Item -LiteralPath $testFile }
}
