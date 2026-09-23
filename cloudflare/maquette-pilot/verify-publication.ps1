param(
  [Parameter(Mandatory = $true)][string]$PublicationId,
  [Parameter(Mandatory = $true)][int]$Revision,
  [Parameter(Mandatory = $true)][string]$ManifestBase64,
  [string]$Bucket = 'bimaestro-maquettes'
)

$ErrorActionPreference = 'Stop'
$wrangler = Join-Path $PSScriptRoot '..\..\viewer-web\node_modules\.bin\wrangler.cmd'
$assets = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ManifestBase64)) | ConvertFrom-Json
$localFile = Join-Path ([IO.Path]::GetTempPath()) ('bimaestro-r2-verify-' + [guid]::NewGuid().ToString('N'))
$total = 0L
try {
  foreach ($asset in $assets) {
    if ($asset.name -notmatch '^(index\.zip|tile-\d{5}\.glb\.gz)$' -or $asset.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Manifeste invalide' }
    & $wrangler r2 object get "$Bucket/$PublicationId/$Revision/$($asset.name)" --file $localFile --remote | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Objet R2 absent : $($asset.name)" }
    $size = (Get-Item -LiteralPath $localFile).Length
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $localFile).Hash.ToLowerInvariant()
    if ($size -ne $asset.bytes -or $hash -ne $asset.sha256) { throw "Copie R2 incorrecte : $($asset.name)" }
    $total += $size
    Remove-Item -LiteralPath $localFile
  }
  Write-Output "R2 VERIFIED $($assets.Count) fichiers, $total octets, $Bucket/$PublicationId/$Revision"
} finally { Remove-Item -LiteralPath $localFile -ErrorAction SilentlyContinue }
