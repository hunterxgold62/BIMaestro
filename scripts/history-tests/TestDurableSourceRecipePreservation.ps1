#Requires -Version 7.0
<##
Pure contract for the direct-recipe conversion path. Inputs must be real
serialized HistoryRecipe DTOs captured around that conversion. This script does
not implement conversion or claim that the current Revit pipeline passes.
SourceRecipeAfterJson is required to detect in-place mutation of the source.
##>
param(
    [Parameter(Mandatory)][string] $SourceRecipeBeforeJson,
    [Parameter(Mandatory)][string] $SourceRecipeAfterJson,
    [Parameter(Mandatory)][string] $ConvertedRecipeJson,
    [Parameter(Mandatory)][string] $OutputJson
)
$ErrorActionPreference = 'Stop'
function ReadRecipe([string] $path) {
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    if ($value -isnot [Collections.IDictionary] -or $value.Version -ne 1 -or [string]::IsNullOrWhiteSpace($value.Kind)) { throw "Invalid recipe DTO: $path" }
    if ($value.Kind -eq 'native_archive' -or $null -ne $value.Native) { throw "This contract requires a direct primary recipe: $path" }
    return $value
}
function Normalize($value, [string] $parent = '') {
    if ($value -is [Collections.IDictionary]) {
        $normalized = [ordered]@{}
        foreach ($key in @($value.Keys | Sort-Object -CaseSensitive)) {
            # Artifact publication and matched system-definition versions may be
            # added. Every other field, including unknown future geometry fields,
            # remains part of the preservation contract.
            if ($key -in @('Native','OpeningFallback')) { continue }
            if ($parent -in @('Ports','Port','PeerPort') -and $key -eq 'SystemVersion') { continue }
            $normalized[$key] = Normalize $value[$key] $key
        }
        return $normalized
    }
    if ($value -is [Collections.IList]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $value) { $items.Add((Normalize $item $parent)) }
        return ,$items.ToArray()
    }
    return $value
}
function OrderedValue($value) {
    if ($value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($value.Keys | Sort-Object -CaseSensitive)) { $ordered[$key] = OrderedValue $value[$key] }
        return $ordered
    }
    if ($value -is [Collections.IList]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $value) { $items.Add((OrderedValue $item)) }
        return ,$items.ToArray()
    }
    return $value
}
function Canonical($value) { return ConvertTo-Json -InputObject (OrderedValue $value) -Depth 100 -Compress }
function ValidateArtifacts($value, [Collections.Generic.List[string]] $errors) {
    if ($value -is [Collections.IDictionary]) {
        foreach ($key in $value.Keys) {
            if ($key -in @('Native','OpeningFallback') -and $null -ne $value[$key]) {
                $artifact = $value[$key]
                if ($artifact -isnot [Collections.IDictionary] -or $artifact.WholeModel -eq $true -or
                    -not [string]::IsNullOrWhiteSpace($artifact.File) -or
                    $artifact.PayloadId -notmatch '^[a-f0-9]{64}$') { $errors.Add('Converted recipe contains an invalid/non-durable artifact.') }
            }
            ValidateArtifacts $value[$key] $errors
        }
    } elseif ($value -is [Collections.IList]) {
        foreach ($item in $value) { ValidateArtifacts $item $errors }
    }
}
$before = ReadRecipe $SourceRecipeBeforeJson
$after = ReadRecipe $SourceRecipeAfterJson
$converted = ReadRecipe $ConvertedRecipeJson
$errors = [Collections.Generic.List[string]]::new()
if ((Canonical $before) -cne (Canonical $after)) { $errors.Add('Conversion mutated the frozen source recipe.') }
if ((Canonical (Normalize $before)) -cne (Canonical (Normalize $converted))) { $errors.Add('Direct source geometry, placement, parameters or relations changed during conversion.') }
if ($converted.OpeningFallback -isnot [Collections.IDictionary] -or $converted.OpeningFallback.DefinitionOnly -ne $true) { $errors.Add('Converted direct recipe has no definition-only capsule.') }
if ([string]::IsNullOrWhiteSpace($converted.OpeningFallback.TypeVersion)) { $errors.Add('Definition-only capsule has no historical definition version.') }
if ($converted.OpeningFallback.RootSourceUniqueId -cne $converted.Type -or
    [string]::IsNullOrWhiteSpace($converted.OpeningFallback.RootUniqueId)) { $errors.Add('Definition-only capsule does not identify the original primary type.') }
ValidateArtifacts $converted $errors
$result = [ordered]@{
    Passed = $errors.Count -eq 0
    Failed = $errors.Count -gt 0
    Scope = 'Pure DTO preservation contract only; native payload integrity, definition completeness and Revit restoration are separate checks.'
    Errors = $errors.ToArray()
    SourceBefore = [IO.Path]::GetFullPath($SourceRecipeBeforeJson)
    SourceAfter = [IO.Path]::GetFullPath($SourceRecipeAfterJson)
    Converted = [IO.Path]::GetFullPath($ConvertedRecipeJson)
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputJson))
$null = [IO.Directory]::CreateDirectory($parent)
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputJson -Encoding utf8
if ($errors.Count -gt 0) { throw "Direct recipe preservation failed: $($errors -join ' ')" }
Write-Output 'PASS: direct recipe DTO preserved; this is not a Revit restoration/durability verdict.'
