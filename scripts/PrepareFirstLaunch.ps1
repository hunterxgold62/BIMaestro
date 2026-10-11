param([switch]$WelcomeOnly)
$ErrorActionPreference = 'Stop'
if (@(Get-Process Revit -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Fermer Revit avant de réinitialiser les marqueurs : une instance ouverte pourrait les réécrire.'
}
$documentsPath = [Environment]::GetFolderPath('MyDocuments')
$roamingPath = [Environment]::GetFolderPath('ApplicationData')
$localPath = [Environment]::GetFolderPath('LocalApplicationData')
$targets = @(
    (Join-Path $documentsPath 'RevitLogs/License/welcome_state.json'),
    (Join-Path $documentsPath 'RevitLogs/License/support_prompt_state.json'),
    (Join-Path $documentsPath 'RevitLogs/License/button_update_notes_seen.json'),
    (Join-Path $documentsPath 'RevitLogs/SauvegardePréférence/AppearanceGuideFirstLaunch.txt'),
    (Join-Path $roamingPath 'BIMaestro/update_notifier_state.json')
)
if ($WelcomeOnly) { $targets = @($targets | Where-Object { [IO.Path]::GetFileName($_) -eq 'welcome_state.json' }) }
$backupRoot = [IO.Path]::GetFullPath((Join-Path $localPath 'BIMaestro/Backups/FirstLaunch'))
$backup = [IO.Path]::GetFullPath((Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))))
if (!$backup.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination de sauvegarde invalide.'
}
$moved = @()
try {
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($target in $targets) {
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $saved = Join-Path $backup ([IO.Path]::GetFileName($target))
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        $profileReset = [IO.Path]::GetFileName($target) -eq 'welcome_state.json'
        if ($profileReset) { Copy-Item -LiteralPath $target -Destination $saved }
        else { Move-Item -LiteralPath $target -Destination $saved }
        $moved += [pscustomobject]@{ Original = $target; Backup = $saved; Sha256 = $hash; ResetInPlace = $profileReset }
        if ((!$profileReset -and (Test-Path -LiteralPath $target)) -or (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -ne $hash) {
            throw 'La sauvegarde du marqueur ne correspond pas au fichier initial.'
        }
        if ($profileReset) {
            # Keep identity and already supplied profile data when replaying onboarding.
            $state = Get-Content -LiteralPath $saved -Raw | ConvertFrom-Json -AsHashtable
            foreach ($name in @('WelcomeShown','HardDismissed','TutorialOfferCompleted')) { $state[$name] = $false }
            foreach ($name in @('FirstCommandUtc','SnoozeUntilUtc','LastAttemptUtc','LastWelcomePromptVersion','LastProfilePromptRevision','TutorialSnoozeUntilUtc')) { $state[$name] = $null }
            $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $target -Encoding UTF8
        }
    }
    $moved | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backup 'restore-map.json') -Encoding UTF8
} catch {
    foreach ($entry in $moved) {
        if ($entry.ResetInPlace) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Original -Force }
        elseif (!(Test-Path -LiteralPath $entry.Original) -and (Test-Path -LiteralPath $entry.Backup)) {
            Move-Item -LiteralPath $entry.Backup -Destination $entry.Original
        }
    }
    throw
}
[pscustomobject]@{ Backup = $backup; ResetMarkers = @($moved | ForEach-Object { [IO.Path]::GetFileName($_.Original) }); RevitRunning = $false } | ConvertTo-Json -Depth 3
