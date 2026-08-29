param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$DownloadUrl,

    [string]$OutputPath = ".\update.json"
)

$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$parsedVersion = [Version]::new()
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Version invalide : $Version"
}

$uri = [Uri]$DownloadUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "https") {
    throw "DownloadUrl doit etre une adresse HTTPS absolue."
}

$manifest = [ordered]@{
    version     = $parsedVersion.ToString()
    downloadUrl = $uri.AbsoluteUri
    sha256      = (Get-FileHash -LiteralPath $resolvedInstaller -Algorithm SHA256).Hash
}

$manifest | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Manifeste cree : $((Resolve-Path -LiteralPath $OutputPath).Path)"
Write-Host "Publiez ce fichier a l'adresse https://www.bimaestro.fr/update.json"
