param([Parameter(Mandatory)][string]$BackupFile)
$ErrorActionPreference = 'Stop'
if (@(Get-Process Revit -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Fermer Revit avant de restaurer les informations du profil.' }
$current = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RevitLogs/License/welcome_state.json'
$old = Get-Content -LiteralPath $BackupFile -Raw | ConvertFrom-Json -AsHashtable
$state = if (Test-Path -LiteralPath $current) { Get-Content -LiteralPath $current -Raw | ConvertFrom-Json -AsHashtable } else { @{ SchemaVersion = 1; InstallId = $old.InstallId } }
if ($state.Email -and $old.Email -and $state.Email -ne $old.Email) { throw 'Le profil courant appartient à une autre adresse. Aucune information remplacée.' }
$restored = @()
foreach ($name in @('Email','FirstName','LastName','Company')) {
    if ([string]::IsNullOrWhiteSpace($state[$name]) -and ![string]::IsNullOrWhiteSpace($old[$name])) {
        $state[$name] = $old[$name]; $restored += $name
        if ($name -eq 'Email') { $state.EmailOptIn = $old.EmailOptIn; $state.OptInUtc = $old.OptInUtc }
    }
}
if ($restored.Count -gt 0) {
    if (Test-Path -LiteralPath $current) { Copy-Item -LiteralPath $current -Destination ($current + '.before-profile-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak') }
    $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $current -Encoding UTF8
    $verified = Get-Content -LiteralPath $current -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in $restored) { if ($verified[$name] -ne $old[$name]) { throw 'Vérification du profil restauré échouée.' } }
}
[pscustomobject]@{ RestoredFields = $restored; HasEmail = ![string]::IsNullOrWhiteSpace($state.Email); HasFirstName = ![string]::IsNullOrWhiteSpace($state.FirstName); HasLastName = ![string]::IsNullOrWhiteSpace($state.LastName) } | ConvertTo-Json
