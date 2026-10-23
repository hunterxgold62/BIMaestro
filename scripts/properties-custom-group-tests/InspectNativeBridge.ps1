param([string]$OutputTag='-r1')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo 'tmp/properties-custom-group-tests/native-bridge-evidence'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$dumpbin='C:/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/dumpbin.exe'
$api='C:/Program Files/Autodesk/Revit 2025'
$descriptions=@(
 @{Name='DesktopMFC.dll';Filter='addGroupHeader@ElemGrid|addGroupHeaderCommon@ElemGrid|isCellPartOfAGroupHeader@ElemGrid|UpdateInsertRows@ElemGrid|UpdateRemoveRows@ElemGrid|findParamIdRow@ElemGrid';Ranges=@('0x180342360,0x180342450','0x180342450,0x1803429c2','0x180347c10,0x180347ce3')},
 @{Name='Utility.dll';Filter='\?\?0AString@@QEAA@PEB_W|\?\?1AString@@';Ranges=@('0x1801dd7d0,0x1801dd838')},
 @{Name='og1100asu.dll';Filter='InsertRows@CGXGridCore|RemoveRows@CGXGridCore|SetValueRange@CGXGridCore|SetStyleRange@CGXGridCore|ResetCurrentCell@CGXGridCore';Ranges=@('0x1800ae530,0x1800ae5e8','0x1800accc0,0x1800acdad')}
)
$modules=foreach($description in $descriptions){
 $path=Join-Path $api $description.Name
 $exports=@(& $dumpbin /exports $path | Select-String -Pattern $description.Filter | ForEach-Object {$_.Line.Trim()})
 if($LASTEXITCODE -ne 0){throw "Could not inspect exports: $path"}
 $disassembly=foreach($range in $description.Ranges){
  & $dumpbin /disasm:nobytes "/range:$range" $path
  if($LASTEXITCODE -ne 0){throw "Could not inspect instructions: $path / $range"}
 }
 $disassemblyPath=Join-Path $out ($description.Name+'.disassembly.txt')
 [IO.File]::WriteAllLines($disassemblyPath,[string[]]$disassembly)
 $file=Get-Item -LiteralPath $path
 [pscustomobject]@{Name=$description.Name;Path=$path;Hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;FileVersion=$file.VersionInfo.FileVersion;Exports=$exports;Ranges=$description.Ranges;Disassembly=$disassemblyPath}
}
$labAssembly=Join-Path $repo "tmp/properties-custom-group-tests/2025$OutputTag/BIMaestro.PropertiesCustomGroupProbe.dll"
$record=[pscustomobject]@{
 InspectedAtUtc=[DateTime]::UtcNow.ToString('O');Scope='Read-only installed binary inspection; not a runtime success claim.'
 RevitVersion=2025;Modules=@($modules)
 Mfc=[pscustomobject]@{Path='C:/Windows/System32/mfc140u.dll';Hash=(Get-FileHash -LiteralPath 'C:/Windows/System32/mfc140u.dll' -Algorithm SHA256).Hash;FromHandleRva='0x2957c0'}
 LabAssembly=[pscustomobject]@{Path=$labAssembly;Hash=(Get-FileHash -LiteralPath $labAssembly -Algorithm SHA256).Hash}
 Findings=@(
  'addGroupHeader(ElemGrid*, int*, const AString&) text overload passes an empty optional<ForgeTypeId> to addGroupHeaderCommon.',
  'addGroupHeaderCommon increments the row cursor twice: spacer at the supplied row, then header at supplied row+1. No InsertRows call is present in the inspected body.',
  'The factory constructs native CGXStyle objects and writes title column1 and marker column3. Header recognition is tested separately through isCellPartOfAGroupHeader.',
  'Utility AString(wchar_t const*) initializes one pointer-sized slot; allocation/destruction are performed through the matching exports of the exact hashed Utility binary.',
  'An empty ForgeTypeId may limit collapse-state persistence; only runtime tests can establish collapse behavior. No custom native group is qualified by this inspection.'
 )
}
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'native-bridge-2025.json')
Write-Output (Join-Path $out 'native-bridge-2025.json')
