#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.Definitions.cs'))
$start = $source.IndexOf('private static void CopyFrozenDefinitions(', [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Production definition copy method missing.' }
$brace = $source.IndexOf('{', $start); $depth = 0; $method = $null
for ($i = $brace; $i -lt $source.Length; $i++) {
    if ($source[$i] -eq '{') { $depth++ } elseif ($source[$i] -eq '}') { $depth--; if ($depth -eq 0) { $method = $source.Substring($start, $i - $start + 1); break } }
}
if ($null -eq $method) { throw 'Unbalanced production definition copy method.' }
$method = $method.Replace('private static void', 'public static void')
$fixture = @'
using System; using System.Collections.Generic; using System.Linq;
namespace FrozenCopyFixture {
public class Element { public int Id; public string UniqueId,Name; }
public class Family:Element {} public class FamilySymbol:Element {} public class Level:Element {} public class Material:Element {} public class MEPSystemType:Element {} public class PipeType:Element {}
public class Document { public Dictionary<int,Element> Elements=new Dictionary<int,Element>(); public List<Element> Copied=new List<Element>(); public void Regenerate(){} }
public enum TransactionStatus { Started,Committed,RolledBack }
public sealed class SubTransaction:IDisposable {
 Document doc;List<Element> before;TransactionStatus status;
 public SubTransaction(Document d){doc=d;}public void Start(){before=doc.Copied.ToList();status=TransactionStatus.Started;}
 public TransactionStatus Commit(){return status=TransactionStatus.Committed;}public TransactionStatus GetStatus()=>status;
 public void RollBack(){doc.Copied=before.ToList();status=TransactionStatus.RolledBack;}public void Dispose(){if(status==TransactionStatus.Started)RollBack();}
}
public sealed class CopyOptions:IDisposable {public void Dispose(){} }
public sealed class Transform {public static readonly Transform Identity=new Transform();}
public static class ElementTransformUtils {
 public static string FailUid;public static int Calls,MixedCalls;
 public static void CopyElements(Document source,int[] ids,Document destination,Transform transform,CopyOptions options){
  Calls++;if(ids.Length!=1){MixedCalls++;throw new InvalidOperationException("mixed paste rejected");}
  var definition=source.Elements[ids[0]];
  if(definition is FamilySymbol && !destination.Copied.Any(e=>e is Level))throw new InvalidOperationException("missing level context");
  if(definition is PipeType && !destination.Copied.Any(e=>e is MEPSystemType))throw new InvalidOperationException("missing system definition");
  destination.Copied.Add(definition);if(definition.UniqueId==FailUid)throw new InvalidOperationException("injected paste failure");
 }
}
public static class HistoryPerformanceLog {
 public static List<object> Failures=new List<object>();public static void RecordDetails(Document d,string phase,double elapsed,int count,int remaining,bool success,object details){if(!success)Failures.Add(details);}
}
public static class Production {
 static CopyOptions Options()=>new CopyOptions();
__METHOD__
}
public static class Tests {
 static int count;static void Assert(bool ok,string name){count++;if(!ok)throw new Exception(name);}
 public static int Run(){
  var definitions=new Element[]{new FamilySymbol{Id=4,UniqueId="a-symbol",Name="Physical family"},new PipeType{Id=5,UniqueId="b-pipe",Name="Pipe"},new MEPSystemType{Id=3,UniqueId="system",Name="Gas"},new Material{Id=2,UniqueId="material",Name="Material"},new Level{Id=1,UniqueId="level",Name="Level"},new Family{Id=6,UniqueId="family",Name="Physical family"}};
  var source=new Document();foreach(var definition in definitions)source.Elements.Add(definition.Id,definition);var destination=new Document();var stages=new List<string>();
  Production.CopyFrozenDefinitions(source,destination,definitions,new[]{"deleted-instance"},(stage,identity)=>stages.Add(identity));
  Assert(destination.Copied.Select(e=>e.Id).SequenceEqual(new[]{1,2,3,4,5}),"Context, system and physical definitions copied in dependency order");
  Assert(ElementTransformUtils.MixedCalls==0&&ElementTransformUtils.Calls==5,"No mixed CopyElements batch");
  Assert(!destination.Copied.Any(e=>e is Family),"Family container imported only with its symbol");
  Assert(source.Elements.Count==6&&source.Copied.Count==0,"Source definitions left unchanged");
  Assert(stages.Count==5&&stages.Any(value=>value.StartsWith("a-symbol|")),"Each stage identifies exact source UID and class");
  destination=new Document();ElementTransformUtils.FailUid="a-symbol";bool failed=false;
  try{Production.CopyFrozenDefinitions(source,destination,definitions,new[]{"deleted-instance"},(stage,identity)=>{});}catch(InvalidOperationException){failed=true;}
  Assert(failed,"Native paste failure remains explicit");
  Assert(!destination.Copied.Any(e=>e.UniqueId=="a-symbol"||e.UniqueId=="b-pipe"),"Failed paste rolled back and later definitions not attempted");
  Assert(destination.Copied.Count==3,"Successful preceding context stays owned by the caller's atomic outer transaction");
  var failure=HistoryPerformanceLog.Failures.Single();Assert((string)failure.GetType().GetProperty("SourceUid").GetValue(failure)=="a-symbol","Failure evidence names rejected definition");
  return count;
 }
}
}
'@
Add-Type -TypeDefinition ($fixture.Replace('__METHOD__', $method))
[pscustomobject]@{ Passed = $true; Assertions = [FrozenCopyFixture.Tests]::Run(); Scope = 'Extracted production copy orchestration with API doubles; native Revit transfer must be checked in the isolated cycle.' } | ConvertTo-Json -Compress
