#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$network=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs'))
$guard=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.Guard.cs'))
function Production-Member([string]$Signature) {
    $start=$network.IndexOf($Signature,[StringComparison]::Ordinal)
    if($start -lt 0) { throw "Production member absent: $Signature" }
    $open=$network.IndexOf('{',$start); $semicolon=$network.IndexOf(';',$start)
    if($semicolon -ge 0 -and $semicolon -lt $open) { return $network.Substring($start,$semicolon+1-$start) }
    $depth=0
    for($i=$open;$i -lt $network.Length;$i++) {
        if($network[$i] -eq '{') { $depth++ }
        elseif($network[$i] -eq '}') {
            $depth--
            if($depth -eq 0) {
                $end=$i+1
                if($end -lt $network.Length -and $network[$end] -eq ';') { $end++ }
                return $network.Substring($start,$end-$start)
            }
        }
    }
    throw "Unclosed production member: $Signature"
}
$types=@('internal sealed class HistoryPort','internal sealed class HistoryConnection') | ForEach-Object { Production-Member $_ }
$methods=@('internal static XYZ Vector(', 'private static double[] Pack(', 'internal static List<Connector> Ports(',
    'private static HistoryPort CapturePort(', 'internal static Connector Match(', 'private static MEPCurve FindHistoricalCurve(',
    'internal static List<HistoryConnection> CaptureConnections(') | ForEach-Object { Production-Member $_ }
$fixtures=@'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
namespace Autodesk.Revit.Exceptions { public sealed class RegenerationFailedException : Exception { } }
namespace Autodesk.Revit.DB {
public enum Domain { DomainPiping,DomainHvac,DomainCableTrayConduit }
public enum ConnectorType { End,Curve,Physical,Logical }
public enum ConnectorProfileType { Round,Rectangular }
public enum StorageType { ElementId,Double }
public enum BuiltInParameter { RBS_PIPING_SYSTEM_TYPE_PARAM,RBS_DUCT_SYSTEM_TYPE_PARAM }
public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
public enum MEPSystemClassification { UndefinedSystemClassification,SupplyHydronic,ReturnHydronic,SupplyAir,ExhaustAir }
public sealed class ElementId {
    public long Value; public ElementId(long id) {Value=id;}
    public static readonly ElementId InvalidElementId=new ElementId(-1);
    public override bool Equals(object o) {return o is ElementId id && id.Value==Value;}
    public override int GetHashCode() {return Value.GetHashCode();}
    public static bool operator ==(ElementId a,ElementId b) {return Object.Equals(a,b);}
    public static bool operator !=(ElementId a,ElementId b) {return !Object.Equals(a,b);}
}
public sealed class XYZ {
    public double X,Y,Z; public XYZ(double x,double y,double z) {X=x;Y=y;Z=z;}
    public double DistanceTo(XYZ p) {return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z));}
    public static XYZ Zero {get {return new XYZ(0,0,0);}}
    public static XYZ BasisX {get {return new XYZ(1,0,0);}}
    public static XYZ BasisY {get {return new XYZ(0,1,0);}}
    public static XYZ BasisZ {get {return new XYZ(0,0,1);}}
}
public class Location { }
public sealed class LocationPoint:Location {public XYZ Point=XYZ.Zero;}
public sealed class LocationCurve:Location {public Curve Curve=new Curve();}
public sealed class Curve {
    public XYZ Start=XYZ.Zero,End=XYZ.BasisX,Middle=new XYZ(.5,0,0);
    public XYZ GetEndPoint(int end) {return end==0?Start:End;} public XYZ Evaluate(double t,bool normalized) {return Middle;}
}
public sealed class Transform {public XYZ Origin=XYZ.Zero,BasisX=XYZ.BasisX,BasisY=XYZ.BasisY,BasisZ=XYZ.BasisZ;}
public sealed class Category {public string Name;}
public class Element {
    public Document Document; public ElementId Id,TypeId=new ElementId(100); public string UniqueId,Name="model",HistoricalTypeVersion;
    public Category Category=new Category(); public Location Location; public Element SystemType;
    public Parameter Parameter; public List<string> Origins=new List<string>();
    public ElementId GetTypeId() {return TypeId;} public Parameter get_Parameter(BuiltInParameter id) {return Parameter;}
}
public sealed class Family {public string Name="family";}
public sealed class FamilySymbol {public Family Family=new Family();}
public class FamilyInstance:Element {
    public FamilySymbol Symbol=new FamilySymbol(); public MEPModel MEPModel=new MEPModel(); public Transform Frame=new Transform();
    public bool Mirrored,HandFlipped,FacingFlipped; public Transform GetTransform() {return Frame;}
}
public class MEPCurve:Element {public ConnectorManager ConnectorManager=new ConnectorManager();}
public class MEPModel {public ConnectorManager ConnectorManager=new ConnectorManager();}
public sealed class ConnectorManager {public List<Connector> Connectors=new List<Connector>();}
public class MEPSystem:Element { }
public class MEPSystemType:Element {public MEPSystemClassification SystemClassification;}
public sealed class Connector {
    public Element Owner; public Domain Domain; public ConnectorProfileType Shape; public ConnectorType ConnectorType;
    public int Id; public XYZ Origin=XYZ.Zero; public Transform CoordinateSystem=new Transform();
    public double Radius=.05,Width,Height; public MEPSystem MEPSystem;
    public Plumbing.PipeSystemType PipeSystemType=Plumbing.PipeSystemType.ReturnHydronic;
    public Mechanical.DuctSystemType DuctSystemType=Mechanical.DuctSystemType.ExhaustAir;
    public List<Connector> AllRefs=new List<Connector>(); public bool IsConnectedTo(Connector peer) {return AllRefs.Contains(peer);}
}
public sealed class Document {
    internal List<Element> Elements=new List<Element>(); public int Regenerations,Rollbacks,Commits; public Action OnRegenerate;
    public T Add<T>(T e,string uid) where T:Element {e.Document=this;e.UniqueId=uid;e.Id=new ElementId(Elements.Count+1);Elements.Add(e);return e;}
    public Element GetElement(string uid) {return Elements.FirstOrDefault(e=>e.UniqueId==uid);}
    public Element GetElement(ElementId id) {return Elements.FirstOrDefault(e=>e.Id==id);}
    public void Regenerate() {Regenerations++;OnRegenerate?.Invoke();}
}
public sealed class FilteredElementCollector:IEnumerable<Element> {
    private Document Doc; private Type Type; public FilteredElementCollector(Document doc) {Doc=doc;}
    public FilteredElementCollector OfClass(Type t) {Type=t;return this;}
    public IEnumerator<Element> GetEnumerator() {return Doc.Elements.Where(e=>Type.IsInstanceOfType(e)).GetEnumerator();}
    IEnumerator IEnumerable.GetEnumerator() {return GetEnumerator();}
}
public sealed class Parameter {
    public bool IsReadOnly,SetResult=true; public StorageType StorageType=StorageType.ElementId;
    public int Calls; public Action<ElementId> OnSet;
    public bool Set(ElementId id) {Calls++;if(SetResult) OnSet?.Invoke(id);return SetResult;}
}
public sealed class SubTransaction:IDisposable {
    private Document Doc; private TransactionStatus Status; private Dictionary<Element,Element> Before;
    private Dictionary<LocationPoint,XYZ> Points;
    public SubTransaction(Document doc) {Doc=doc;}
    public void Start() {Status=TransactionStatus.Started;Before=Doc.Elements.ToDictionary(e=>e,e=>e.SystemType);
        Points=Doc.Elements.Select(e=>e.Location).OfType<LocationPoint>().ToDictionary(p=>p,p=>p.Point);}
    public TransactionStatus Commit() {Doc.Commits++;return Status=TransactionStatus.Committed;}
    public void RollBack() {Doc.Rollbacks++;foreach(var pair in Before) pair.Key.SystemType=pair.Value;
        foreach(var pair in Points) pair.Key.Point=pair.Value; Status=TransactionStatus.RolledBack;}
    public TransactionStatus GetStatus() {return Status;} public void Dispose() { }
}
}
namespace Autodesk.Revit.DB.Plumbing {
public enum PipeSystemType { UndefinedSystemType,SupplyHydronic,ReturnHydronic }
public sealed class Pipe:MEPCurve { }
public sealed class FlexPipe:MEPCurve {public List<XYZ> Points=new List<XYZ>();}
public sealed class PipeInsulation:Element { }
public sealed class PipingSystemType:MEPSystemType { }
}
namespace Autodesk.Revit.DB.Mechanical {
public enum DuctSystemType { UndefinedSystemType,SupplyAir,ExhaustAir }
public sealed class Duct:MEPCurve { }
public sealed class FlexDuct:MEPCurve {public List<XYZ> Points=new List<XYZ>();}
public sealed class DuctInsulation:Element { }
public sealed class MechanicalSystemType:MEPSystemType { }
}
namespace Analyse {
internal sealed class HistoryNativeArtifact {public bool? SourceSystemTypeDefined;}
internal sealed class HistoryRecipe {public HistoryNativeArtifact Native,OpeningFallback;public List<HistoryConnection> Connections;}
internal static class ElementHistoryReconstruction {
    internal static bool VerifyHistoricalPlacement(Element e,HistoryRecipe r,out string failure,bool includeBoundingBox=false) {failure=null;return true;}
    internal static Element FindOriginal(Document d,string uid) {return d.GetElement(uid);}
}
internal static class ElementHistoryRestoration {
    internal static bool IsCalorifuge(string category,string name) {return category=="insulation";}
    internal static List<string> GetOrigins(Element e) {return e.Origins;}
}
internal static class ElementHistoryNativeArchive {
    internal static Element ReadMepSystemType(Element e) {return e?.SystemType;}
    internal static string ReadHistoricalTypeVersion(Element e) {return e?.HistoricalTypeVersion;}
    internal static bool HasMepSystemType(Element e) {return e is MEPCurve;}
}
'@
$checks=@'
namespace Analyse {
public static class NetworkGuardChecks {
    static int Checks;
    static void Assert(bool ok,string message) {Checks++;if(!ok) throw new Exception(message);}
    static void Reject(Action action,string message) {bool rejected=false;try {action();}catch(InvalidOperationException) {rejected=true;}Assert(rejected,message);}
    static Connector Port(Element e,int id=1) {
        var p=new Connector {Owner=e,Id=id,Domain=Domain.DomainPiping,ConnectorType=ConnectorType.End,Shape=ConnectorProfileType.Round};
        if(e is FamilyInstance f) f.MEPModel.ConnectorManager.Connectors.Add(p);else ((MEPCurve)e).ConnectorManager.Connectors.Add(p);return p;
    }
    static FamilyInstance Family(Document d,string uid) {return d.Add(new FamilyInstance {Location=new LocationPoint()},uid);}
    static HistoryRecipe Undefined() {return new HistoryRecipe {Native=new HistoryNativeArtifact {SourceSystemTypeDefined=false}};}
    public static string Run() {
        var d=new Document();var a=Family(d,"a");var b=Family(d,"b");var neighbor=Family(d,"neighbor");
        var pa=Port(a);var pb=Port(b);var pn=Port(neighbor);pa.AllRefs.Add(pn);pn.AllRefs.Add(pa);
        var guard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,b);
        Assert(guard.States.Count==3,"A direct connected neighbour must be included.");
        Assert(guard.ExistingConnections.Count==1,"Both owner views of an existing edge must be captured once.");
        ElementHistoryNetwork.VerifyConnectionGuard(d,guard);
        pa.AllRefs.Remove(pn);pn.AllRefs.Remove(pa);
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"A lost physical edge must fail even when all owner frames, ports and systems remain unchanged.");
        pa.AllRefs.Add(pn);pn.AllRefs.Add(pa);
        pa.AllRefs.Add(pb);pb.AllRefs.Add(pa);ElementHistoryNetwork.VerifyConnectionGuard(d,guard);
        Assert(true,"The requested new edge may be added while existing edges remain intact.");pa.AllRefs.Remove(pb);pb.AllRefs.Remove(pa);
        var far=Family(d,"far");var pf=Port(far);pn.AllRefs.Add(pf);pf.AllRefs.Add(pn);
        var neighbourEdges=ElementHistoryNetwork.CaptureConnectionGuard(d,a,b);pn.AllRefs.Remove(pf);pf.AllRefs.Remove(pn);
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,neighbourEdges),"The solver cannot remove a guarded neighbour's edge to the remaining network.");
        a.Frame.Origin=new XYZ(1,0,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"An insertion-frame shift must fail even if ports remain coincident.");a.Frame.Origin=XYZ.Zero;
        a.Frame.BasisX=new XYZ(-1,0,0);a.Frame.BasisY=new XYZ(0,-1,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"A 180-degree inversion with identical ports must fail.");a.Frame.BasisX=XYZ.BasisX;a.Frame.BasisY=XYZ.BasisY;
        ((LocationPoint)neighbor.Location).Point=new XYZ(0,.1,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"Direct-neighbour solver movement must fail.");((LocationPoint)neighbor.Location).Point=XYZ.Zero;
        pa.PipeSystemType=PipeSystemType.SupplyHydronic;Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"Supply cannot silently replace historical Return.");pa.PipeSystemType=PipeSystemType.ReturnHydronic;
        pa.Radius=.06;Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"A section change must fail.");pa.Radius=.05;
        pa.ConnectorType=ConnectorType.Curve;Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"A connector kind change must fail.");pa.ConnectorType=ConnectorType.End;
        pa.Origin=new XYZ(.00002,0,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"A 2e-5 ft displacement must exceed the 1e-5 ft gate.");pa.Origin=XYZ.Zero;
        Port(a,2);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,guard),"Additional/ambiguous ports must fail.");a.MEPModel.ConnectorManager.Connectors.RemoveAt(1);
        var insulation=d.Add(new PipeInsulation(),"insulation");var pi=new Connector {Owner=insulation,ConnectorType=ConnectorType.Physical};pa.AllRefs.Add(pi);
        var links=ElementHistoryNetwork.CaptureConnections(a);Assert(links.Count==1 && links[0].Peer=="neighbor","Insulation refs must be excluded while the real physical peer remains captured.");
        Assert(ElementHistoryNetwork.CaptureConnectionGuard(d,a,b).States.Count==3,"Insulation must not join the connection guard.");
        var insulationGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,b);pa.AllRefs.Remove(pi);
        ElementHistoryNetwork.VerifyConnectionGuard(d,insulationGuard);Assert(true,"Excluded insulation links are outside the preserved physical edge set.");
        Assert(ElementHistoryNetwork.CaptureConnections(insulation).Count==0,"Insulation owners must have no captured restore edges.");
        var pipe=d.Add(new Pipe {Location=new LocationCurve()},"pipe");var pp=Port(pipe);var curveGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,pipe,null);
        ((LocationCurve)pipe.Location).Curve.Middle=new XYZ(.5,.01,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,curveGuard),"Unchanged endpoints must not mask a curved midpoint change.");
        var flex=d.Add(new FlexDuct {Points=new List<XYZ>{XYZ.Zero,XYZ.BasisX}},"flex");Port(flex);var flexGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,flex,null);
        flex.Points[1]=new XYZ(1,.01,0);Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,flexGuard),"Flexible intermediate geometry must remain exact.");
        var supply=d.Add(new PipingSystemType {SystemClassification=MEPSystemClassification.SupplyHydronic},"supply");
        var ret=d.Add(new PipingSystemType {SystemClassification=MEPSystemClassification.ReturnHydronic},"return");
        var ret2=d.Add(new PipingSystemType {SystemClassification=MEPSystemClassification.ReturnHydronic},"return2");
        a.SystemType=ret;var systemGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,null);a.SystemType=supply;
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,systemGuard),"A defined system UID must remain exact even if connector geometry is identical.");a.SystemType=ret;
        var undefinedGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,null,Undefined());a.SystemType=null;
        ElementHistoryNetwork.VerifyConnectionGuard(d,undefinedGuard);Assert(true,"An explicitly undefined historical type may be unset while class and geometry remain stable.");
        a.SystemType=ret;var fallbackRecipe=new HistoryRecipe {OpeningFallback=new HistoryNativeArtifact {SourceSystemTypeDefined=false}};
        var fallbackGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,null,fallbackRecipe);a.SystemType=null;
        ElementHistoryNetwork.VerifyConnectionGuard(d,fallbackGuard);Assert(fallbackGuard.States[0].AllowUndefinedSystemNormalization,"A direct recipe must retain undefined-system evidence from its opening fallback.");
        a.SystemType=ret;var primaryDefined=new HistoryRecipe {Native=new HistoryNativeArtifact {SourceSystemTypeDefined=true},OpeningFallback=fallbackRecipe.OpeningFallback};
        var primaryGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,null,primaryDefined);a.SystemType=null;
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,primaryGuard),"The primary native receipt must take precedence over contradictory fallback metadata.");
        var historical=links[0].Port;historical.Classification="SupplyHydronic";
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,ElementHistoryNetwork.CaptureConnectionGuard(d,a,null,null,null,historical)),"Historical endpoint semantics must be checked independently from the current snapshot.");
        // Exercise the real private sizing selector through a test-only partial wrapper.
        var target=new HistoryPort {Point=new[]{0d,0d,0d},Domain=(int)Domain.DomainPiping,Classification="ReturnHydronic",SystemType="return"};
        Assert(ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe())==ret.Id,"Sizing must choose the retained return system, never the first available supply system.");
        target.SystemType="missing";Reject(()=>ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe()),"A missing retained system must not fall back to another type.");
        target.SystemType=null;Reject(()=>ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe()),"Two systems with the same classification require identity evidence.");
        var identified=d.Add(new Pipe {SystemType=ret},"identified");
        var old=new HistoryRecipe {Connections=new List<HistoryConnection>{new HistoryConnection {Port=target,Peer="identified",PeerPort=new HistoryPort()}}};
        Assert(ElementHistoryNetwork.TestSizing(d,target,old)==ret.Id,"Legacy recipes may use the identified historical curve, not arbitrary collection order.");
        var evidence=new HistoryRecipe {Connections=new List<HistoryConnection>{new HistoryConnection {Port=target,PeerPort=new HistoryPort {SystemType="return"}}}};
        Assert(ElementHistoryNetwork.TestSizing(d,target,evidence)==ret.Id,"Persisted peer system identity must disambiguate classification.");
        evidence.Connections.Add(new HistoryConnection {Port=target,PeerPort=new HistoryPort {SystemType="return2"}});
        Reject(()=>ElementHistoryNetwork.TestSizing(d,target,evidence),"Conflicting peer types must fail instead of choosing the first.");
        target.Classification="Fitting";Reject(()=>ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe()),"An undefined/fitting classification does not identify a project system.");
        d.Elements.Remove(ret2);target.Classification="ReturnHydronic";
        Assert(ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe())==ret.Id,"A unique matching classification may size an otherwise unidentified fitting.");
        ret.HistoricalTypeVersion="v-current";ret.Origins.Add("historical-return");
        Assert(!ElementHistoryNetwork.HistoricalSystemTypeMatches(ret,"historical-return","v-saved"),"An origin alone must not authorize a different historical type version.");
        ret.HistoricalTypeVersion="v-saved";ret.Origins.Clear();
        Assert(!ElementHistoryNetwork.HistoricalSystemTypeMatches(ret,"historical-return","v-saved"),"A matching version without the source origin must be refused.");
        ret.Origins.Add("historical-return");ret.Origins.Add(ret.UniqueId);
        Assert(ElementHistoryNetwork.HistoricalSystemTypeMatches(ret,"historical-return","v-saved"),"The exact historical version with its source origin must be accepted after UID remapping.");
        target.SystemType="historical-return";target.SystemVersion="v-saved";
        Assert(ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe())==ret.Id,"Sizing must locate a versioned historical type through origins when its runtime UID differs.");
        target.SystemVersion="v-other";
        Reject(()=>ElementHistoryNetwork.TestSizing(d,target,new HistoryRecipe()),"Sizing must refuse a retained port whose historical system version differs.");
        a.SystemType=ret;
        var versionedPort=new HistoryPort {Point=new[]{0d,0d,0d},Direction=new[]{0d,0d,1d},Domain=(int)Domain.DomainPiping,Shape=(int)ConnectorProfileType.Round,ConnectorType=(int)ConnectorType.End,Diameter=.1,Classification="ReturnHydronic",SystemType="historical-return",SystemVersion="v-other"};
        var portVersionGuard=ElementHistoryNetwork.CaptureConnectionGuard(d,a,null,null,null,versionedPort);
        Reject(()=>ElementHistoryNetwork.VerifyConnectionGuard(d,portVersionGuard),"A port with matching geometry and origin but a different system version must fail the connection guard.");
        ret.HistoricalTypeVersion=null;ret.Origins.Clear();a.SystemType=null;target.SystemVersion=null;target.SystemType=null;
        var duct=d.Add(new Duct(),"duct");var pd=Port(duct);pd.Domain=Domain.DomainHvac;
        var exhaust=d.Add(new MechanicalSystemType {SystemClassification=MEPSystemClassification.ExhaustAir},"exhaust");duct.SystemType=exhaust;
        duct.Parameter=new Parameter {OnSet=id=>duct.SystemType=null};string failure;
        Assert(ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,new HistoryRecipe(),out failure) && duct.Parameter.Calls==0,"Defined/unknown source recipes must not be unset.");
        duct.Parameter.IsReadOnly=true;Assert(!ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && failure!=null && duct.SystemType==exhaust,"A read-only parameter is an explicit failure.");duct.Parameter.IsReadOnly=false;
        duct.Parameter.SetResult=false;int rb=d.Rollbacks;
        Assert(!ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && d.Rollbacks==rb+1 && duct.SystemType==exhaust,"A false setter must fail and roll back.");duct.Parameter.SetResult=true;
        duct.Parameter.OnSet=id=>{};rb=d.Rollbacks;
        Assert(!ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && d.Rollbacks==rb+1,"A true setter without an actual undefined type must fail and roll back.");
        duct.Location=new LocationPoint();duct.Parameter.OnSet=id=>{duct.SystemType=null;((LocationPoint)duct.Location).Point=XYZ.BasisX;};rb=d.Rollbacks;
        Assert(!ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && d.Rollbacks==rb+1 && duct.SystemType==exhaust && ((LocationPoint)duct.Location).Point.DistanceTo(XYZ.Zero)==0,"Unsetting cannot authorize solver displacement and must roll it back.");
        duct.Parameter.OnSet=id=>duct.SystemType=null;int commits=d.Commits;
        Assert(ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && failure==null && duct.SystemType==null && d.Commits==commits+1,"A genuinely undefined type with unchanged ExhaustAir ports must commit.");
        Assert(pd.DuctSystemType==DuctSystemType.ExhaustAir,"Unsetting the type must preserve source connector classification.");
        duct.SystemType=exhaust;commits=d.Commits;
        Assert(ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,fallbackRecipe,out failure) && duct.SystemType==null && d.Commits==commits+1,"A direct recipe must unset a copied system using its opening-fallback evidence.");
        duct.SystemType=exhaust;int calls=duct.Parameter.Calls;
        Assert(ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,primaryDefined,out failure) && duct.SystemType==exhaust && duct.Parameter.Calls==calls,"A defined primary native receipt must prevent unsetting even if an older fallback says undefined.");
        duct.SystemType=exhaust;pd.AllRefs.Add(pb);pb.AllRefs.Add(pd);duct.Parameter.OnSet=id=>{duct.SystemType=null;pd.AllRefs.Remove(pb);pb.AllRefs.Remove(pd);};rb=d.Rollbacks;
        Assert(!ElementHistoryNetwork.RestoreUndefinedSystemType(d,duct,Undefined(),out failure) && d.Rollbacks==rb+1 && failure.Contains("connexion physique"),"Unsetting a system cannot silently disconnect its existing physical edge.");
        return Checks+" assertions passed against extracted production guard, selector, connection capture and port matching.";
    }
}
internal static partial class ElementHistoryNetwork {
    internal static ElementId TestSizing(Document doc,HistoryPort port,HistoryRecipe recipe) {return SizingSystemType(doc,port,recipe);}
}
}
'@
$guardBody=[regex]::Replace($guard,'(?m)^using [^;]+;\s*','')
$compiled=$fixtures+($types -join "`n")+"`ninternal static partial class ElementHistoryNetwork {`n"+($methods -join "`n")+"`n}}`n"+$guardBody+$checks
Add-Type -TypeDefinition $compiled
Write-Output ('PASS: '+[Analyse.NetworkGuardChecks]::Run())
