#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$source=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryNativeArchive.Durable.cs'))
function Extract([string]$signature){$start=$source.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw "Missing production method: $signature"};$opening=$source.IndexOf('{',$start);$depth=0;for($i=$opening;$i -lt $source.Length;$i++){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}};throw 'Unterminated production method.'}
$methods=@('internal static void RestoreFrozenTypeIdentity(Element frozen, Element target)','internal static void ValidateFrozenTypeIdentity(Element frozen, Element target)','private static string FrozenTypeIdentityDiagnostic(Element element, Parameter parameter)','private static bool ReadFrozenTypeIdentityParameters(Element frozen, Element target, out Parameter source, out Parameter copy)','private static void RestoreFrozenTypeIdentities(Document capsule, Dictionary<string, Element> originals, HashSet<ElementId> templateIds)','private static void ValidateFrozenTypeIdentities(Document capsule, Dictionary<string, Element> originals, HashSet<ElementId> templateIds)','private static bool SameLiveDocument(Document first, Document second)')|ForEach-Object{Extract $_}
$direct=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryNativeArchive.Definitions.cs'))
foreach($export in @($source,$direct)){
 $restore=$export.IndexOf('RestoreFrozenTypeIdentities(capsule,',[StringComparison]::Ordinal)
 $rename=$export.IndexOf('type.Name = VersionedDefinitionName(', [StringComparison]::Ordinal)
 if($restore -lt 0 -or $rename -lt 0 -or $restore -lt $rename){throw 'Type identity must be restored after the exporter finishes renaming types.'}
 $commit=$export.IndexOf('CommitDurableTransaction(tx);',$restore,[StringComparison]::Ordinal)
 $save=$export.IndexOf('capsule.SaveAs(outputRvt, options);',$commit,[StringComparison]::Ordinal)
 if($commit -lt $restore -or $save -lt $commit -or $export.IndexOf('ValidateFrozenTypeIdentities(capsule,',$save,[StringComparison]::Ordinal) -lt $save){throw 'Commit/SaveAs persisted type identity validation missing.'}
}
$fixture=@'
using System;using System.Collections.Generic;using System.Linq;
namespace Autodesk.Revit.Exceptions{public class InvalidObjectException:Exception{}}
namespace Analyse {
internal enum BuiltInParameter{IFC_TYPE_GUID=-1019001}internal enum StorageType{None,String,Integer}
internal sealed class ElementId{internal int Value;internal long GetIdLongValue()=>Value;public override bool Equals(object other)=>other is ElementId id&&Value==id.Value;public override int GetHashCode()=>Value;}
internal sealed class Parameter{internal string Value;internal int Writes;internal bool Refuse,Ignore;internal bool IsReadOnly;internal ElementId Id=new ElementId{Value=-1019001};internal StorageType StorageType=StorageType.String;internal bool HasValue=>Value!=null;internal string AsString()=>Value;internal bool Set(string value){Writes++;if(Refuse)return false;if(!Ignore)Value=value;return true;}}
internal class Element{internal Document Document;internal string UniqueId;internal ElementId Id;internal bool Listed=true;internal Parameter Identity;internal IEnumerable<Parameter> Parameters=>Listed&&Identity!=null?new[]{Identity}:Array.Empty<Parameter>();internal Parameter get_Parameter(BuiltInParameter id){if((int)id!=-1019001)throw new Exception("Wrong IFC builtin");return Identity;}}
internal class ElementType:Element{}internal sealed class WallType:ElementType{}internal sealed class FloorType:ElementType{}
internal sealed class Document{internal bool IsModifiable,IsValidObject=true;internal int Regenerations;internal Action AfterRegenerate;internal readonly Dictionary<string,Element> Elements=new Dictionary<string,Element>();internal Element GetElement(string uid)=>Elements.TryGetValue(uid,out var element)?element:null;internal void Regenerate(){Regenerations++;AfterRegenerate?.Invoke();}}
internal static class ElementHistoryNativeArchive{
'@
$checks=@'
 static int Count;static void Check(bool ok,string why){Count++;if(!ok)throw new Exception(why);}
 static void Reject(Action action,string why){try{action();}catch(InvalidOperationException){Count++;return;}throw new Exception(why);}
 static WallType Type(Document document,string uid,int id,string value){var type=new WallType{Document=document,UniqueId=uid,Id=new ElementId{Value=id},Identity=new Parameter{Value=value}};document.Elements[uid]=type;return type;}
 static int Main(){try{
  var originalDoc=new Document();var capsule=new Document{IsModifiable=true};var original=Type(originalDoc,"original",1,"historical-ifc-guid");var copied=Type(capsule,"copied",2,"generated-guid");
  RestoreFrozenTypeIdentity(original,copied);Check(copied.Identity.Value=="historical-ifc-guid"&&copied.Identity.Writes==1,"Source→capsule identity not restored.");Check(original.Identity.Writes==0&&original.Identity.Value=="historical-ifc-guid","Read-only source was modified.");
  RestoreFrozenTypeIdentity(original,copied);Check(copied.Identity.Writes==1,"Matching identity was unnecessarily rewritten.");
  var destination=new Document{IsModifiable=true};var imported=Type(destination,"imported",3,"generated-on-import");RestoreFrozenTypeIdentity(copied,imported);ValidateFrozenTypeIdentity(original,imported);Check(imported.Identity.Value==original.Identity.Value,"Second copy lost frozen identity.");
  Reject(()=>RestoreFrozenTypeIdentity(original,original),"Original definition must not be edited.");var sameDoc=Type(originalDoc,"same-doc",4,"current");Reject(()=>RestoreFrozenTypeIdentity(original,sameDoc),"Same-document live definition must not be edited.");Check(sameDoc.Identity.Writes==0,"Same-document guard ran after mutation.");
  imported.Identity.Value="readonly-generated";imported.Identity.IsReadOnly=true;Reject(()=>RestoreFrozenTypeIdentity(copied,imported),"Read-only divergence accepted.");imported.Identity.IsReadOnly=false;
  destination.IsModifiable=false;Reject(()=>RestoreFrozenTypeIdentity(copied,imported),"Missing transaction accepted.");destination.IsModifiable=true;
  imported.Identity.Refuse=true;Reject(()=>RestoreFrozenTypeIdentity(copied,imported),"False Set accepted.");imported.Identity.Refuse=false;imported.Identity.Ignore=true;Reject(()=>RestoreFrozenTypeIdentity(copied,imported),"Unpersisted Set accepted.");imported.Identity.Ignore=false;
  imported.Identity.Refuse=true;try{RestoreFrozenTypeIdentity(copied,imported);throw new Exception("Diagnostic fixture did not fail.");}catch(InvalidOperationException error){Check(error.Message.Contains("SetResult=false")&&error.Message.Contains("HasValue=True")&&error.Message.Contains("Text=\"historical-ifc-guid\"")&&error.Message.Contains("Listed=True"),"False Set diagnostic omitted real source/target states.");}imported.Identity.Refuse=false;
  imported.Identity.StorageType=StorageType.Integer;Reject(()=>RestoreFrozenTypeIdentity(copied,imported),"Wrong storage accepted.");imported.Identity.StorageType=StorageType.String;
  var unset=Type(originalDoc,"unset",5,null);Reject(()=>RestoreFrozenTypeIdentity(unset,imported),"Unrepresentable builtin unset must fail explicitly.");
  imported.Identity.Value="";var empty=Type(originalDoc,"empty",6,"");RestoreFrozenTypeIdentity(empty,imported);Check(imported.Identity.Value=="","Defined empty identity was not preserved.");
  var absentSource=Type(originalDoc,"no-param",7,null);absentSource.Identity=null;var absentCopy=Type(capsule,"copy-no-param",8,null);absentCopy.Identity=null;RestoreFrozenTypeIdentity(absentSource,absentCopy);Check(true,"Types without builtin need no invented parameter.");
  var virtualSource=Type(originalDoc,"virtual-source",10,"2pZnxV_ov1uOVe7N8uxAFj");virtualSource.Listed=false;var virtualCopy=Type(capsule,"virtual-copy",11,"3nS74ZO8v30eKzYlspbaDH");virtualCopy.Listed=false;virtualCopy.Identity.Refuse=true;
  RestoreFrozenTypeIdentity(virtualSource,virtualCopy);ValidateFrozenTypeIdentity(virtualSource,virtualCopy);Check(virtualCopy.Identity.Writes==0&&virtualSource.Identity.Writes==0&&virtualCopy.Identity.Value=="3nS74ZO8v30eKzYlspbaDH","Unlisted computed IFC GUID must never be written or treated as a stored definition field.");
  virtualCopy.Listed=true;Reject(()=>RestoreFrozenTypeIdentity(virtualSource,virtualCopy),"Asymmetric stored IFC field must fail.");virtualCopy.Listed=false;virtualSource.Listed=true;Reject(()=>RestoreFrozenTypeIdentity(virtualSource,virtualCopy),"Reverse asymmetric stored IFC field must fail.");
  Reject(()=>RestoreFrozenTypeIdentity(original,new FloorType{Document=destination,UniqueId="floor",Identity=new Parameter{Value="different"}}),"Wrong concrete type accepted.");
  var template=Type(capsule,"template",9,"keep-template-guid");copied.Identity.Value="generated-after-rename";var map=new Dictionary<string,Element>{{copied.UniqueId,original},{template.UniqueId,original}};
  RestoreFrozenTypeIdentities(capsule,map,new HashSet<ElementId>{template.Id});Check(copied.Identity.Value==original.Identity.Value&&capsule.Regenerations==1,"Batch restore did not run after rename/before persistence.");Check(template.Identity.Value=="keep-template-guid"&&template.Identity.Writes==0,"Template/current definition was mutated.");
  copied.Identity.Value="reset-by-commit";Reject(()=>ValidateFrozenTypeIdentities(capsule,map,new HashSet<ElementId>{template.Id}),"Commit/SaveAs rewrite was not detected.");
  RestoreFrozenTypeIdentity(original,copied);capsule.AfterRegenerate=()=>copied.Identity.Value="reset-by-regeneration";Reject(()=>RestoreFrozenTypeIdentities(capsule,map,new HashSet<ElementId>{template.Id}),"Regeneration rewrite accepted.");
  Console.WriteLine("PASS: "+Count+" frozen type IFC identity assertions, plus export rename/commit/SaveAs ordering guards. API adapters only; native replay remains required.");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}}
'@
$root=Join-Path $repo ('tmp/history-frozen-ifc-tests/'+[guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($root)
[IO.File]::WriteAllText((Join-Path $root 'Fixture.cs'),$fixture+($methods -join "`n")+$checks)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project="<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include=`"Newtonsoft.Json`"><HintPath>$([Security.SecurityElement]::Escape($json))</HintPath></Reference><Compile Include=`"Fixture.cs`"/></ItemGroup></Project>"
$projectPath=Join-Path $root 'FrozenIfc.Tests.csproj';[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:NuGetAudit=false -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Frozen IFC identity fixture build failed.'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/FrozenIfc.Tests.dll') | Tee-Object -Variable identityOutput
if($LASTEXITCODE -ne 0){throw "Frozen IFC identity assertions failed: $root"}
$count=[regex]::Match(($identityOutput -join "`n"),'PASS: (\d+)').Groups[1].Value
if(!$count){throw 'Frozen IFC identity test did not produce a verifiable assertion count.'}
@{Passed=$true;Assertions=[int]$count;Builtin='IFC_TYPE_GUID';BuiltinId=-1019001;ExportOrderingChecked=$true;RevitInvoked=$false;NativeReplayValidated=$false;Scope='Extracted production IFC type identity copier/validator and batch filters; API adapters; export rename, commit and SaveAs ordering guarded.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
Write-Output "Results: $root"
