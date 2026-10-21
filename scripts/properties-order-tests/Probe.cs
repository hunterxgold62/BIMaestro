using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesAppearance { internal static class PropertiesAppearanceCommand { internal static void Shutdown() {} } }
namespace BIMaestro.PropertiesOrderTests
{
 public class Probe : IExternalApplication
 {
  string dir; DateTime next; bool active, exitAfterClose;
  NativePropertiesGrid diagnosticGrid; DispatcherTimer diagnosticTimer;
  sealed class WriteExpectation { public string Parameter="ALL_MODEL_INSTANCE_COMMENTS"; public string Value; public double Tolerance=1e-9; public List<string> AllowedDependentParameters=new List<string>(); }
  string FixturePath => Path.Combine(dir,"PropertiesOrderFixture.rvt");
  public Result OnStartup(UIControlledApplication app) { dir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); File.WriteAllText(Path.Combine(dir,"started-"+Process.GetCurrentProcess().Id+".txt"),app.ControlledApplication.VersionNumber); app.Idling+=Idle; return Result.Succeeded; }
  public Result OnShutdown(UIControlledApplication app) { app.Idling-=Idle;if(diagnosticTimer!=null){diagnosticTimer.Stop();diagnosticTimer.Tick-=DiagnosticTick;}diagnosticGrid=null;PropertiesAppearanceService.Shutdown(); return Result.Succeeded; }
  void Idle(object sender, IdlingEventArgs args)
  {
   args.SetRaiseWithoutDelay();
   if(DateTime.UtcNow<next)return; next=DateTime.UtcNow.AddMilliseconds(500);
   int pid; var pidFile=Path.Combine(dir,"target-pid.txt");
   if(!File.Exists(pidFile)||!int.TryParse(File.ReadAllText(pidFile).Trim(),out pid)||pid!=Process.GetCurrentProcess().Id)return;
   var app=sender as UIApplication; if(app==null||app.Application.VersionNumber!=File.ReadAllText(Path.Combine(dir,"version.txt")).Trim())return;
   AppearanceStore.TestFilePath=Path.Combine(dir,"isolated-preferences.json");
   string commandFile=Path.Combine(dir,"control.txt");
   try {
    if(exitAfterClose && app.ActiveUIDocument==null && app.Application.Documents.Size==0) { exitAfterClose=false;var exit=RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);if(app.CanPostCommand(exit))app.PostCommand(exit);return; }
    if(File.Exists(commandFile)) { string command=File.ReadAllText(commandFile).Trim(); File.Delete(commandFile); Execute(app,command); }
    if(active && IsFixture(app))PropertiesAppearanceService.ProcessIdling(app);
   } catch(Exception ex){Write("error.json",new { time=DateTime.UtcNow,error=ex.ToString() });}
  }
  bool IsFixture(UIApplication app) => app.ActiveUIDocument!=null && string.Equals(app.ActiveUIDocument.Document.PathName,FixturePath,StringComparison.OrdinalIgnoreCase);
  void RequireFixture(UIApplication app) { if(!IsFixture(app))throw new InvalidOperationException("Refusing nonfixture document"); }
  void Execute(UIApplication app,string command)
  {
   if(command=="fixture") { Fixture(app); Write("fixture.json",new {path=FixturePath,pid=Process.GetCurrentProcess().Id});return; }
   RequireFixture(app);
   if(command=="show-properties") {var pane=app.GetDockablePane(DockablePanes.BuiltInDockablePanes.PropertiesPalette);if(!pane.IsShown()){var show=RevitCommandId.LookupPostableCommandId(PostableCommand.TogglePropertiesPalette);if(app.CanPostCommand(show))app.PostCommand(show);}}
   else if(command=="shutdown-fixture") {active=false;PropertiesAppearanceService.Shutdown();exitAfterClose=app.Application.Documents.Size==1;var close=RevitCommandId.LookupPostableCommandId(PostableCommand.Close);if(app.CanPostCommand(close))app.PostCommand(close);Write("shutdown-requested.json",new{exitAfterClose,note="Close posted through native UI; discard fixture changes in native save prompt."});return;}
   else if(command=="select-wall") { var wall=Walls(app).First();app.ActiveUIDocument.Selection.SetElementIds(new[]{wall.Id}); }
   else if(command=="select-wall2") {var wall=Walls(app).Skip(1).First();app.ActiveUIDocument.Selection.SetElementIds(new[]{wall.Id});}
   else if(command=="select-multiple")app.ActiveUIDocument.Selection.SetElementIds(Walls(app).Take(2).Select(w=>w.Id).ToArray());
   else if(command=="select-view")app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);
   else if(command=="baseline")Write("baseline.json",Snapshot(app));
   else if(command=="move" || command=="configure") { Configure(app);active=true;PropertiesAppearanceService.ProcessIdling(app); }
   else if(command=="raw-move")RawMove(app);
   else if(command=="restore") {active=false;PropertiesAppearanceService.ResetAll();PropertiesAppearanceService.ProcessIdling(app);}
   else if(command=="verify-write")VerifyWrite(app);
   else if(command=="verify-unchanged")VerifyUnchanged(app);
   else if(command=="inspect"||command=="report"||command=="native-state"){}
   else throw new InvalidOperationException("Unknown command "+command);
   Report(app,command);
  }
  IEnumerable<Autodesk.Revit.DB.Wall> Walls(UIApplication app) => new FilteredElementCollector(app.ActiveUIDocument.Document).OfClass(typeof(Autodesk.Revit.DB.Wall)).Cast<Autodesk.Revit.DB.Wall>();
  Autodesk.Revit.DB.Wall Wall(UIApplication app) {var selection=app.ActiveUIDocument.Selection.GetElementIds();return selection.Count==1?app.ActiveUIDocument.Document.GetElement(selection.First()) as Autodesk.Revit.DB.Wall??Walls(app).First():Walls(app).First();}
  void Fixture(UIApplication app)
  {
   if(File.Exists(FixturePath)){app.OpenAndActivateDocument(FixturePath);return;}
   var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
   try { using(var tx=new Transaction(doc,"Disposable order fixture")) {tx.Start();var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();var second=Level.Create(doc,level.Elevation+15);second.Name="ORDER_LEVEL_2";var type=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First();var wall=Autodesk.Revit.DB.Wall.Create(doc,Line.CreateBound(XYZ.Zero,new XYZ(20,0,0)),type.Id,level.Id,10,0,false,false);wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("ORDER_BASELINE");var wall2=Autodesk.Revit.DB.Wall.Create(doc,Line.CreateBound(new XYZ(0,10,0),new XYZ(20,10,0)),type.Id,level.Id,8,0,false,false);wall2.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("ORDER_SECOND_WALL");tx.Commit();}doc.SaveAs(FixturePath,new SaveAsOptions{OverwriteExistingFile=true});}finally{doc.Close(false);}app.OpenAndActivateDocument(FixturePath);
  }
  void Configure(UIApplication app)
  {
   var context=AppearanceContext.ForEditor(app);var comment=TargetParameter(app);
   var target=context.Parameters.Single(p=>p.NativeName==AppearancePreferences.Normalize(comment.Definition.Name));
   var rows=context.PreviewRows;var row=rows.Single(r=>r.ParameterKey==target.Key);
   var profile=new AppearanceProfile {Key=context.CategoryKey};profile.Layout.Groups.Add(target.GroupKey);profile.Layout.Parameters[target.GroupKey]=new List<string>{row.OrderKey};
   PropertiesAppearanceService.Commit(new[]{profile},true);Write("configured.json",new {group=target.GroupKey,parameter=target.Key,label=row.Label});
  }
  void RawMove(UIApplication app)
  {
   Report(app,"before-raw-move");
   active=false;var grid=NativePropertiesGrid.Find(app);var context=AppearanceContext.Capture(app);var comment=TargetParameter(app);string label=comment.Definition.Name.Trim();uint row=0,start=0,end=grid.RowCount;
   for(uint i=1;i<=grid.RowCount;i++){if(grid.Value(i,3).Trim()=="^") { if(row>0){end=i-1;break;}start=i;}if(grid.Value(i,1).Trim()==label)row=i;}
   if(row==0||start==0)throw new Exception("Comments row/group not found");
   bool parameterMoved=row==start+1||grid.MoveRows(row,row,start+1);
   bool groupMoved=start==1||grid.MoveRows(start,end,1);
   grid.Redraw();Write("raw-move.json",new {start,end,row,parameterMoved,groupMoved});
  }
  Dictionary<string,string> Snapshot(UIApplication app)
  {
   var doc=app.ActiveUIDocument.Document;var elements=new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements());var result=new Dictionary<string,string>();
   foreach(var element in elements)foreach(Parameter p in element.Parameters) {string value;try{switch(p.StorageType){case StorageType.String:value=p.AsString()??"";break;case StorageType.Double:value=p.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;case StorageType.Integer:value=p.AsInteger().ToString(CultureInfo.InvariantCulture);break;case StorageType.ElementId:value=p.AsElementId().ToString();break;default:value="";break;}}catch(Exception ex){value="<unreadable:"+ex.GetType().Name+">";}result[element.UniqueId+"|"+p.Id+"|"+p.Definition.Name]=value;}
   return result;
  }
  void VerifyWrite(UIApplication app)
  {
   var baseline=JsonConvert.DeserializeObject<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dir,"baseline.json")));var after=Snapshot(app);var config=Expectation();var expected=config.Value??File.ReadAllText(Path.Combine(dir,"expected-value.txt")).TrimEnd('\r','\n');var wall=Wall(app);var comment=TargetParameter(app);string key=wall.UniqueId+"|"+comment.Id+"|"+comment.Definition.Name;
   var deltas=baseline.Keys.Union(after.Keys).Where(k=>!baseline.ContainsKey(k)||!after.ContainsKey(k)||baseline[k]!=after[k]).Select(k=>new {key=k,before=baseline.ContainsKey(k)?baseline[k]:null,after=after.ContainsKey(k)?after[k]:null}).ToArray();
   bool expectedMatches=after.ContainsKey(key)&&after[key]==expected;
   if(comment.StorageType==StorageType.Double){double number;expectedMatches=double.TryParse(expected,NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&Math.Abs(comment.AsDouble()-number)<=config.Tolerance;}
   var allowed=new HashSet<string>();
   if(comment.StorageType==StorageType.Double)foreach(string name in config.AllowedDependentParameters){var p=wall.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter),name));if(p==null)throw new Exception("Unknown allowed dependent parameter "+name);allowed.Add(wall.UniqueId+"|"+p.Id+"|"+p.Definition.Name);}
   bool pass=expectedMatches&&deltas.Any(d=>d.key==key)&&deltas.All(d=>d.key==key||allowed.Contains(d.key));Write("verify-write.json",new {pass,parameter=config.Parameter,storage=comment.StorageType.ToString(),expected,actual=after.ContainsKey(key)?after[key]:null,allowedDependentKeys=allowed,deltas});if(!pass)throw new Exception("Native write verification failed; see verify-write.json");
  }
  WriteExpectation Expectation(){var file=Path.Combine(dir,"expected-write.json");return File.Exists(file)?JsonConvert.DeserializeObject<WriteExpectation>(File.ReadAllText(file)):new WriteExpectation();}
  Parameter TargetParameter(UIApplication app){var config=Expectation();var p=Wall(app).get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter),config.Parameter));return p??throw new Exception("Missing target parameter "+config.Parameter);}
  void VerifyUnchanged(UIApplication app)
  {
   var baseline=JsonConvert.DeserializeObject<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dir,"baseline.json")));var after=Snapshot(app);
   var deltas=baseline.Keys.Union(after.Keys).Where(k=>!baseline.ContainsKey(k)||!after.ContainsKey(k)||baseline[k]!=after[k]).Select(k=>new {key=k,before=baseline.ContainsKey(k)?baseline[k]:null,after=after.ContainsKey(k)?after[k]:null}).ToArray();
   bool pass=deltas.Length==0;Write("verify-unchanged.json",new {pass,deltas});if(!pass)throw new Exception("Unexpected parameter changes; see verify-unchanged.json");
  }
  void Report(UIApplication app,string command)
  {
   var grid=NativePropertiesGrid.Find(app);var rows=new List<object>();
   diagnosticGrid=grid;if(diagnosticTimer==null){diagnosticTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(1)};diagnosticTimer.Tick+=DiagnosticTick;diagnosticTimer.Start();}
   if(grid!=null)for(uint i=1;i<=grid.RowCount;i++){var rect=grid.Bounds(i,2);rows.Add(new {row=i,label=grid.Value(i,1),value=grid.Value(i,2),marker=grid.Value(i,3),editable=grid.CanStyle(i),rect=new {left=rect.Left,top=rect.Top,right=rect.Right,bottom=rect.Bottom}});}
   var mappings=new List<object>();var context=AppearanceContext.Capture(app);
   foreach(var parameter in context.Parameters)
   {
    string expectedLabel=parameter.Name;int separator=expectedLabel.IndexOf(" · ",StringComparison.Ordinal);if(separator>=0)expectedLabel=expectedLabel.Substring(separator+3);
    int? nativeRow=null;string actualLabel=null,error=null;bool? matches=null;
    try {if(grid==null)throw new InvalidOperationException("Native grid unavailable");if(!parameter.NativeParameterId.HasValue)throw new InvalidOperationException("No native parameter id");nativeRow=grid.FindParameterRow(parameter.NativeParameterId.Value);if(nativeRow>0){actualLabel=grid.Value((uint)nativeRow.Value,1);matches=AppearancePreferences.Normalize(actualLabel)==AppearancePreferences.Normalize(expectedLabel);}else error="Parameter absent from native grid (row="+nativeRow+")";}
    catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
    mappings.Add(new {key=parameter.Key,id=parameter.NativeParameterId,expectedLabel,nativeRow,actualLabel,matches,error});
   }
   var snapshot=Snapshot(app);var json=JsonConvert.SerializeObject(snapshot.OrderBy(p=>p.Key));string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-","");
   var nativeState=NativeState(grid);
   Write("report.json",new {command,time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,version=app.Application.VersionNumber,build=app.Application.VersionBuild,path=FixturePath,status=PropertiesAppearanceService.Status,layoutStatus=PropertiesPaletteLayout.Status,hasApplied=PropertiesPaletteLayout.HasApplied,hwnd=grid?.Window.ToInt64(),nativeState,rows,mappings,hash,parameters=snapshot});
   File.Copy(Path.Combine(dir,"report.json"),Path.Combine(dir,"report-"+command+".json"),true);
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NativeFlag(IntPtr instance);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NativeCurrentCell(IntPtr instance,ref uint row,ref uint col,int ignorePrint);
  [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
  [DllImport("user32.dll")]static extern IntPtr GetFocus();
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder value,int count);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder value,int count);
  [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out NativePropertiesGrid.Rect rect);
  object WindowState(IntPtr window){var cls=new StringBuilder(256);var text=new StringBuilder(4096);GetClassName(window,cls,cls.Capacity);GetWindowText(window,text,text.Capacity);NativePropertiesGrid.Rect rect;bool hasRect=GetWindowRect(window,out rect);return new{hwnd=window.ToInt64(),className=cls.ToString(),text=text.ToString(),visible=IsWindowVisible(window),hasRect,rect=new{left=rect.Left,top=rect.Top,right=rect.Right,bottom=rect.Bottom}};}
  void DiagnosticTick(object sender,EventArgs args)
  {
   // Native reads only on Revit's UI dispatcher. No DB/UI API call is allowed here.
   try {var grid=diagnosticGrid;if(grid==null)return;Write("timer-state.json",new{time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,state=NativeState(grid)});}
   catch(Exception ex){Write("timer-state.json",new{time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,error=ex.ToString()});}
  }
  object NativeState(NativePropertiesGrid grid)
  {
   var focus=WindowState(GetFocus());int? activeCell=null,currentResult=null;bool? editing=null;uint row=0,col=0;string error=null;var children=new List<object>();
   try{
    if(grid==null||!grid.IsCurrent())throw new InvalidOperationException("No current native grid");
    var module=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m=>m.ModuleName.Equals("og1100asu.dll",StringComparison.OrdinalIgnoreCase));
    var address=GetProcAddress(module.BaseAddress,"?IsActiveCurrentCell@CGXGridCore@@QEAAHXZ");if(address==IntPtr.Zero)throw new NotSupportedException("IsActiveCurrentCell export absent");activeCell=Marshal.GetDelegateForFunctionPointer<NativeFlag>(address)(grid.Core);
    address=GetProcAddress(module.BaseAddress,"?GetCurrentCell@CGXGridCore@@QEAAHAEAK0H@Z");if(address!=IntPtr.Zero)currentResult=Marshal.GetDelegateForFunctionPointer<NativeCurrentCell>(address)(grid.Core,ref row,ref col,0);
    editing=grid.IsEditing;NativePropertiesGrid.EnumChildWindows(grid.Window,(h,p)=>{children.Add(WindowState(h));return true;},IntPtr.Zero);
   }catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
   return new{focus,grid=grid==null?null:WindowState(grid.Window),activeCell,currentResult,currentRow=row,currentColumn=col,editing,error,children};
  }
  void Write(string file,object value)=>File.WriteAllText(Path.Combine(dir,file),JsonConvert.SerializeObject(value,Formatting.Indented));
 }
}
