using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesCustomGroupTests
{
 public partial class Probe
 {
  List<ElementId> benchWalls;List<ElementId> benchViews;
  string benchMode;int benchStep,benchStable;long benchStart,benchReady,benchRevision;
  DateTime benchNext;readonly List<object> benchSamples=new List<object>();
  readonly List<string> benchFailures=new List<string>();
  object savedAudit;long lastLayoutRevision;int nativeRebuilds;
  bool combined;
  readonly List<object> benchTraces=new List<object>();

  bool ExtraCommand(UIApplication app,string command)
  {
   if(command=="edit-baseline")
   {Write("baseline.json",Snapshot(app));var target=TargetElement(app);var parameter=TargetParameter(app);var grid=NativePropertiesGrid.Find(app);Write("report-"+command+".json",new{command,owner=target.UniqueId,id=parameter.Id.Value,parameter.Definition.Name,row=grid.FindParameterRow(parameter.Id.Value),editing=grid.IsEditing});return true;}
   if(command=="edit-verify")
   {VerifyWrite(app);File.Copy(Path.Combine(dir,"verify-write.json"),Path.Combine(dir,"report-"+command+".json"),true);return true;}
   if(command=="edit-unchanged")
   {VerifyUnchanged(app);File.Copy(Path.Combine(dir,"verify-unchanged.json"),Path.Combine(dir,"report-"+command+".json"),true);return true;}
   if(command=="prepare-plans")
   {
    var doc=app.ActiveUIDocument.Document;
    using(var tx=new Transaction(doc,"Disposable structural plans for palette qualification"))
    {
     tx.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.StructuralPlan);
     var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).Take(2).ToArray();
     if(levels.Length<2)throw new InvalidOperationException("Two levels required.");
     for(int i=0;i<2;i++){var v=ViewPlan.Create(doc,type.Id,levels[i].Id);v.Name="BIMaestro TEST structure "+(i+1);v.ViewTemplateId=ElementId.InvalidElementId;}
     tx.Commit();
    }
    Write("report-"+command+".json",new{command,note="Only the disposable copy is changed. Integrity baseline must be captured after this setup."});return true;
   }
   if(command=="inventory")
   {
    var doc=app.ActiveUIDocument.Document;
    benchWalls=new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w=>!w.IsStackedWallMember).Take(12).Select(w=>w.Id).ToList();
    benchViews=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v=>!v.IsTemplate)
     .OrderBy(v=>v.Name.StartsWith("BIMaestro TEST structure ",StringComparison.Ordinal)?0:v.ViewType==ViewType.EngineeringPlan?1:2).Take(2).Select(v=>v.Id).ToList();
    Write("report-inventory.json",new{command,pid=Process.GetCurrentProcess().Id,version=app.Application.VersionNumber,build=app.Application.VersionBuild,path=doc.PathName,
     elements=new FilteredElementCollector(doc).GetElementCount(),walls=new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount(),
     plans=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).GetElementCount(),
     chosenWalls=benchWalls.Select(id=>new{id=id.Value,doc.GetElement(id).Name}),chosenViews=benchViews.Select(id=>new{id=id.Value,doc.GetElement(id).Name,type=((View)doc.GetElement(id)).ViewType.ToString(),template=((View)doc.GetElement(id)).ViewTemplateId.Value})});return true;
   }
   if(command=="compact-audit-before"||command=="compact-audit-after")
   {
    var audit=CompactAudit(app);if(command.EndsWith("before"))savedAudit=audit;
    Write("report-"+command+".json",new{command,time=DateTime.UtcNow,audit,unchanged=savedAudit!=null&&Newtonsoft.Json.JsonConvert.SerializeObject(savedAudit)==Newtonsoft.Json.JsonConvert.SerializeObject(audit)});return true;
   }
   if(command=="setup-combined")
   {
    PropertiesAppearanceService.CustomLayoutOwnsGrid=NativeGroupExperiment.OwnsGrid;
    PropertiesAppearanceService.CustomRowColors=NativeGroupExperiment.RowColors;
    var phase=GroupTypeId.Phasing.TypeId;
    var profiles=new[]{"cat:-2000011","cat:-2000279"}.Select(key=>{
     var p=new AppearanceProfile{Key=key};p.Layout.Groups.Add(phase);
     p.Rules.Add(new AppearanceRule{Kind="group",TargetKey=phase,Light=new PaletteColors{Text="#155C42",Background="#DCEEE3"}});return p;}).ToArray();
    PropertiesAppearanceService.Commit(profiles,true);combined=active=autoGroup=true;
    PropertiesAppearanceService.NotifyContextChanging();
    Write("report-"+command+".json",new{command,combined,active,autoGroup});return true;
   }
   if(command=="compact-state")
   {
    var grid=NativePropertiesGrid.Find(app);var owner=NativeGroupExperiment.SelectedOwner(app);
    var context=AppearanceContext.Capture(app);
    Write("report-"+command+".json",new{command,time=DateTime.UtcNow,combined,active,autoGroup,owner=owner?.UniqueId,selection=app.ActiveUIDocument.Selection.GetElementIds().Select(id=>id.Value),
     view=app.ActiveUIDocument.ActiveView.Name,status=PropertiesAppearanceService.Status,layout=PropertiesPaletteLayout.Status,
     header=NativeGroupExperiment.HeaderTest(grid),state=NativeGroupExperiment.StateInfo,modelRevision=NativeGroupExperiment.ModelRevision,
     rows=grid==null?null:Enumerable.Range(1,(int)grid.RowCount).Select(row=>new{row,label=grid.Value((uint)row,1),value=grid.Value((uint)row,2),marker=grid.Value((uint)row,3)}).ToArray(),
     mappings=grid==null?null:context.Parameters.Where(p=>p.NativeParameterId.HasValue).Select(p=>new{id=p.NativeParameterId,row=grid.FindParameterRow(p.NativeParameterId.Value),label=p.NativeName}).ToArray(),metrics=PropertiesAppearanceMetrics.Snapshot()});return true;
   }
   if(command=="bench-native"||command=="bench-combined")
   {
    if(benchWalls==null||benchViews==null||benchWalls.Count<2||benchViews.Count<2)throw new InvalidOperationException("Run inventory: two walls and two editable plans required.");
    benchMode=command;benchStep=-1;benchStable=0;benchStart=0;benchSamples.Clear();benchFailures.Clear();benchTraces.Clear();nativeRebuilds=0;lastLayoutRevision=NativeGroupExperiment.LayoutRevision;
    combined=command=="bench-combined";autoGroup=combined;active=combined;
    if(!combined){PropertiesAppearanceService.SetPreview(new AppearanceProfile[0],false);PropertiesAppearanceService.ProcessIdling(app);}
    else {PropertiesAppearanceService.CancelPreview();PropertiesAppearanceService.NotifyContextChanging();}
    benchRevision=NativeGroupExperiment.ModelRevision;PropertiesAppearanceMetrics.Reset();benchNext=DateTime.UtcNow.AddSeconds(2);
    Write("report-"+command+".json",new{command,status="started",steps=30,measurement="API selection/view request to first verified native layout, including poll scheduling; not physical click latency"});return true;
   }
   if(command=="bench-status")
   {Write("report-"+command+".json",new{command,benchMode,benchStep,samples=benchSamples.Count,failures=benchFailures,combined,autoGroup,metrics=PropertiesAppearanceMetrics.Snapshot()});return true;}
   if(command=="select-plan")
   {if(benchViews==null||benchViews.Count==0)throw new InvalidOperationException("Run inventory first");app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);app.ActiveUIDocument.RequestViewChange((View)app.ActiveUIDocument.Document.GetElement(benchViews[0]));Write("report-"+command+".json",new{command});return true;}
   return false;
  }
  object CompactAudit(UIApplication app)
  {
   var doc=app.ActiveUIDocument.Document;long values=0;int elements=0;
   using(var valueHash=SHA256.Create())using(var schemaHash=SHA256.Create())
   {
    Action<HashAlgorithm,string> append=(hash,s)=>{byte[] bytes=Encoding.UTF8.GetBytes(s+"\n");hash.TransformBlock(bytes,0,bytes.Length,null,0);};
    foreach(var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements()).OrderBy(e=>e.Id.Value))
    {
     elements++;
     foreach(Parameter p in e.Parameters.Cast<Parameter>().OrderBy(p=>p.Id.Value).ThenBy(p=>p.Definition.Name,StringComparer.Ordinal))
     {
      string key=e.UniqueId+"|"+p.Id.Value+"|"+p.Definition.Name;string value;
      try{switch(p.StorageType){case StorageType.String:value=p.AsString()??"";break;case StorageType.Integer:value=p.AsInteger().ToString();break;case StorageType.Double:value=p.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;case StorageType.ElementId:value=p.AsElementId().Value.ToString();break;default:value="";break;}}catch(Exception ex){value="ERROR:"+ex.GetType().Name;}
      append(valueHash,key+"|"+value);append(schemaHash,key+"|"+p.StorageType+"|"+p.IsReadOnly+"|"+p.Definition.GetGroupTypeId().TypeId+"|"+(p.IsShared?p.GUID.ToString():""));values++;
     }
    }
    valueHash.TransformFinalBlock(new byte[0],0,0);schemaHash.TransformFinalBlock(new byte[0],0,0);
    return new{elements,values,valueHash=Convert.ToHexString(valueHash.Hash),schemaHash=Convert.ToHexString(schemaHash.Hash)};
   }
  }
  void BenchmarkTick(UIApplication app)
  {
   if(benchMode==null)return;
   if(DateTime.UtcNow<benchNext)return;
   if(NativeGroupExperiment.ModelRevision!=benchRevision){FinishBenchmark("Model changed during read-only transitions");return;}
   if(combined&&!autoGroup){FinishBenchmark("Custom group automatic guard stopped");return;}
   if(benchStart==0)
   {
    if(++benchStep>=30){FinishBenchmark(null);return;}
    var ui=app.ActiveUIDocument;int target=benchStep%6;benchStable=0;benchReady=0;
    benchStart=Stopwatch.GetTimestamp();
    if(target<2)ui.Selection.SetElementIds(new[]{benchWalls[target]});
    else if(target==2)ui.Selection.SetElementIds(benchWalls.Take(2).ToArray());
    else if(target==3)ui.Selection.SetElementIds(new ElementId[0]);
    else {ui.Selection.SetElementIds(new ElementId[0]);ui.RequestViewChange((View)ui.Document.GetElement(benchViews[target-4]));}
    return;
   }
   var grid=NativePropertiesGrid.Find(app);bool ready=false;
   int seenRow=0;string seenLabel=null,expectedLabel=null;bool editing=grid?.IsEditing??false;
   if(grid!=null&&!editing)
   {
    var owner=NativeGroupExperiment.SelectedOwner(app);
    if(owner!=null)
    {
     var expected=owner is Wall?owner.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET):owner.get_Parameter(BuiltInParameter.VIEW_SCALE);
     int row=expected==null?0:grid.FindParameterRow(expected.Id.Value);
     seenRow=row;expectedLabel=expected?.Definition.Name;seenLabel=row>0?grid.Value((uint)row,1):null;
     ready=row>0&&AppearancePreferences.Normalize(grid.Value((uint)row,1))==AppearancePreferences.Normalize(expected.Definition.Name);
     if(combined)ready=ready&&NativeGroupExperiment.OwnsGrid(grid)&&PropertiesAppearanceService.HasCurrentColorPlan&&PropertiesAppearanceService.LastColoredPaint>=benchStart;
    }
   }
   if(benchTraces.Count<300)benchTraces.Add(new{step=benchStep,time=DateTime.UtcNow,ready,editing,seenRow,seenLabel,expectedLabel,owner=NativeGroupExperiment.SelectedOwner(app)?.UniqueId});
   if(ready)
   {
    if(benchReady==0)benchReady=Stopwatch.GetTimestamp();
    if(++benchStable>=3)
    {
     benchSamples.Add(new{step=benchStep,target=benchStep%6,readyMs=Math.Round((benchReady-benchStart)*1000.0/Stopwatch.Frequency,3),stableMs=Math.Round((Stopwatch.GetTimestamp()-benchStart)*1000.0/Stopwatch.Frequency,3),layoutRevision=NativeGroupExperiment.LayoutRevision});
     benchStart=0;benchNext=DateTime.UtcNow.AddMilliseconds(250);
    }
   }
   else {benchStable=0;benchReady=0;}
   if(benchStart!=0&&(Stopwatch.GetTimestamp()-benchStart)*1000.0/Stopwatch.Frequency>15000)FinishBenchmark("Native palette failed to settle within 15 seconds at step "+benchStep);
   if(lastLayoutRevision!=NativeGroupExperiment.LayoutRevision){nativeRebuilds++;lastLayoutRevision=NativeGroupExperiment.LayoutRevision;}
  }
  void FinishBenchmark(string failure)
  {
   if(failure!=null)benchFailures.Add(failure);
   Write("result-"+benchMode+".json",new{time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,version="2025",mode=benchMode,combined,autoGroup,pass=failure==null&&benchSamples.Count==30,
    samples=benchSamples,failures=benchFailures,traces=benchTraces,nativeRebuilds,modelRevision=NativeGroupExperiment.ModelRevision,metrics=PropertiesAppearanceMetrics.Snapshot(),
    limitations="Read-only API-driven transitions; includes 250ms scheduling. Does not measure physical input latency or prove absence of visual flashing."});
   benchMode=null;
  }
 }
}
