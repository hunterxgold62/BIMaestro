#Requires -Version 7.0
# Frozen-definition reuse is no longer the nominal capture contract. Existing
# archive readers retain their separate compatibility tests. Exercise compact
# instance persistence and existing-model definition references instead.
& (Join-Path $PSScriptRoot 'TestCompactDirectPersistence.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Compact direct persistence regression failed.' }