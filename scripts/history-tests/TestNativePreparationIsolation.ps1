#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-native-isolation-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryDurableBridge.NativeClipboard.cs'))
$start=$source.IndexOf('        private static bool TryRecoverNativeMemberFailure(')
$end=$source.IndexOf('        private static void ResetPreparationSource()', $start)
if($start -lt 0 -or $end -le $start){throw 'Production isolation helpers missing.'}
$production='using System;using System.Collections.Generic;using System.Diagnostics;using System.Linq;using Autodesk.Revit.DB; namespace Analyse{internal static partial class ElementHistoryDurableBridge{'+$source.Substring($start,$end-$start)+'}}'
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'),$production)
Copy-Item -LiteralPath (Join-Path $repo 'BIMaestro/commands/qui est le coupable/HistoryOpeningClassification.cs') -Destination (Join-Path $output 'Classification.cs')
$fixture=@'
using System;using System.Collections.Generic;using System.Linq;using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 internal enum Domain{DomainUndefined,DomainPiping,DomainHvac}
 internal sealed class ElementId{internal int Value;internal ElementId(int value){Value=value;}}
 internal sealed class Category{internal ElementId Id=new ElementId(-2000011);internal string Name="Model";}
 internal class Element{internal string UniqueId;internal ElementId Id;internal Category Category=new Category();internal bool IsValidObject=true,ViewSpecific;internal Element Root;internal readonly List<Element> Members=new List<Element>();internal readonly List<Connector> Ports=new List<Connector>();}
 internal sealed class FamilyInstance:Element{internal Element Host,SuperComponent;}
 internal sealed class MEPSystem:Element{}
 internal sealed class Connector{internal Element Owner;internal Domain Domain;internal MEPSystem MEPSystem;internal readonly List<Connector> AllRefs=new List<Connector>();internal bool IsConnectedTo(Connector other)=>AllRefs.Contains(other);}
 internal sealed class Document{internal bool IsValidObject=true;internal readonly Dictionary<string,Element> Items=new Dictionary<string,Element>();internal Element GetElement(string uid)=>Items.TryGetValue(uid??"",out var element)?element:null;}
}
namespace Analyse {
 internal static class IdExtensions{internal static int GetIdValue(this ElementId id)=>id.Value;internal static long GetIdLongValue(this ElementId id)=>id.Value;}
 internal sealed class ElementHistoryEvent{internal string EventId;}
 internal sealed class HistoryRestoreRequest{internal string SourceUniqueId;internal string ConnectionsJson="original-links";}
 internal static class ElementHistoryNativeArchive{
  internal sealed class NativePreparationMemberException:InvalidOperationException{internal string SourceUniqueId,RootSourceUniqueId,Stage;internal NativePreparationMemberException(string uid,string root){SourceUniqueId=uid;RootSourceUniqueId=root;Stage="verify_native_placements";}}
  internal static Element OpeningRoot(Element element)=>element.Root??element;
  internal static IEnumerable<Element> Aggregate(Document doc,Element root)=>new[]{root}.Concat(root.Members);
  internal static bool IsDocumentModelElement(Document doc,Element element)=>doc.Items.ContainsKey(element.UniqueId);
 }
 internal static class ElementHistoryNetwork{internal static List<Connector> Ports(Element element)=>element.Ports;}
 internal static partial class ElementHistoryDurableBridge{
  private sealed class Prepared{internal ElementHistoryEvent Event;internal HistoryRestoreRequest Request;}
  private sealed class NativePreparationPass{internal List<Prepared> Pending=new List<Prepared>();internal readonly List<Prepared> Classified=new List<Prepared>(),Physical=new List<Prepared>(),Selected=new List<Prepared>();internal List<Prepared> Publishing;internal Document Source;internal long Generation;internal int Phase,Cursor,RecoveryAttempts;internal readonly HashSet<string> RecoveredFailureIdentities=new HashSet<string>(StringComparer.Ordinal);}
  private static readonly object Sync=new object();private static readonly Dictionary<string,ElementHistoryEvent> Pending=new Dictionary<string,ElementHistoryEvent>();private static readonly List<string> Failures=new List<string>();private static NativePreparationPass NativePass;private static int checks,resets,sequence;
  private static string EventId(ElementHistoryEvent ev)=>ev.EventId;
  private static void RecordFailure(string id,Exception error)=>Failures.Add(id);
  private static void SetPreparationProgress(string label,int completed,int total,long? generation=null){}
  private static void ResetNativePreparation(){NativePass=null;resets++;}
  private static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
  private static NativePreparationPass New(){Pending.Clear();Failures.Clear();resets=0;return NativePass=new NativePreparationPass{Source=new Document(),Phase=3};}
  private static Element Element(NativePreparationPass pass,string name,bool family=false){var e=family?(Element)new FamilyInstance():new Element();e.UniqueId=name;e.Id=new ElementId(++sequence);pass.Source.Items.Add(name,e);return e;}
  private static Prepared Add(NativePreparationPass pass,Element element){var p=new Prepared{Event=new ElementHistoryEvent{EventId=element.UniqueId},Request=new HistoryRestoreRequest{SourceUniqueId=element.UniqueId}};pass.Pending.Add(p);pass.Classified.Add(p);pass.Physical.Add(p);pass.Selected.Add(p);Pending.Add(element.UniqueId,p.Event);return p;}
  private static Connector Port(Element e,MEPSystem system=null){var c=new Connector{Owner=e,Domain=Domain.DomainPiping,MEPSystem=system};e.Ports.Add(c);return c;}
  private static void Connect(Connector a,Connector b){a.AllRefs.Add(b);b.AllRefs.Add(a);}
  private static ElementHistoryNativeArchive.NativePreparationMemberException Failure(Element element)=>new ElementHistoryNativeArchive.NativePreparationMemberException(element.UniqueId,ElementHistoryNativeArchive.OpeningRoot(element).UniqueId);
  private static List<Prepared> Component(NativePreparationPass pass,Element bad){Check(TryFindFailedNativeComponents(pass,new List<ElementHistoryNativeArchive.NativePreparationMemberException>{Failure(bad)},out var result),"component must be provable");return result;}
  internal static int Main(){try{
   var p=New();var post=Element(p,"bad-post");var pipe=Element(p,"pipe");var fitting=Element(p,"fitting",true);Add(p,post);Add(p,pipe);Add(p,fitting);Connect(Port(pipe),Port(fitting));var component=Component(p,post);Check(component.Count==1&&component[0].Request.SourceUniqueId==post.UniqueId,"isolated post must not quarantine network");
   var source=p.Source;Check(TryRecoverNativeMemberFailure(p,new InvalidOperationException("wrapper",Failure(post))),"typed nested failure must recover");Check(Failures.SequenceEqual(new[]{post.UniqueId}),"only failed component marked");Check(p.Physical.Count==2&&p.Classified.Count==2&&p.Pending.Count==2,"independent requests survive");Check(ReferenceEquals(source,p.Source)&&ReferenceEquals(NativePass,p)&&resets==0,"source and pass reused");Check(p.Phase==2&&p.RecoveryAttempts==1&&p.Selected.Count==0&&p.Publishing==null,"restart at selection only once");Check(!TryRecoverNativeMemberFailure(p,Failure(post))&&p.RecoveryAttempts==1&&Failures.Count==1,"identical failure is never replayed");var copyFailure=Failure(pipe);copyFailure.Stage="copy_native_set";Check(TryRecoverNativeMemberFailure(p,copyFailure),"new localized copy failure permits second bounded recovery");Check(p.RecoveryAttempts==2&&p.Physical.Count==1&&p.Physical[0].Request.SourceUniqueId==fitting.UniqueId,"connected valid neighbour survives second recovery");Check(p.Physical[0].Request.ConnectionsJson=="original-links","recorded connection metadata remains untouched");Check(!TryRecoverNativeMemberFailure(p,Failure(fitting)),"third automatic recovery forbidden");
   p=New();var group=Element(p,"group");var a=Element(p,"group-a");var b=Element(p,"group-b");a.Root=b.Root=group;group.Members.AddRange(new[]{a,b});Add(p,a);Add(p,b);Add(p,Element(p,"independent"));component=Component(p,a);Check(component.Count==2&&component.All(x=>x.Request.SourceUniqueId!="independent"),"same native root stays indivisible");
   p=New();a=Element(p,"physical-a");b=Element(p,"physical-b");Add(p,a);Add(p,b);Add(p,Element(p,"physical-independent"));Connect(Port(a),Port(b));Check(Component(p,a).Count==1,"physical neighbour remains eligible when only one owner fails");
   p=New();a=Element(p,"logical-a");b=Element(p,"logical-b");Add(p,a);Add(p,b);Add(p,Element(p,"logical-independent"));var system=new MEPSystem{UniqueId="original-system"};Port(a,system);Port(b,system);Check(Component(p,a).Count==1,"shared logical system does not quarantine other valid owners");
   p=New();var host=Element(p,"host");var child=(FamilyInstance)Element(p,"hosted",true);child.Host=host;Add(p,host);Add(p,child);Add(p,Element(p,"host-independent"));Check(Component(p,child).Count==2,"host relationship propagates quarantine");
   p=New();var parent=Element(p,"parent");child=(FamilyInstance)Element(p,"subcomponent",true);child.SuperComponent=parent;Add(p,parent);Add(p,child);Add(p,Element(p,"parent-independent"));Check(Component(p,parent).Count==2,"supercomponent relationship propagates quarantine");
   p=New();var post1=Element(p,"bad-post-1");var post2=Element(p,"bad-post-2");a=Element(p,"healthy-network-a");b=Element(p,"healthy-network-b");Add(p,post1);Add(p,post2);Add(p,a);Add(p,b);Connect(Port(a),Port(b));var aggregate=new AggregateException(Failure(post1),new InvalidOperationException("member context",Failure(post2)),Failure(post1));Check(TryCollectNativeMemberFailures(aggregate,out var known)&&known.Count==2,"aggregate member identities deduplicated through wrappers");Check(TryRecoverNativeMemberFailure(p,new InvalidOperationException("export context",aggregate)),"one recovery handles multiple localized failures");Check(new HashSet<string>(Failures).SetEquals(new[]{post1.UniqueId,post2.UniqueId})&&Failures.Count==2,"two isolated failures do not quarantine healthy network");Check(p.Physical.Count==2&&p.Physical.All(x=>x.Request.SourceUniqueId.StartsWith("healthy-network"))&&p.RecoveryAttempts==1,"remaining network exported with a single recovery budget");
   p=New();a=Element(p,"failed-network-a");b=Element(p,"failed-network-b");var independent=Element(p,"healthy-post");var neighbour=Element(p,"healthy-network-neighbour");Add(p,a);Add(p,b);Add(p,independent);Add(p,neighbour);Connect(Port(a),Port(b));Connect(Port(b),Port(neighbour));Check(TryRecoverNativeMemberFailure(p,new AggregateException(Failure(a),Failure(b))),"multiple failing network owners recover together");Check(Failures.Count==2&&p.Physical.Count==2&&p.Physical.Any(x=>x.Request.SourceUniqueId==independent.UniqueId)&&p.Physical.Any(x=>x.Request.SourceUniqueId==neighbour.UniqueId)&&p.RecoveryAttempts==1,"valid connected neighbour and unrelated post remain eligible");
   p=New();var many=new List<Element>();for(int i=0;i<500;i++){var item=Element(p,"item-"+i);many.Add(item);Add(p,item);}Check(TryRecoverNativeMemberFailure(p,new AggregateException(Failure(many[12]),Failure(many[478]))),"500-element batch recovers after two localized errors");Check(p.Physical.Count==498&&Failures.Count==2&&p.RecoveryAttempts==1,"two errors quarantine exactly two owners from 500");
   p=New();a=Element(p,"mixed-known");b=Element(p,"mixed-independent");Add(p,a);Add(p,b);Check(!TryRecoverNativeMemberFailure(p,new AggregateException(Failure(a),new InvalidOperationException("unlocalized failure"))),"unknown aggregate leaf blocks partitioning");Check(Failures.Count==0&&p.Physical.Count==2&&p.RecoveryAttempts==0,"mixed unknown aggregate leaves all state intact");
   p=New();a=Element(p,"unknown-a");Add(p,a);Add(p,Element(p,"unknown-b"));Check(!TryRecoverNativeMemberFailure(p,new InvalidOperationException("unknown")),"unknown failure must never partition");Check(Failures.Count==0&&p.Physical.Count==2&&p.Phase==3&&p.RecoveryAttempts==0,"unknown failure leaves state untouched");var bad=Failure(a);bad.RootSourceUniqueId="unproven-root";Check(!TryRecoverNativeMemberFailure(p,bad)&&Failures.Count==0,"unproven root must not partition");bad=Failure(a);bad.Stage="save_capsule";Check(!TryRecoverNativeMemberFailure(p,bad),"nonplacement failure must not partition");
   p=New();a=Element(p,"only");Add(p,a);Check(TryRecoverNativeMemberFailure(p,Failure(a)),"isolated final failure handled");Check(Failures.Count==1&&resets==1&&NativePass==null,"empty pass releases source through normal reset");
   Console.WriteLine("PASS: "+checks+" production native preparation isolation checks.");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit><NoWarn>0649;0414</NoWarn></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Classification.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
$projectPath=Join-Path $output 'NativeIsolation.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Native isolation test build failed.'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/NativeIsolation.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'Native isolation checks failed.'}
Write-Output $output
