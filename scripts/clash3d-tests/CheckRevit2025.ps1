param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName='clash-net8',
 [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$SourceRunName='clash-release', [string]$ReservationAssembly='')
$ErrorActionPreference='Stop'
# Keep SourceRunName for older callers; the 2025 harness now compiles production sources directly.
& (Join-Path $PSScriptRoot 'Build.ps1') -RevitVersion 2025 -RunName $RunName -ReservationAssembly $ReservationAssembly
