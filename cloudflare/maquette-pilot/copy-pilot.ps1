param(
  [Parameter(Mandatory = $true)][string]$Token
)

$ErrorActionPreference = 'Stop'
$publicationId = '5b1d1220-2b0c-4194-8b4e-ea18db63f73d'
$revision = 2
$endpoint = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share'
$apiKey = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo'
$wrangler = Join-Path $PSScriptRoot '..\..\viewer-web\node_modules\.bin\wrangler.cmd'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('bimaestro-r2-antibes-pilot-' + [guid]::NewGuid().ToString('N'))

function Request-Share([hashtable]$body) {
  Invoke-RestMethod -Uri $endpoint -Method Post -ContentType 'application/json' -Headers @{ apikey = $apiKey } -Body ($body | ConvertTo-Json -Compress -Depth 8)
}

if (-not (Test-Path -LiteralPath $wrangler)) { throw 'Wrangler est introuvable.' }
New-Item -ItemType Directory -Path $directory | Out-Null

try {
  $share = Request-Share @{ action = 'resolve'; token = $Token }
  if ($share.publication.id -ne $publicationId -or $share.publication.revision -ne $revision) { throw 'Le lien ne correspond pas à la publication pilote attendue.' }
  $assets = @($share.manifest.assets)
  if ($assets.Count -ne 15 -or ($assets | Measure-Object -Property bytes -Sum).Sum -ne 16633642) { throw 'Le manifeste de la publication pilote a changé.' }
  $tiles = Request-Share @{ action = 'tile'; token = $Token; revision = $revision; names = @($assets | Where-Object name -ne 'index.zip' | ForEach-Object name) }
  $urls = @{}
  $urls['index.zip'] = $share.packageUrl
  foreach ($tile in $tiles.tiles) { $urls[$tile.name] = $tile.url }
  foreach ($asset in $assets) {
    if ($asset.name -notmatch '^(index\.zip|tile-\d{5}\.glb\.gz)$' -or -not $urls[$asset.name]) { throw 'Fichier inattendu dans le manifeste.' }
    $localFile = Join-Path $directory $asset.name
    Invoke-WebRequest -Uri $urls[$asset.name] -OutFile $localFile
    $actualSize = (Get-Item -LiteralPath $localFile).Length
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $localFile).Hash.ToLowerInvariant()
    if ($actualSize -ne $asset.bytes -or $actualHash -ne $asset.sha256) { throw "Intégrité incorrecte : $($asset.name)" }
    & $wrangler r2 object put "bimaestro-maquettes-test/$publicationId/$revision/$($asset.name)" --file $localFile --content-type application/octet-stream --remote
    if ($LASTEXITCODE -ne 0) { throw "Envoi R2 échoué : $($asset.name)" }
    Write-Output "Copie vérifiée : $($asset.name) ($actualSize octets)"
  }
} finally {
  foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $file.FullName }
  if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory }
}
