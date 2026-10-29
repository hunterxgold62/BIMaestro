#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo ('tmp/history-reader-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out|Out-Null
$folder=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$window=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryWindow.xaml.cs'))
$readiness=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryDurableBridge.Readiness.cs'))
$bridge=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryDurableBridge.cs'))
$reconstruction=[IO.File]::ReadAllText((Join-Path $folder 'ElementHistoryReconstruction.cs'))
function Block([string]$text,[string]$signature){
 $start=$text.IndexOf($signature,[StringComparison]::Ordinal);if($start -lt 0){throw ('Missing '+$signature)}
 $open=$text.IndexOf('{',$start);$depth=0
 for($position=$open;$position -lt $text.Length;$position++){
  if($text[$position] -eq '{'){$depth++}
  if($text[$position] -eq '}'){$depth--;if($depth -eq 0){return $text.Substring($start,$position-$start+1)}}
 }
 throw 'Unclosed production block.'
}
$readerStart=$window.IndexOf('        private static bool HasTemporaryReceipt(')
$readerEnd=$window.IndexOf('        private void RestoreDeletedButton_Click',$readerStart)
$contractStart=$readiness.IndexOf('        internal static bool IsReadyDirectRequest(')
$contractEnd=$readiness.IndexOf('        internal static EventReadiness GetReadiness(',$contractStart)
$resolveStart=$bridge.IndexOf('        internal static HistoryRestoreRequest ResolvePreparedRequest(')
$resolveEnd=$bridge.IndexOf('        internal static HistoryRestoreRequest ToRequest(',$resolveStart)
$readRecipe=Block $reconstruction '        internal static HistoryRecipe ReadRecipe('
$checkReadiness=Block $readiness '        private static void CheckReadiness('
if($readerStart -lt 0 -or $readerEnd -le $readerStart -or $contractStart -lt 0 -or $contractEnd -le $contractStart -or $resolveStart -lt 0 -or $resolveEnd -le $resolveStart){throw 'Production reader/contract extraction failed.'}
$production='using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Globalization;using Newtonsoft.Json.Linq;namespace Analyse{internal static partial class ElementHistoryDurableBridge{'+$readiness.Substring($contractStart,$contractEnd-$contractStart)+$bridge.Substring($resolveStart,$resolveEnd-$resolveStart)+$checkReadiness+'}internal static partial class ElementHistoryWindow{'+$window.Substring($readerStart,$readerEnd-$readerStart)+'}internal static class ElementHistoryReconstruction{'+$readRecipe+'}}'
[IO.File]::WriteAllText((Join-Path $out 'Production.cs'),$production)
$fixture=@'
using System;using System.IO;using System.Collections.Generic;using System.Linq;using Newtonsoft.Json;
namespace Analyse{
 internal sealed class ElementHistoryEvent{public string EventId;public int ElementId;public DateTime Ts;}
 internal sealed class HistoryRecipe{public int Version=1;public string Kind,Type,Level;public bool UsesExistingModelDefinitions;public HistoryNativeArtifact Native,OpeningFallback;public List<HistoryCircuit> ElectricalCircuits;public List<string> SystemMembers;public int ElectricalSystemType;public double[] Point,BasisX,BasisZ;public List<HistoryPort> Ports;public List<HistoryConnection> Connections;}
 internal sealed class HistoryCircuit{public HistoryRecipe Recipe;}
 internal sealed class HistoryPort{public int Id;public double[] Origin;}
 internal sealed class HistoryConnection{public string Peer;public HistoryPort Port,PeerPort;}
 internal sealed class HistoryNativeArtifact{public string PayloadId,File;public bool DefinitionOnly,WholeModel,Ready;}
 internal sealed class HistoryRestoreRequest{public string SourceUniqueId,Label,CaptureFailure;public HistoryRecipe Recipe;}
 internal enum ElectricalSystemType{PowerCircuit=0}
 internal sealed class OpeningClassification{public bool IsExcluded()=>false;}
 internal static class CollaborativeModelTrackerStore{internal static string ActiveDirectory;}
 internal static class ElementHistoryRetryPolicy{internal static bool IsTransient(Exception ex)=>false;}
 // Controlled IO adapter: real production contracts/readers/readiness below remain unmodified.
 // Full durable-record checksum/atomic-publication behavior is covered by the real store suite.
 internal static class ElementHistoryDurableStore{
  internal static readonly Dictionary<string,HistoryRestoreRequest> Data=new Dictionary<string,HistoryRestoreRequest>();
  internal static readonly HashSet<string> Corrupt=new HashSet<string>();internal static int NativeReads,AutonomousReads;
  internal static string AutonomousPath(string id)=>Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"autonomous-conversions",id+".json");
  internal static void WriteAutonomous(string id,HistoryRestoreRequest data){var path=AutonomousPath(id);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,JsonConvert.SerializeObject(data));}
  internal static HistoryRestoreRequest ResolveAutonomousConversion(string id){AutonomousReads++;var path=AutonomousPath(id);if(!File.Exists(path))return null;var data=JsonConvert.DeserializeObject<HistoryRestoreRequest>(File.ReadAllText(path));if(!ElementHistoryDurableBridge.IsReadyDirectRequest(data))throw new InvalidDataException("Invalid autonomous recipe for "+id);return data;}
  internal static HistoryRestoreRequest ResolveConversion(string id){NativeReads++;if(Corrupt.Contains(id))throw new InvalidDataException("Invalid stored checksum for "+id);return Data.TryGetValue(id,out var r)?r:null;}
  internal static OpeningClassification ResolveOpeningClassification(string id)=>null;
 }
 internal static partial class ElementHistoryDurableBridge{
  internal enum ReadinessState{Preparing,Ready,Unavailable}
  internal sealed class EventReadiness{public ReadinessState State;public bool Legacy,Excluded;public string Reason;}
  internal static readonly object Sync=new object();internal static readonly Dictionary<string,string> Errors=new Dictionary<string,string>();
  internal static readonly Dictionary<string,HistoryRestoreRequest> Raw=new Dictionary<string,HistoryRestoreRequest>(),Opening=new Dictionary<string,HistoryRestoreRequest>();
  internal static readonly Dictionary<string,EventReadiness> States=new Dictionary<string,EventReadiness>();internal static int OpeningReads;
  internal static string EventId(ElementHistoryEvent ev)=>ev.EventId;
  private static bool IsLegacy(ElementHistoryEvent ev)=>ev.EventId.StartsWith("legacy-",StringComparison.Ordinal);
  private static string ReadyReason(HistoryRecipe recipe)=>recipe.UsesExistingModelDefinitions?"Autonomous JSON ready":"Native ready";
  private static void SetReadiness(string id,ReadinessState state,string reason,bool legacy=false,bool retryable=false,long? expectedGeneration=null,bool excluded=false){States[id]=new EventReadiness{State=state,Reason=reason,Legacy=legacy,Excluded=excluded};}
  internal static EventReadiness GetReadiness(ElementHistoryEvent ev){CheckReadiness(ev,ev.EventId,1);return States[ev.EventId];}
  internal static HistoryRestoreRequest TryGetOpeningNativeRequest(ElementHistoryEvent ev){OpeningReads++;return Opening.TryGetValue(ev.EventId,out var r)?r:null;}
  internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev,bool resolve=true)=>Raw.TryGetValue(ev.EventId,out var r)?r:null;
  private static IEnumerable<HistoryRecipe> Recipes(HistoryRecipe r){if(r==null)yield break;yield return r;foreach(var c in r.ElectricalCircuits??new List<HistoryCircuit>())foreach(var n in Recipes(c.Recipe))yield return n;}
  private static IEnumerable<HistoryNativeArtifact> Artifacts(HistoryRecipe r)=>Recipes(r).SelectMany(recipe=>new[]{recipe.Native,recipe.OpeningFallback}).Where(a=>a!=null);
 }
 internal static partial class ElementHistoryWindow{
  static int count;static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  static ElementHistoryEvent Event(string id,int number=1)=>new ElementHistoryEvent{EventId=id,ElementId=number,Ts=DateTime.UtcNow.AddMonths(-4)};
  static HistoryRestoreRequest Pump(int n)=>new HistoryRestoreRequest{SourceUniqueId="pump-"+n,Label="Recorded pump "+n,Recipe=new HistoryRecipe{Kind="family",Type="recorded-pump-family-symbol",Level="recorded-level",UsesExistingModelDefinitions=true,Point=new[]{36.21091831573927+n*5,198.71722950812025,117.2158718814335},BasisX=new[]{-1.0,0,0},BasisZ=new[]{0.0,0,1},Ports=new List<HistoryPort>{new HistoryPort{Id=1},new HistoryPort{Id=2}},Connections=new List<HistoryConnection>{new HistoryConnection{Peer="pipe-in-"+n},new HistoryConnection{Peer="pipe-out-"+n}}}};
  static HistoryRestoreRequest Native()=>new HistoryRestoreRequest{SourceUniqueId="native-item",Recipe=new HistoryRecipe{Kind="native_archive",Native=new HistoryNativeArtifact{PayloadId=new string('a',64),Ready=true}}};
  static int Main(string[] args){try{
   CollaborativeModelTrackerStore.ActiveDirectory=Path.Combine(Path.GetFullPath(args[0]),"isolated-history");Directory.CreateDirectory(CollaborativeModelTrackerStore.ActiveDirectory);
   var old=Event("old-autonomous");ElementHistoryDurableStore.WriteAutonomous(old.EventId,Pump(1));var oldPath=ElementHistoryDurableStore.AutonomousPath(old.EventId);File.SetLastWriteTimeUtc(oldPath,DateTime.UtcNow.AddMonths(-4));
   var recovered=ReadReadyRequest(old);Check(recovered.SourceUniqueId=="pump-1"&&recovered.Recipe.Native==null&&recovered.Recipe.OpeningFallback==null&&!Directory.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native")),"four-month semantic JSON must remain readable with no source RVT or native blob directory");
   Check(ElementHistoryDurableBridge.GetReadiness(old).State==ElementHistoryDurableBridge.ReadinessState.Ready,"actual readiness must accept four-month autonomous JSON");
   Check(ElementHistoryDurableStore.NativeReads==0&&ElementHistoryDurableBridge.OpeningReads==0,"autonomous JSON must not consult unavailable old native storage or opening files");
   var both=Event("both");ElementHistoryDurableStore.WriteAutonomous(both.EventId,Pump(2));ElementHistoryDurableStore.Data[both.EventId]=Native();Check(ReadReadyRequest(both).SourceUniqueId=="pump-2","autonomous data wins over a native conversion");
   ElementHistoryDurableStore.Corrupt.Add(both.EventId);Check(ReadReadyRequest(both).SourceUniqueId=="pump-2","a corrupt obsolete native payload cannot poison independent autonomous JSON");
   Check(recovered.Recipe.Ports.Count==2&&recovered.Recipe.Connections.Count==2&&recovered.Recipe.Point.Length==3&&!ReferenceEquals(recovered,ElementHistoryDurableBridge.ResolvePreparedRequest(old.EventId)),"reader returns detached semantic DTOs preserving placement and connector links");
   var corrupt=Event("corrupt-autonomous");ElementHistoryDurableStore.WriteAutonomous(corrupt.EventId,Pump(3));File.WriteAllText(ElementHistoryDurableStore.AutonomousPath(corrupt.EventId),"{broken");ElementHistoryDurableStore.Data[corrupt.EventId]=Native();
   Check(ElementHistoryDurableBridge.GetReadiness(corrupt).State==ElementHistoryDurableBridge.ReadinessState.Unavailable&&ReadReadyRequests(new[]{corrupt}).Requests.Count==0,"corrupt autonomous data must be unavailable rather than silently accepted via a stale Ready/native record");
   var missingLevel=Event("invalid-autonomous");var incomplete=Pump(4);incomplete.Recipe.Level=null;ElementHistoryDurableStore.WriteAutonomous(missingLevel.EventId,incomplete);Check(ElementHistoryDurableBridge.GetReadiness(missingLevel).State==ElementHistoryDurableBridge.ReadinessState.Unavailable,"incomplete autonomous recipe cannot claim Ready");
   var nativeOnly=Event("native-only");ElementHistoryDurableStore.Data[nativeOnly.EventId]=Native();Check(ReadReadyRequest(nativeOnly).Recipe.Native.PayloadId!=null&&ElementHistoryDurableBridge.GetReadiness(nativeOnly).State==ElementHistoryDurableBridge.ReadinessState.Ready,"legacy native-only conversions remain readable fallback data");
   var direct=Event("stored-direct");ElementHistoryDurableStore.Data[direct.EventId]=Pump(5);ElementHistoryDurableBridge.Opening[direct.EventId]=new HistoryRestoreRequest{Recipe=new HistoryRecipe{Kind="native_archive",Native=new HistoryNativeArtifact{WholeModel=true,Ready=true,File="snapshot-opening.rvt"}}};Check(ReadReadyRequest(direct).Recipe.UsesExistingModelDefinitions,"already recorded direct semantic JSON wins over an opening backup");
   var legacy=Event("legacy-original");ElementHistoryDurableBridge.Raw[legacy.EventId]=Pump(6);ElementHistoryDurableBridge.Raw[legacy.EventId].Recipe.UsesExistingModelDefinitions=false;Check(ReadReadyRequest(legacy).Recipe.UsesExistingModelDefinitions&&!ElementHistoryDurableBridge.Raw[legacy.EventId].Recipe.UsesExistingModelDefinitions,"legacy direct JSON is normalized without rewriting its historical DTO");
   var missing=Event("missing-data");Check(ReadReadyRequests(new[]{missing}).Requests.Count==0,"missing data cannot be accepted merely because UI previously said Ready");
   var partial=ReadReadyRequests(new[]{old,corrupt});Check(partial.Requests.Count==1&&partial.Events[0]==old&&partial.Warnings.Count==1,"a corrupt member never discards another independently readable JSON record");
   var large=Enumerable.Range(10,500).Select(n=>Event("large-"+n,n)).ToList();foreach(var e in large)ElementHistoryDurableStore.Data[e.EventId]=Pump(e.ElementId);ElementHistoryDurableStore.Corrupt.Add(large[2].EventId);ElementHistoryDurableStore.Corrupt.Add(large[99].EventId);var batch=ReadReadyRequests(large);Check(batch.Requests.Count==498&&batch.Warnings.Count==2,"500 requests retain 498 valid semantic requests if two stored records are corrupt");
   Console.WriteLine("PASS: "+count+" production autonomous-reader/readiness checks (four-month JSON, no RVT/native blobs).");return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $out 'Fixtures.cs'),$fixture)
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/bin/Revit2025/Debug/Newtonsoft.Json.dll') -Destination (Join-Path $out 'Newtonsoft.Json.dll')
$project=Join-Path $out 'Reader.Tests.csproj'
[IO.File]::WriteAllText($project,'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit><NoWarn>0649</NoWarn></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/><Reference Include="Newtonsoft.Json"><HintPath>Newtonsoft.Json.dll</HintPath></Reference></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $out 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet build $project --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $out 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $out 'build.log');throw 'Restoration-reader fixture compilation failed.'}
& dotnet (Join-Path $out 'bin/Debug/net8.0/Reader.Tests.dll') $out
if($LASTEXITCODE -ne 0){throw 'Restoration-reader fixture failed.'}
Write-Output $out
