param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion='2026',[ValidateSet('1','2','3')][string]$Generation='1',[switch]$Restore)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$folder=Join-Path $repo "tmp/home-tests/$RevitVersion"
$modern=[int]$RevitVersion -ge 2025
$name=if($modern){'BIMaestroHomeConceptModern'}else{'BIMaestroHomeConceptLegacy'}
$file=if($modern){'home-concepts-modern.js'}else{'home-concepts-legacy.js'}
if($Generation -eq '2'){$file=$file.Replace('.js','-v2.js')}
if($Generation -eq '3'){$file=$file.Replace('.js','-v3.js')}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $file))
if($modern){$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'home-presets.js'))+"`n"+$source}
$bytes=[Text.Encoding]::UTF8.GetBytes($source)
$hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','')
$body=if($Restore){
 "if(window.$name)window.$name.restore(); window.__bimaestroConceptRevision=null;return JSON.stringify({restored:true});"
}else{
 @"
if(window.__bimaestroConceptRevision==='$hash' && window.$name && window.$name.inspect().concept==='$RevitVersion') return JSON.stringify(window.$name.inspect());
if(window.BIMaestroHomeConceptLegacy){window.BIMaestroHomeConceptLegacy.restore();delete window.BIMaestroHomeConceptLegacy;}
if(window.BIMaestroHomeConceptModern){window.BIMaestroHomeConceptModern.restore();delete window.BIMaestroHomeConceptModern;}
if(window.BIMaestroHomeProbe){window.BIMaestroHomeProbe.restore();delete window.BIMaestroHomeProbe;}
$source
if(!window.$name)return JSON.stringify({applied:false,reason:'Waiting for matching Home document'});
var result=window.$name.apply('$RevitVersion');
if(result.applied)window.__bimaestroConceptRevision='$hash';
return JSON.stringify(result);
"@
}
[IO.File]::WriteAllText((Join-Path $folder 'control.js'),$body)
Write-Output "Home concept $RevitVersion queued (restore=$Restore)"
