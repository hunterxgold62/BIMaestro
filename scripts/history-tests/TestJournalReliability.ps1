#Requires -Version 7.0
# Current production uses a durable local spool before shared delivery.
# Keep the established command as the entry point for its production-method fixture.
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'TestJournalSpoolReliability.ps1')
