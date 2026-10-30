#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-available-dispatch-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.Available.cs') -Destination (Join-Path $output 'Production.cs')
$fixture=@'
using System;using System.Collections.Generic;using System.Linq;
namespace Autodesk.Revit.DB{internal sealed class Document{}}
namespace Autodesk.Revit.Exceptions{internal sealed class RegenerationFailedException:Exception{internal RegenerationFailedException(string message):base(message){}}}
namespace Analyse{
 internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory=>AppContext.BaseDirectory;}
 internal sealed class HistoryNativeArtifact{internal bool DefinitionOnly;}
 internal sealed class HistoryRecipe{internal bool UsesExistingModelDefinitions;internal HistoryNativeArtifact Native;}
 internal sealed class HistoryRestoreRequest{internal string SourceUniqueId;internal HistoryRecipe Recipe;}
 internal sealed class HistoryRestoreBatch{internal int Created,Failed;internal readonly List<string> CaptureWarnings=new List<string>();}
 internal static partial class ElementHistoryRestoration{
  internal static int NativeCalls,CoordinatedCalls,LastCount,RefreshCalls;internal static Exception NativeError,CoordinatedError;internal static HistoryRestoreBatch NativeResult;
  private static HistoryRestoreBatch RestoreNativeCopiesCore(Autodesk.Revit.DB.Document doc,IEnumerable<HistoryRestoreRequest> requests,bool allowPartial)=>RestoreNativeCopies(doc,requests);
  internal static HistoryRestoreBatch RestoreNativeCopies(Autodesk.Revit.DB.Document doc,IEnumerable<HistoryRestoreRequest> requests){NativeCalls++;LastCount=requests.Count();if(NativeError!=null)throw NativeError;return NativeResult;}
  internal static HistoryRestoreBatch Restore(Autodesk.Revit.DB.Document doc,IEnumerable<HistoryRestoreRequest> requests,Action refresh=null){CoordinatedCalls++;var list=requests.ToList();LastCount=list.Count;if(CoordinatedError!=null)throw CoordinatedError;refresh?.Invoke();return new HistoryRestoreBatch{Created=list.Count(x=>x.Recipe!=null),Failed=list.Count(x=>x.Recipe==null)};}
  internal static void Reset(){NativeCalls=CoordinatedCalls=LastCount=RefreshCalls=0;NativeError=CoordinatedError=null;NativeResult=new HistoryRestoreBatch{Created=500};}
 }
 internal static class Checks{
  static int checks;static readonly Autodesk.Revit.DB.Document doc=new Autodesk.Revit.DB.Document();static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
  static HistoryRestoreRequest Direct(string uid)=>new HistoryRestoreRequest{SourceUniqueId=uid,Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};
  static HistoryRestoreRequest Native(string uid)=>new HistoryRestoreRequest{SourceUniqueId=uid,Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact()}};
  static void Reset()=>ElementHistoryRestoration.Reset();static void Refresh()=>ElementHistoryRestoration.RefreshCalls++;
  static Exception Capture(Action action){try{action();return null;}catch(Exception ex){return ex;}}
  static int Main(){try{
   Reset();var batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{Direct("direct-a"),Direct("direct-b")},Refresh);Check(ElementHistoryRestoration.NativeCalls==0&&ElementHistoryRestoration.CoordinatedCalls==1,"all direct data uses one coordinated restore");Check(batch.Created==2&&batch.Failed==0&&ElementHistoryRestoration.RefreshCalls==1,"direct results and refresh callback retained");
   Reset();batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("native-a"),Native("native-b")},Refresh);Check(ElementHistoryRestoration.NativeCalls==1&&ElementHistoryRestoration.CoordinatedCalls==0,"all native data prefers one complete clipboard set");Check(ReferenceEquals(batch,ElementHistoryRestoration.NativeResult)&&batch.CaptureWarnings.Count==0,"successful native result returned unchanged");
   Reset();var nativeError=new InvalidOperationException("native-rejected-fixture");ElementHistoryRestoration.NativeError=nativeError;var many=Enumerable.Range(0,500).Select(i=>i<2?new HistoryRestoreRequest{SourceUniqueId="failed-"+i}:Native("native-"+i)).ToList();var validNative=many.Where(x=>x.Recipe!=null).ToList();batch=ElementHistoryRestoration.RestoreAvailable(doc,validNative,Refresh);Check(ElementHistoryRestoration.NativeCalls==1&&ElementHistoryRestoration.CoordinatedCalls==1,"rolled-back native set falls back once");Check(batch.Created==498&&batch.Failed==0&&ElementHistoryRestoration.LastCount==498,"all available requests reach the coordinated fallback");Check(batch.CaptureWarnings.Count==1&&batch.CaptureWarnings[0].Contains("native-rejected-fixture")&&batch.CaptureWarnings[0].Contains("objet par objet"),"native fallback reason clearly reported");Check(ElementHistoryRestoration.RefreshCalls==1,"fallback receives refresh callback");
   Reset();batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n"),Direct("d")},Refresh);Check(ElementHistoryRestoration.NativeCalls==0&&ElementHistoryRestoration.CoordinatedCalls==1&&ElementHistoryRestoration.LastCount==2,"mixed data stays in one dependency-coordinated call");Check(batch.Created==2&&batch.CaptureWarnings.Count==0,"mixed coordinated path is not mislabeled native failure");
   Reset();batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{null,new HistoryRestoreRequest{SourceUniqueId="missing-recipe"},Native("available")},Refresh);Check(ElementHistoryRestoration.CoordinatedCalls==1&&ElementHistoryRestoration.NativeCalls==0&&ElementHistoryRestoration.LastCount==2,"null entries filtered but recipe-less requests reach per-item reporting");Check(batch.Created==1&&batch.Failed==1,"missing recipe failure does not discard available item");
   Reset();var definition=Native("definition");definition.Recipe.Native.DefinitionOnly=true;batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{definition,Native("physical")});Check(ElementHistoryRestoration.NativeCalls==0&&ElementHistoryRestoration.CoordinatedCalls==1,"definition-only artifacts cannot use complete clipboard path");
   Reset();var stale=Direct("direct-with-stale-native");stale.Recipe.Native=new HistoryNativeArtifact();batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{stale,Native("physical")});Check(ElementHistoryRestoration.NativeCalls==0&&ElementHistoryRestoration.CoordinatedCalls==1,"direct mode remains coordinated even with stale native field");
   Reset();var regeneration=new Autodesk.Revit.Exceptions.RegenerationFailedException("regeneration");ElementHistoryRestoration.NativeError=regeneration;Check(ReferenceEquals(Capture(()=>ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n")})),regeneration),"regeneration failure propagates unchanged");Check(ElementHistoryRestoration.CoordinatedCalls==0,"regeneration failure never causes another document operation");
   Reset();var wrapped=new InvalidOperationException("archive context",regeneration);ElementHistoryRestoration.NativeError=wrapped;Check(ReferenceEquals(Capture(()=>ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n")})),wrapped)&&ElementHistoryRestoration.CoordinatedCalls==0,"wrapped regeneration failure blocks fallback");
   Reset();var aggregate=new AggregateException(new InvalidOperationException("ordinary"),wrapped);ElementHistoryRestoration.NativeError=aggregate;Check(ReferenceEquals(Capture(()=>ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n")})),aggregate)&&ElementHistoryRestoration.CoordinatedCalls==0,"any aggregated regeneration failure blocks fallback");
   Reset();ElementHistoryRestoration.NativeError=new AggregateException(new InvalidOperationException("a"),new InvalidOperationException("b"));batch=ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n")});Check(ElementHistoryRestoration.NativeCalls==1&&ElementHistoryRestoration.CoordinatedCalls==1&&batch.Created==1,"ordinary aggregate failure permits one coordinated fallback");
   Reset();var fallbackError=new InvalidOperationException("fallback failed");ElementHistoryRestoration.NativeError=nativeError;ElementHistoryRestoration.CoordinatedError=fallbackError;Check(ReferenceEquals(Capture(()=>ElementHistoryRestoration.RestoreAvailable(doc,new[]{Native("n")})),fallbackError),"fallback exception propagates without recursive retry");Check(ElementHistoryRestoration.NativeCalls==1&&ElementHistoryRestoration.CoordinatedCalls==1,"failed fallback is attempted only once");
   Reset();batch=ElementHistoryRestoration.RestoreAvailable(doc,null);Check(ElementHistoryRestoration.CoordinatedCalls==1&&ElementHistoryRestoration.NativeCalls==0&&batch.Created==0&&batch.Failed==0,"empty input uses existing empty coordinated behavior");
   Reset();int enumerations=0;IEnumerable<HistoryRestoreRequest> Once(){if(++enumerations>1)throw new Exception("input enumerated twice");yield return Native("once-a");yield return Native("once-b");}batch=ElementHistoryRestoration.RestoreAvailable(doc,Once());Check(enumerations==1&&ElementHistoryRestoration.LastCount==2&&ElementHistoryRestoration.NativeCalls==1,"input materialized once before classification and native call");
   Console.WriteLine("PASS: "+checks+" production available-restoration dispatch checks.");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit><NoWarn>0649;0414</NoWarn></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath=Join-Path $output 'AvailableDispatch.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Available-restoration dispatch test build failed.'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/AvailableDispatch.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'Available-restoration dispatch checks failed.'}
Write-Output $output
