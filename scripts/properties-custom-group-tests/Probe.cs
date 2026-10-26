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

namespace BIMaestro.PropertiesAppearance { internal static class PropertiesAppearanceCommand { internal static void Shutdown() {} internal static void Request(Action<UIApplication> action) { BIMaestro.PropertiesCustomGroupTests.Probe.Enqueue(action); } } }
namespace BIMaestro.PropertiesCustomGroupTests
{
 public partial class Probe : IExternalApplication
 {
  static readonly Queue<Action<UIApplication>> editorActions=new Queue<Action<UIApplication>>();
  internal static void Enqueue(Action<UIApplication> action){editorActions.Enqueue(action);}
  public static bool OpenEditor(UIApplication app)
  {
   var probe=current;if(probe==null||!probe.IsFixture(app)||app.Application.VersionNumber!="2025")return false;
   if(!File.Exists(Path.Combine(probe.dir,"target-pid.txt"))||File.ReadAllText(Path.Combine(probe.dir,"target-pid.txt")).Trim()!=Process.GetCurrentProcess().Id.ToString())return false;
   PropertiesAppearanceEditor.CustomGroupRequested=title=>Enqueue(ui=>{
    if(!probe.IsFixture(ui))throw new InvalidOperationException("Laboratory fixture only.");
    if(string.IsNullOrWhiteSpace(title)||title.Length>120||title.Contains("\n")||title.Contains("\r"))throw new InvalidOperationException("Invalid group title.");
    NativeGroupExperiment.Run(ui,probe.dir,"group-restore");
    File.WriteAllText(Path.Combine(probe.dir,"custom-title.txt"),title.Trim());probe.autoGroup=true;
   });
   var editor=new PropertiesAppearanceEditor(AppearanceContext.ForEditor(app));
   var window=new System.Windows.Window { Content=editor, Width=1280, Height=850 };
   var root=new System.Windows.Controls.DockPanel();window.Content=root;window.Resources=editor.Resources;
   var actions=new System.Windows.Controls.StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new System.Windows.Thickness(12) };
   var save=new System.Windows.Controls.Button { Content="Enregistrer mon affichage",MinWidth=210 };save.SetResourceReference(System.Windows.FrameworkElement.StyleProperty,"PrimaryButton");save.Click+=(_,__)=>editor.SaveChanges(null);actions.Children.Add(save);
   System.Windows.Controls.DockPanel.SetDock(actions,System.Windows.Controls.Dock.Bottom);root.Children.Add(actions);root.Children.Add(editor);
   window.Closed+=(_,__)=>{editor.Close();Enqueue(ui=>{PropertiesAppearanceService.CancelPreview();PropertiesAppearanceService.ProcessIdling(ui);});};
   window.Title="Apparence des propriétés · Prototype numéroté "+Path.GetFileName(probe.dir);
   new System.Windows.Interop.WindowInteropHelper(window){Owner=app.MainWindowHandle};window.Show();return true;
  }
  static Probe current;
  readonly PalettePresentationGate presentation=new PalettePresentationGate();
  bool contextDirty;
  void SelectionChanged(object sender,SelectionChangedEventArgs args){contextDirty=true;PropertiesAppearanceService.NotifyContextChanging();if(autoGroup)presentation.Hold(diagnosticGrid);if(autoGroup||benchMode!=null){next=DateTime.MinValue;workEvent?.Raise();}}

  string dir; DateTime next; bool active, exitAfterClose, appearanceIsolated, autoGroup, diagnostics;
  NativePropertiesGrid diagnosticGrid; DispatcherTimer diagnosticTimer;
  DispatcherTimer workTimer;ExternalEvent workEvent;
  sealed class PollHandler : IExternalEventHandler
  {
   readonly Probe owner;internal PollHandler(Probe owner){this.owner=owner;}
   public void Execute(UIApplication app){owner.Poll(app);}
   public string GetName()=>"BIMaestro isolated native group laboratory poll";
  }
  sealed class WriteExpectation { public string Target="wall"; public string Parameter="ALL_MODEL_INSTANCE_COMMENTS"; public string Value; public double Tolerance=1e-9; public List<string> AllowedDependentParameters=new List<string>(); }
  string FixturePath => Path.Combine(dir,"PropertiesCustomGroupFixture.rvt");
  public Result OnStartup(UIControlledApplication app) { current=this;dir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); PropertiesAppearanceMetrics.Enabled=true; File.WriteAllText(Path.Combine(dir,"started-"+Process.GetCurrentProcess().Id+".txt"),app.ControlledApplication.VersionNumber); app.Idling+=Idle;app.SelectionChanged+=SelectionChanged;app.ControlledApplication.DocumentChanged+=ModelChanged;workEvent=ExternalEvent.Create(new PollHandler(this));workTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(250)};workTimer.Tick+=WorkTick;workTimer.Start();return Result.Succeeded; }
  public Result OnShutdown(UIControlledApplication app) { app.Idling-=Idle;app.SelectionChanged-=SelectionChanged;presentation.Dispose();app.ControlledApplication.DocumentChanged-=ModelChanged;if(workTimer!=null){workTimer.Stop();workTimer.Tick-=WorkTick;}workEvent?.Dispose();if(diagnosticTimer!=null){diagnosticTimer.Stop();diagnosticTimer.Tick-=DiagnosticTick;}diagnosticGrid=null;PropertiesAppearanceService.Shutdown(); return Result.Succeeded; }
  void ModelChanged(object sender,Autodesk.Revit.DB.Events.DocumentChangedEventArgs args) { NativeGroupExperiment.RecordModelChange(); }
  void WorkTick(object sender,EventArgs args) { presentation.Expire(); if(editorActions.Count>0||autoGroup||active||benchMode!=null||exitAfterClose||File.Exists(Path.Combine(dir,"control.txt")))workEvent?.Raise(); }
  void Idle(object sender, IdlingEventArgs args) { var app=sender as UIApplication;if(app!=null)Poll(app); }
  void Poll(UIApplication app)
  {
   if(DateTime.UtcNow<next)return;
   int pid; var pidFile=Path.Combine(dir,"target-pid.txt");
   if(!File.Exists(pidFile)||!int.TryParse(File.ReadAllText(pidFile).Trim(),out pid)||pid!=Process.GetCurrentProcess().Id)return;
   if(app==null||app.Application.VersionNumber!=File.ReadAllText(Path.Combine(dir,"version.txt")).Trim())return;
   contextDirty=false;
   AppearanceStore.TestFilePath=Path.Combine(dir,"isolated-preferences.json");
   string commandFile=Path.Combine(dir,"control.txt");
   try {
    if(exitAfterClose && app.ActiveUIDocument==null && app.Application.Documents.Size==0) { exitAfterClose=false;var exit=RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);if(app.CanPostCommand(exit))app.PostCommand(exit);return; }
    if(IsFixture(app))IsolateInstalledAppearance(app);
    while(editorActions.Count>0)editorActions.Dequeue()(app);
    if(File.Exists(commandFile)) { string command=File.ReadAllText(commandFile).Trim(); File.Delete(commandFile); Execute(app,command); }
    if(active && IsFixture(app))PropertiesAppearanceService.ProcessIdling(app);
    if(autoGroup && IsFixture(app)){long timing=PropertiesAppearanceMetrics.Begin();try{long revision=NativeGroupExperiment.LayoutRevision;NativeGroupExperiment.Ensure(app,dir);if(revision!=NativeGroupExperiment.LayoutRevision&&active){PropertiesAppearanceService.NotifyContextChanging();PropertiesAppearanceService.ProcessIdling(app);}}catch(Exception recovery){autoGroup=false;Write("auto-stopped.json",new{time=DateTime.UtcNow,error=recovery.ToString()});}finally{PropertiesAppearanceMetrics.End("group.ensure",timing);}}
    if(IsFixture(app))BenchmarkTick(app);
   } catch(Exception ex){Write("error.json",new { time=DateTime.UtcNow,error=ex.ToString() });}
   finally{presentation.Release();next=contextDirty?DateTime.MinValue:DateTime.UtcNow.AddMilliseconds(250);}
  }
  bool IsFixture(UIApplication app) => app.ActiveUIDocument!=null && string.Equals(app.ActiveUIDocument.Document.PathName,FixturePath,StringComparison.OrdinalIgnoreCase);
  void RequireFixture(UIApplication app) { if(!IsFixture(app))throw new InvalidOperationException("Refusing nonfixture document"); }
  void Execute(UIApplication app,string command)
  {
   if(command=="fixture") { Fixture(app); Write("fixture.json",new {path=FixturePath,pid=Process.GetCurrentProcess().Id});return; }
   RequireFixture(app);
   IsolateInstalledAppearance(app);
   if(CustomGroupCommand(app,command))return;
   if(ExtraCommand(app,command))return;
   if(command=="audit-full"||command=="audit-fast"||command=="perf"||command=="diagnostics-on"||command=="diagnostics-off")
   {
    if(command=="audit-full"||command=="audit-fast"){NativeGroupExperiment.AutomaticFullAudit=command=="audit-full";NativeGroupExperiment.QualifyModules();PropertiesAppearanceMetrics.Reset();}
    if(command.StartsWith("diagnostics-",StringComparison.Ordinal))diagnostics=command=="diagnostics-on";
    Write("report-"+command+".json",new{command,time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,version=app.Application.VersionNumber,mode=NativeGroupExperiment.AutomaticFullAudit?"audit":"interactive",autoGroup,diagnostics,modelRevision=NativeGroupExperiment.ModelRevision,state=NativeGroupExperiment.StateInfo,metrics=PropertiesAppearanceMetrics.Snapshot()});return;
   }
   if(command=="view-demo")
   {
    var context=AppearanceContext.ForEditor(app);var phase=context.Groups.First(g=>g.Name.IndexOf("phase",StringComparison.OrdinalIgnoreCase)>=0);
    var profile=new AppearanceProfile{Key=context.CategoryKey};profile.Layout.Groups.Add(phase.Key);
    profile.Rules.Add(new AppearanceRule{Kind="group",TargetKey=phase.Key,Label=phase.Name,Light=new PaletteColors{Text="#155C42",Background="#DCEEE3"}});
    autoGroup=true;active=true;PropertiesAppearanceService.Commit(new[]{profile},true);PropertiesAppearanceService.ProcessIdling(app);
   }
   else if(command=="editor") { OpenEditor(app); }
   else if(command=="auto-on") { autoGroup=true; NativeGroupExperiment.Ensure(app,dir); }
   else if(command=="auto-off") { autoGroup=false; }
   else if(command=="group-apply") { active=false; NativeGroupExperiment.Run(app,dir,"group-insert");NativeGroupExperiment.Run(app,dir,"group-members"); }
   else if(command.StartsWith("group-",StringComparison.Ordinal)) { active=false; NativeGroupExperiment.Run(app,dir,command); }
   else if(command=="show-properties") {var pane=app.GetDockablePane(DockablePanes.BuiltInDockablePanes.PropertiesPalette);if(!pane.IsShown()){var show=RevitCommandId.LookupPostableCommandId(PostableCommand.TogglePropertiesPalette);if(app.CanPostCommand(show))app.PostCommand(show);}}
   else if(command=="shutdown-fixture") {active=false;autoGroup=false;PropertiesAppearanceService.Shutdown();exitAfterClose=app.Application.Documents.Size==1;var close=RevitCommandId.LookupPostableCommandId(PostableCommand.Close);if(app.CanPostCommand(close))app.PostCommand(close);Write("shutdown-requested.json",new{exitAfterClose,note="Close posted through native UI; discard fixture changes in native save prompt."});return;}
   else if(command=="select-wall") { var wall=Walls(app).First();app.ActiveUIDocument.Selection.SetElementIds(new[]{wall.Id}); }
   else if(command=="select-wall2") {var wall=Walls(app).Skip(1).First();app.ActiveUIDocument.Selection.SetElementIds(new[]{wall.Id});}
   else if(command=="select-multiple")app.ActiveUIDocument.Selection.SetElementIds(Walls(app).Take(2).Select(w=>w.Id).ToArray());
   else if(command=="select-view")app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);
   else if(command=="select-3d") {var doc=app.ActiveUIDocument.Document;var view=new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>!v.IsTemplate);if(view==null){using(var tx=new Transaction(doc,"Disposable 3D view")){tx.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);view=View3D.CreateIsometric(doc,type.Id);view.Name="ORDER_3D_VIEW";tx.Commit();}}app.ActiveUIDocument.Selection.SetElementIds(new ElementId[0]);app.ActiveUIDocument.RequestViewChange(view);}
   else if(command=="baseline")Write("baseline.json",Snapshot(app));
   else if(command=="rebuild-view") {var doc=app.ActiveUIDocument.Document;var p=app.ActiveUIDocument.ActiveView.get_Parameter(BuiltInParameter.VIEW_DETAIL_LEVEL);using(var tx=new Transaction(doc,"Disposable palette rebuild")){tx.Start();p.Set(p.AsInteger()==3?1:3);tx.Commit();}}
   else if(command=="move" || command=="configure" || command=="move-view-group") { Configure(app,command=="move-view-group");active=true;PropertiesAppearanceService.ProcessIdling(app); }
   else if(command=="raw-move")RawMove(app);
   else if(command=="restore") {active=false;PropertiesAppearanceService.ResetAll();PropertiesAppearanceService.ProcessIdling(app);}
   else if(command=="verify-write")VerifyWrite(app);
   else if(command=="verify-unchanged")VerifyUnchanged(app);
   else if(command=="inspect"||command=="report"||command=="native-state"){}
   else throw new InvalidOperationException("Unknown command "+command);
   Report(app,command);
  }
  FieldInfo installedPreviewField;MethodInfo installedProcess;object isolatedPreview;
  void IsolateInstalledAppearance(UIApplication app)
  {
   if(appearanceIsolated)
   {
    if(installedPreviewField==null||ReferenceEquals(installedPreviewField.GetValue(null),isolatedPreview))return;
    var grid=NativePropertiesGrid.Find(app);if(grid==null||grid.IsEditing)return;
    installedPreviewField.SetValue(null,isolatedPreview);installedProcess.Invoke(null,new object[]{app});return;
   }
   // A production add-in may be loaded in the laboratory too. Suspend only its
   // in-memory preview so two independent layout services cannot fight over the
   // same native grid. Never write or reset the user's saved preferences.
   var production=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="BIMaestro");
   var service=production?.GetType("BIMaestro.PropertiesAppearance.PropertiesAppearanceService");
   var preferences=production?.GetType("BIMaestro.PropertiesAppearance.AppearancePreferences");
   if(service!=null&&preferences!=null){var flags=BindingFlags.Static|BindingFlags.NonPublic;installedPreviewField=service.GetField("_preview",flags);installedProcess=service.GetMethod("ProcessIdling",flags);isolatedPreview=Activator.CreateInstance(preferences);installedPreviewField.SetValue(null,isolatedPreview);service.GetField("_next",flags).SetValue(null,DateTime.MinValue);service.GetMethod("ProcessIdling",flags).Invoke(null,new object[]{app});}
   appearanceIsolated=true;
  }
  IEnumerable<Autodesk.Revit.DB.Wall> Walls(UIApplication app) => new FilteredElementCollector(app.ActiveUIDocument.Document).OfClass(typeof(Autodesk.Revit.DB.Wall)).Cast<Autodesk.Revit.DB.Wall>();
  Autodesk.Revit.DB.Wall Wall(UIApplication app) {var selection=app.ActiveUIDocument.Selection.GetElementIds();return selection.Count==1?app.ActiveUIDocument.Document.GetElement(selection.First()) as Autodesk.Revit.DB.Wall??Walls(app).First():Walls(app).First();}
  void Fixture(UIApplication app)
  {
   if(File.Exists(FixturePath)){app.OpenAndActivateDocument(FixturePath);return;}
   var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
   try { using(var tx=new Transaction(doc,"Disposable order fixture")) {tx.Start();CreateProjectParameters(app,doc);var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();var second=Level.Create(doc,level.Elevation+15);second.Name="ORDER_LEVEL_2";var type=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First();var wall=Autodesk.Revit.DB.Wall.Create(doc,Line.CreateBound(XYZ.Zero,new XYZ(20,0,0)),type.Id,level.Id,10,0,false,false);wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("ORDER_BASELINE");wall.LookupParameter("BIMAESTRO_Code_metier").Set("METIER_BASELINE");wall.LookupParameter("BIMAESTRO_Statut_validation").Set(0);var wall2=Autodesk.Revit.DB.Wall.Create(doc,Line.CreateBound(new XYZ(0,10,0),new XYZ(20,10,0)),type.Id,level.Id,8,0,false,false);wall2.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("ORDER_SECOND_WALL");tx.Commit();}doc.SaveAs(FixturePath,new SaveAsOptions{OverwriteExistingFile=true});}finally{doc.Close(false);}app.OpenAndActivateDocument(FixturePath);
  }
  void CreateProjectParameters(UIApplication ui,Document doc)
  {
   var app=ui.Application;var previous=app.SharedParametersFilename;var path=Path.Combine(dir,"fixture-shared-parameters.txt");
   File.WriteAllText(path,"# This is a Revit shared parameter file.\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n");
   try{
    app.SharedParametersFilename=path;var file=app.OpenSharedParameterFile()??throw new Exception("Fixture shared parameter file unavailable");var group=file.Groups.Create("BIMaestroLab");
    var categories=app.Create.NewCategorySet();categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Walls));
    var text=group.Definitions.Create(new ExternalDefinitionCreationOptions("BIMAESTRO_Code_metier",SpecTypeId.String.Text){GUID=new Guid("2253B16C-E49D-482F-B137-18D376729F1A")});
    var flag=group.Definitions.Create(new ExternalDefinitionCreationOptions("BIMAESTRO_Statut_validation",SpecTypeId.Boolean.YesNo){GUID=new Guid("46A5E212-87A8-4E1C-A85A-1469A52C33C4")});
    if(!doc.ParameterBindings.Insert(text,app.Create.NewInstanceBinding(categories),GroupTypeId.IdentityData)||!doc.ParameterBindings.Insert(flag,app.Create.NewInstanceBinding(categories),GroupTypeId.Constraints))throw new Exception("Fixture parameter binding refused");
   }finally{app.SharedParametersFilename=previous;}
  }
  void Configure(UIApplication app,bool groupOnly=false)
  {
   var context=AppearanceContext.ForEditor(app);var comment=TargetParameter(app);
   var target=context.Parameters.Single(p=>p.NativeName==AppearancePreferences.Normalize(comment.Definition.Name));
   var rows=context.PreviewRows;var row=rows.Single(r=>r.ParameterKey==target.Key);
   var profile=new AppearanceProfile {Key=context.CategoryKey};profile.Layout.Groups.Add(target.GroupKey);if(!groupOnly)profile.Layout.Parameters[target.GroupKey]=new List<string>{row.OrderKey};
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
  Dictionary<string,object> Schema(UIApplication app)
  {
   var result=new Dictionary<string,object>();var doc=app.ActiveUIDocument.Document;
   foreach(var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements()))foreach(Parameter p in e.Parameters)
   {var key=e.UniqueId+"|"+p.Id+"|"+p.Definition.Name;result[key]=new{storage=p.StorageType.ToString(),readOnly=p.IsReadOnly,groupTypeId=p.Definition.GetGroupTypeId()?.TypeId,shared=p.IsShared,guid=p.IsShared?p.GUID.ToString():null};}
   return result;
  }
  Dictionary<string,string> Snapshot(UIApplication app)
  {
   var doc=app.ActiveUIDocument.Document;var elements=new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements());var result=new Dictionary<string,string>();
   foreach(var element in elements)foreach(Parameter p in element.Parameters) {string value;try{switch(p.StorageType){case StorageType.String:value=p.AsString()??"";break;case StorageType.Double:value=p.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;case StorageType.Integer:value=p.AsInteger().ToString(CultureInfo.InvariantCulture);break;case StorageType.ElementId:value=p.AsElementId().ToString();break;default:value="";break;}}catch(Exception ex){value="<unreadable:"+ex.GetType().Name+">";}result[element.UniqueId+"|"+p.Id+"|"+p.Definition.Name]=value;}
   return result;
  }
  void VerifyWrite(UIApplication app)
  {
   var baseline=JsonConvert.DeserializeObject<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dir,"baseline.json")));var after=Snapshot(app);var config=Expectation();var expected=config.Value??File.ReadAllText(Path.Combine(dir,"expected-value.txt")).TrimEnd('\r','\n');var wall=TargetElement(app);var comment=TargetParameter(app);string key=wall.UniqueId+"|"+comment.Id+"|"+comment.Definition.Name;
   var deltas=baseline.Keys.Union(after.Keys).Where(k=>!baseline.ContainsKey(k)||!after.ContainsKey(k)||baseline[k]!=after[k]).Select(k=>new {key=k,before=baseline.ContainsKey(k)?baseline[k]:null,after=after.ContainsKey(k)?after[k]:null}).ToArray();
   bool expectedMatches=after.ContainsKey(key)&&after[key]==expected;
   if(comment.StorageType==StorageType.Double){double number;expectedMatches=double.TryParse(expected,NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&Math.Abs(comment.AsDouble()-number)<=config.Tolerance;}
   var allowed=new HashSet<string>();
   if(comment.StorageType==StorageType.Double)foreach(string name in config.AllowedDependentParameters){var p=wall.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter),name));if(p==null)throw new Exception("Unknown allowed dependent parameter "+name);allowed.Add(wall.UniqueId+"|"+p.Id+"|"+p.Definition.Name);}
   bool pass=expectedMatches&&deltas.Any(d=>d.key==key)&&deltas.All(d=>d.key==key||allowed.Contains(d.key));Write("verify-write.json",new {pass,parameter=config.Parameter,storage=comment.StorageType.ToString(),expected,actual=after.ContainsKey(key)?after[key]:null,allowedDependentKeys=allowed,deltas});if(!pass)throw new Exception("Native write verification failed; see verify-write.json");
  }
  WriteExpectation Expectation(){var file=Path.Combine(dir,"expected-write.json");return File.Exists(file)?JsonConvert.DeserializeObject<WriteExpectation>(File.ReadAllText(file)):new WriteExpectation();}
  Element TargetElement(UIApplication app)=>Expectation().Target=="view"?(Element)app.ActiveUIDocument.ActiveView:Wall(app);
  Parameter TargetParameter(UIApplication app){var config=Expectation();var element=TargetElement(app);long id;Parameter p;if(long.TryParse(config.Parameter,NumberStyles.Integer,CultureInfo.InvariantCulture,out id))p=element.Parameters.Cast<Parameter>().SingleOrDefault(x=>x.Id.Value==id);else p=element.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter),config.Parameter));return p??throw new Exception("Missing target parameter "+config.Parameter);}
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
   Write("report.json",new {command,time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,version=app.Application.VersionNumber,build=app.Application.VersionBuild,path=FixturePath,status=PropertiesAppearanceService.Status,layoutStatus=PropertiesPaletteLayout.Status,hasApplied=PropertiesPaletteLayout.HasApplied,parameterSchema=Schema(app),nativeGroupHeaderTest=NativeGroupExperiment.HeaderTest(grid),autoGroup,hwnd=grid?.Window.ToInt64(),nativeState,rows,mappings,hash,metrics=PropertiesAppearanceMetrics.Snapshot(),parameters=snapshot});
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
   try {if(!diagnostics)return;var grid=diagnosticGrid;if(grid==null)return;Write("timer-state.json",new{time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,state=NativeState(grid)});}
   catch(Exception ex){Write("timer-state.json",new{time=DateTime.UtcNow,pid=Process.GetCurrentProcess().Id,error=ex.ToString()});}
  }
  object NativeState(NativePropertiesGrid grid)
  {
   var focus=WindowState(GetFocus());int? activeCell=null,currentResult=null;bool? editing=null;uint row=0,col=0;string error=null;var children=new List<object>();
   try{
    if(grid==null||!grid.IsCurrent())throw new InvalidOperationException("No current native grid");
    var module=NativeModuleCache.Loaded("og1100asu.dll");
    var address=GetProcAddress(module.BaseAddress,"?IsActiveCurrentCell@CGXGridCore@@QEAAHXZ");if(address==IntPtr.Zero)throw new NotSupportedException("IsActiveCurrentCell export absent");activeCell=Marshal.GetDelegateForFunctionPointer<NativeFlag>(address)(grid.Core);
    address=GetProcAddress(module.BaseAddress,"?GetCurrentCell@CGXGridCore@@QEAAHAEAK0H@Z");if(address!=IntPtr.Zero)currentResult=Marshal.GetDelegateForFunctionPointer<NativeCurrentCell>(address)(grid.Core,ref row,ref col,0);
    editing=grid.IsEditing;NativePropertiesGrid.EnumChildWindows(grid.Window,(h,p)=>{children.Add(WindowState(h));return true;},IntPtr.Zero);
   }catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
   return new{focus,grid=grid==null?null:WindowState(grid.Window),activeCell,currentResult,currentRow=row,currentColumn=col,editing,error,children};
  }
  void Write(string file,object value)=>File.WriteAllText(Path.Combine(dir,file),JsonConvert.SerializeObject(value,Formatting.Indented));
 }
}
