#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-closed-cleanup-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.cs'))
function Slice([string]$first,[string]$next){$a=$source.IndexOf($first);$b=if($a -ge 0){$source.IndexOf($next,$a)}else{-1};if($a -lt 0 -or $b -le $a){throw ('Production section missing: '+$first)};return $source.Substring($a,$b-$a)}
$parts=@((Slice '        private static bool ManualPreparation;' '        // Explicit preparation also upgrades'),(Slice '        internal static HistoryRestoreRequest ResolvePreparedRequest(' '        internal static HistoryRestoreRequest ToRequest('),(Slice '        private static bool NeedsOpeningClassification(' '        private static bool NeedsConversion('),(Slice '        private static void Publish(Prepared item)' '        private static void ConvertSmallNative('),(Slice '        private const string TerminalBridgeFailurePrefix' '        internal static void Shutdown()'))
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'),('using System;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Linq; namespace Analyse{internal static partial class ElementHistoryDurableBridge{'+($parts -join [Environment]::NewLine)+'}}'))
$fixture=@'
using System;using System.Collections.Generic;using System.IO;using System.Linq;
namespace Analyse{
 internal sealed class ElementHistoryEvent{internal string EventId,ModelKey,Action="delete";internal bool Excluded;}
 internal sealed class HistoryNativeArtifact{internal string File,PayloadId;internal bool WholeModel;}
 internal sealed class HistoryRecipe{internal bool UsesExistingModelDefinitions;internal HistoryNativeArtifact Native;}
 internal sealed class HistoryRestoreRequest{internal string SourceUniqueId,Label,CaptureFailure;internal HistoryRecipe Recipe;internal bool NativeGeneration,ClassificationOnly;internal Classification OpeningClassification;}
 internal sealed class Classification{internal bool Excluded;internal bool IsExcluded()=>Excluded;}
 internal static class ElementHistoryCompatibility{internal static string GetLoadedApiIssue()=>null;}
 internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory=>AppContext.BaseDirectory;}
 internal sealed class ElementHistoryLocalSpool{internal static readonly ElementHistoryLocalSpool Default=new ElementHistoryLocalSpool();internal bool Pending;internal bool HasPending(string directory)=>Pending;}
 internal static class ElementHistoryBridgeCache{internal static readonly List<string> Deleted=new List<string>();internal static bool LeaseBusy;internal static void Delete(string root,string path){if(LeaseBusy)throw new IOException("leased");Deleted.Add(path);}}
 internal static class ElementHistoryDurableStore{
  internal static readonly Dictionary<string,HistoryRestoreRequest> Values=new Dictionary<string,HistoryRestoreRequest>();internal static readonly HashSet<string> Jobs=new HashSet<string>();internal static bool WriteFails,ReadbackFails,ClassificationMustNotRead;internal static string Poison;internal static int Writes;
  internal static HistoryRestoreRequest ResolveAutonomousConversion(string id)=>null;
  internal static HistoryRestoreRequest ResolveConversion(string id){if(Poison==id)throw new InvalidDataException("corrupt");return Values.TryGetValue(id,out var value)?value:null;}
  internal static Classification ResolveOpeningClassification(string id){if(ClassificationMustNotRead)throw new Exception("completed result was reclassified");return null;}
  internal static void PublishOpeningClassification(string id,Classification value){}
  internal static void PublishNativeConversion(string id,HistoryRestoreRequest request){if(WriteFails)throw new IOException("disk unavailable");Values[id]=request;Writes++;if(ReadbackFails)Poison=id;}
  internal static void PublishConversion(string id,HistoryRestoreRequest request)=>PublishNativeConversion(id,request);
  internal static void DeleteJob(string id){if(ResolveConversion(id)==null)throw new InvalidOperationException("conversion required");Jobs.Remove(id);}
 }
 internal static partial class ElementHistoryDurableBridge{
  private sealed class Session{internal string Id,BridgeFile,ModelKey;internal bool Closed,Deleted;}
  private sealed class Prepared{internal ElementHistoryEvent Event;internal HistoryRestoreRequest Request;}
  private static readonly object Sync=new object();private static readonly Dictionary<string,Session> Sessions=new Dictionary<string,Session>(),Live=new Dictionary<string,Session>();private static readonly Dictionary<string,ElementHistoryEvent> Pending=new Dictionary<string,ElementHistoryEvent>();private static readonly Dictionary<string,string> Errors=new Dictionary<string,string>();private static readonly Dictionary<string,int> TransportRetries=new Dictionary<string,int>();private static readonly List<ElementHistoryEvent> Journal=new List<ElementHistoryEvent>();private static readonly Dictionary<string,HistoryRestoreRequest> Raw=new Dictionary<string,HistoryRestoreRequest>();private static DateTime NextPassUtc;private static int Converted,Preparing,checks;private static bool ThrowJournal,ThrowNativeLookup;
  private enum ReadinessState{Preparing,Ready,Unavailable}
  private static string EventId(ElementHistoryEvent ev)=>ev.EventId;
  private static void QueueAutonomousPreparation(string modelKey,HashSet<string> selectedIds){} // IO upgrade is outside this legacy cleanup fixture.
  private static void SetReadiness(string id,ReadinessState state,string reason){if(state==ReadinessState.Preparing)Preparing++;}
  private static void SignalReadiness(){}
  private static string ReadyReason(HistoryRecipe recipe)=>"ready";
  private static bool IsReadyDirectRequest(HistoryRestoreRequest request)=>request?.Recipe?.UsesExistingModelDefinitions==true;
  private static bool IsReadyNativeRequest(HistoryRestoreRequest request)=>request?.Recipe?.Native?.PayloadId!=null;
  private static bool HasLiveExclusion(ElementHistoryEvent ev)=>ev.Excluded;
  private static HistoryRestoreRequest TryGetOpeningNativeRequest(ElementHistoryEvent ev){if(ThrowNativeLookup)throw new Exception("completed result requested native upgrade");return Raw[ev.EventId];}
  private static IEnumerable<HistoryNativeArtifact> Artifacts(HistoryRecipe recipe){if(recipe?.Native!=null)yield return recipe.Native;}
  private static IEnumerable<ElementHistoryEvent> ReadJournal(){if(ThrowJournal)throw new InvalidDataException("unreadable journal");return Journal;}
  private static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev,bool resolve=true){if(resolve){var value=ElementHistoryDurableStore.ResolveConversion(ev.EventId);if(value!=null)return value;}var raw=Raw[ev.EventId];return new HistoryRestoreRequest{SourceUniqueId=raw.SourceUniqueId,Label=raw.Label,Recipe=raw.Recipe,CaptureFailure=raw.CaptureFailure};}
  private static void SaveSession(Session session){}
  private static string LocalBridgeDirectory=>Path.Combine(AppContext.BaseDirectory,"owned-bridge");
  private static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
  private static bool Throws(Action action){try{action();return false;}catch(IOException){return true;}catch(InvalidDataException){return true;}}
  private static void Reset(){Sessions.Clear();Live.Clear();Pending.Clear();Errors.Clear();TransportRetries.Clear();Journal.Clear();Raw.Clear();ElementHistoryDurableStore.Values.Clear();ElementHistoryDurableStore.Jobs.Clear();ElementHistoryDurableStore.Writes=0;ElementHistoryDurableStore.WriteFails=ElementHistoryDurableStore.ReadbackFails=ElementHistoryDurableStore.ClassificationMustNotRead=false;ElementHistoryDurableStore.Poison=null;ElementHistoryBridgeCache.Deleted.Clear();ElementHistoryBridgeCache.LeaseBusy=false;ElementHistoryLocalSpool.Default.Pending=false;ThrowJournal=ThrowNativeLookup=false;Preparing=0;FinishPreparationLocked();}
  private static Session SessionFor(string model="model",bool closed=true){var s=new Session{Id=Guid.NewGuid().ToString("N"),ModelKey=model,Closed=closed};s.BridgeFile=Path.Combine(LocalBridgeDirectory,"snapshot-opening-"+s.Id+".rvt");Sessions.Add(s.Id,s);return s;}
  private static ElementHistoryEvent EventFor(string id,Session session,bool pending=true,bool error=false,bool transient=false,bool receipt=true){var e=new ElementHistoryEvent{EventId=id,ModelKey=session.ModelKey};Journal.Add(e);Raw[id]=new HistoryRestoreRequest{SourceUniqueId="source-"+id,Label="item-"+id,Recipe=receipt?new HistoryRecipe{Native=new HistoryNativeArtifact{File=session.BridgeFile,WholeModel=true}}:null};if(pending){Pending.Add(id,e);ElementHistoryDurableStore.Jobs.Add(id);}if(error)Errors[id]="unsupported element";if(transient)TransportRetries[id]=1;return e;}
  internal static int Main(){try{
   Reset();var s=SessionFor();var direct=EventFor("direct",s,false);ElementHistoryDurableStore.Values[direct.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{UsesExistingModelDefinitions=true}};var native=EventFor("native",s,false);ElementHistoryDurableStore.Values[native.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{Native=new HistoryNativeArtifact{PayloadId="durable-payload"}}};for(int i=0;i<4;i++)EventFor("permanent-"+i,s,true,true);RequestPreparation(s.ModelKey);RequestPreparation(s.ModelKey);Check(Errors.Count==4&&Preparing==0,"repeated clicks cannot retry definitive failures");CleanupClosedSessions();Check(s.Deleted&&ElementHistoryBridgeCache.Deleted.SequenceEqual(new[]{s.BridgeFile}),"direct/native ready plus four finalized failures release closed source");Check(Pending.Count==0&&ElementHistoryDurableStore.Jobs.Count==0&&Errors.Count==0,"final outcomes acknowledge all failed jobs");Check(ElementHistoryDurableStore.Writes==4,"four final diagnostics durably published");Check(Enumerable.Range(0,4).All(i=>IsTerminalBridgeFailure(ElementHistoryDurableStore.Values["permanent-"+i])),"final diagnostic markers survive readback");Check(ElementHistoryDurableStore.Values["native"].Recipe.Native.PayloadId=="durable-payload","native durable payload reference preserved");Check(Raw["permanent-0"].Recipe.Native.WholeModel,"original journal remains immutable");ElementHistoryDurableStore.ClassificationMustNotRead=true;ThrowNativeLookup=true;Check(!NeedsNativeUpgrade(direct,ElementHistoryDurableStore.Values[direct.EventId]),"ready direct result never upgraded again");var terminal=Journal.First(e=>e.EventId=="permanent-0");Check(!NeedsNativeUpgrade(terminal,ElementHistoryDurableStore.Values[terminal.EventId]),"terminal result never upgraded again");Check(!NeedsOpeningClassification(terminal,Raw[terminal.EventId],ElementHistoryDurableStore.Values[terminal.EventId]),"terminal result never reclassified");
   Reset();s=SessionFor();EventFor("transient",s,true,true,true);CleanupClosedSessions();Check(!s.Deleted&&Pending.Count==1&&ElementHistoryDurableStore.Writes==0,"transient disk failure retains source");RequestPreparation(s.ModelKey);Check(!Errors.ContainsKey("transient")&&Preparing==1,"transport failure remains retryable");CleanupClosedSessions();Check(!s.Deleted,"unfinished transport retry retains source");
   Reset();s=SessionFor();EventFor("untreated",s);CleanupClosedSessions();Check(!s.Deleted&&ElementHistoryDurableStore.Writes==0,"never processed request cannot be evicted");
   Reset();s=SessionFor("closed-model");var other=SessionFor("other-model",false);EventFor("foreign",other,true,true);CleanupClosedSessions();Check(s.Deleted&&Errors.ContainsKey("foreign")&&ElementHistoryDurableStore.Writes==0,"another model failure does not retain closed unrelated copy");
   Reset();s=SessionFor();other=SessionFor(s.ModelKey,false);Live.Add(other.Id,other);EventFor("other-source",other,true,true);CleanupClosedSessions();Check(ElementHistoryDurableStore.Writes==0&&Errors.ContainsKey("other-source"),"another live source of same model cannot be finalized");
   Reset();s=SessionFor();Live.Add(s.Id,s);EventFor("no-receipt-closed",s,true,true,false,false);CleanupClosedSessions();Check(s.Deleted&&ElementHistoryDurableStore.Writes==1,"closed shutdown session is not considered live");
   Reset();s=SessionFor();other=SessionFor(s.ModelKey,false);Live.Add(other.Id,other);EventFor("no-receipt-live",s,true,true,false,false);CleanupClosedSessions();Check(!s.Deleted&&ElementHistoryDurableStore.Writes==0,"unattributed failure retained while same model is live");
   Reset();s=SessionFor();EventFor("spool",s,true,true);ElementHistoryLocalSpool.Default.Pending=true;CleanupClosedSessions();Check(!s.Deleted&&ElementHistoryDurableStore.Writes==0,"pending spool blocks finalization and deletion");
   Reset();s=SessionFor();EventFor("corrupt",s,true,true);ElementHistoryDurableStore.Poison="corrupt";Check(Throws(CleanupClosedSessions)&&!s.Deleted&&Pending.Count==1,"corrupt durable record retains source and job");
   Reset();s=SessionFor();EventFor("write-fails",s,true,true);ElementHistoryDurableStore.WriteFails=true;Check(Throws(CleanupClosedSessions)&&!s.Deleted&&Pending.Count==1&&ElementHistoryDurableStore.Jobs.Count==1,"failed publication cannot delete source or acknowledge job");
   Reset();s=SessionFor();EventFor("readback-fails",s,true,true);ElementHistoryDurableStore.ReadbackFails=true;Check(Throws(CleanupClosedSessions)&&!s.Deleted&&Pending.Count==1&&ElementHistoryDurableStore.Jobs.Count==1,"failed readback cannot delete source or acknowledge job");
   Reset();s=SessionFor();ElementHistoryBridgeCache.LeaseBusy=true;CleanupClosedSessions();Check(!s.Deleted&&ElementHistoryBridgeCache.Deleted.Count==0,"active lease retains source");
   Reset();s=SessionFor();ThrowJournal=true;Check(Throws(CleanupClosedSessions)&&!s.Deleted,"unreadable journal blocks cleanup");
   Reset();s=SessionFor();s.BridgeFile=Path.Combine(AppContext.BaseDirectory,"outside-owned.rvt");Check(Throws(CleanupClosedSessions)&&ElementHistoryBridgeCache.Deleted.Count==0,"owned path guard remains enforced");
   Console.WriteLine("PASS: "+checks+" production closed-source cleanup and terminal outcome checks.");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit><NoWarn>0649;0414</NoWarn></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath=Join-Path $output 'ClosedCleanup.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Closed-source cleanup test build failed.'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/ClosedCleanup.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'Closed-source cleanup checks failed.'}
Write-Output $output
