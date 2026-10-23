using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ComboBox=System.Windows.Controls.ComboBox;
using Button=System.Windows.Controls.Button;
using Transform=Autodesk.Revit.DB.Transform;
namespace BIMaestro.MotorTests
{
 internal static class MaintenanceValve955038Checks
 {
  const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  static object G(object x,string n)=>x?.GetType().GetField(n,F)?.GetValue(x)??x?.GetType().GetProperty(n,F)?.GetValue(x);
  static void S(object x,string n,object v)=>x.GetType().GetField(n,F).SetValue(x,v);
  static object[] A(object x,string n)=>G(x,n)is IEnumerable e?e.Cast<object>().ToArray():Array.Empty<object>();
  static double[] V(object x)=>x is XYZ p?new[]{p.X,p.Y,p.Z}:null;
  static object Frame(object x)=>x is Transform t?new{origin=V(t.Origin),x=V(t.BasisX),y=V(t.BasisY),z=V(t.BasisZ)}:null;
  internal static void Run(Assembly dll,Document doc,UIApplication ui,string folder)
  {
   var rows=new List<object>();var failures=new List<string>();var checks=new List<string>();
   var report=new Dictionary<string,object>{{"cases",rows},{"failures",failures},{"checks",checks},{"assembly",dll.Location},
    {"sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll.Location)))},{"revitApi",dll.GetReferencedAssemblies().First(a=>a.Name=="RevitAPI").FullName}};
   Type T(string n)=>dll.GetType("BIMaestro.MepBooster."+n,true);
   object Op(string n)=>Enum.Parse(T("MaintenanceAccessOperation"),n);
   void Save()=>File.WriteAllText(Path.Combine(folder,"valve-955038-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
   void Check(bool ok,string n){if(ok)checks.Add(n);else failures.Add(n);}
   void Stage(string n){File.WriteAllText(Path.Combine(folder,"valve-955038-stage.txt"),n);Save();}
   var source=doc.GetElement(new ElementId(955038L))as FamilyInstance;
   string prior=Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH");
   try
   {
    if(source==null)throw new Exception("955038 missing; no substitute used");
    string memory=Path.Combine(folder,"diagnostic-corrections.json");File.WriteAllText(memory,"{\"Version\":1,\"Corrections\":[]}");
    Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH",Path.GetFullPath(memory));
    var origin=source.GetTransform().Origin;var heights=new List<double>();
    foreach(Floor floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)))
    using(var options=new Options())using(var geometry=floor.get_Geometry(options))
     if(geometry!=null)foreach(var solid in geometry.OfType<Solid>())foreach(var face in solid.Faces.OfType<PlanarFace>())
      if(face.FaceNormal.Z>.999){var hit=face.Project(origin);if(hit!=null&&face.IsInside(hit.UVPoint)&&hit.XYZPoint.Z<origin.Z)heights.Add(hit.XYZPoint.Z);}
    if(heights.Count==0)throw new Exception("No actual Floor face below valve XY; a named level is not substituted");
    double floorZ=heights.Max();report["floorZM"]=floorZ*.3048;report["originalOrigin"]=V(origin);
    report["family"]=source.Symbol.Family.Name;report["type"]=source.Name;
    void Case(string label,string operation,double shift)
    {
     var row=new Dictionary<string,object>{{"name",label},{"requested",operation},{"shiftMm",shift*304.8}};rows.Add(row);
     using(var group=new TransactionGroup(doc,"Temporary955038 "+label))
     {
      group.Start();
      try
      {
       source=(FamilyInstance)doc.GetElement(new ElementId(955038L));
       if(Math.Abs(shift)>1e-9)using(var tx=new Transaction(doc,"Temporary valve height")){tx.Start();if(source.Pinned)source.Pinned=false;ElementTransformUtils.MoveElement(doc,source.Id,XYZ.BasisZ*shift);tx.Commit();}
       Check(!doc.IsModifiable,label+": recognition runs outside a modifying transaction");
       var state=Activator.CreateInstance(T("MaintenanceWorkbenchState"),true);S(state,"Operation",Op(operation));
       Stage(label+": auto recognition and whole gesture");
       T("MaintenanceAutoconfiguration").GetMethod("Suggest",F).Invoke(null,new object[]{doc,source,state,(Action<string>)Stage});
       var fields=(IDictionary)G(state,"Fields");
       row["state"]=new{operation=G(state,"Operation")?.ToString(),command=V(G(state,"CommandPoint")),frame=Frame(G(state,"DetectedFrame")),foot=V(G(state,"FootPoint")),
        reason=G(state,"NoOperatorReason"),support=G(state,"SupportNote"),warnings=A(state,"PlacementWarnings"),features=A(state,"FeatureWarnings"),diagnostics=A(state,"PlacementDiagnostics"),
        detected=G(state,"DetectionNote"),wholeGesture=G(state,"WholeGestureChecked"),clear=G(state,"WholeGestureClear"),
        fields=fields.Keys.Cast<string>().ToDictionary(k=>k,k=>fields[k]),sourceOrigin=V(source.GetTransform().Origin)};
       Check(operation=="Actuator"?G(state,"Operation")?.ToString()=="Actuator":G(state,"Operation")?.ToString()!="Actuator",label+": manual and removal scenarios remain separate");
       if(G(state,"FootPoint")==null)Check(!string.IsNullOrWhiteSpace(G(state,"NoOperatorReason")as string),label+": no operator has an explicit reason");
       var win=(Window)Activator.CreateInstance(T("MaintenanceWorkbenchWindow"),F,null,new object[]{doc,source,state,ui.MainWindowHandle},null);
       win.Loaded+=(_,__)=>win.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
       {
        try
        {
         Check(((ComboBox)G(win,"_mode")).SelectedIndex==0,label+": real model is default, never automatic demonstration");
         T("MaintenanceWorkbenchWindow").GetMethod("Draw",F).Invoke(win,null);win.UpdateLayout();
         var reason=(TextBlock)G(win,"_noOperatorReason");row["ui"]=new{status=((TextBlock)G(win,"_status")).Text,reason=reason.Text,visible=reason.Visibility.ToString(),note=((TextBlock)G(win,"_modeNote")).Text};
         if(G(state,"FootPoint")==null)Check(reason.Visibility==System.Windows.Visibility.Visible&&!string.IsNullOrWhiteSpace(reason.Text),label+": missing-avatar reason is directly visible");
         var bitmap=new RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(win);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
         using(var file=File.Create(Path.Combine(folder,"valve955038-"+label+".png")))png.Save(file);
         ((ComboBox)G(win,"_operation")).SelectedIndex=operation=="Actuator"?1:3;
         Check(G(state,"CommandPoint")==null&&G(state,"FootPoint")==null&&((ComboBox)G(win,"_mode")).SelectedIndex==0,label+": dropdown clears anchors and stays in actual model");
         ((Button)G(win,"_suggest")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
         Check(G(win,"PickRequest")as string=="auto",label+": cleared command requests new detection instead of position-only search");
        }catch(Exception ex){row["uiError"]=ex.ToString();failures.Add(label+": UI error");}finally{if(win.IsVisible)win.Close();Save();}
       }));
       win.ShowDialog();
      }catch(Exception ex){row["error"]=ex.ToString();failures.Add(label+": error");}
      finally{group.RollBack();Save();}
     }
     Check((((FamilyInstance)doc.GetElement(new ElementId(955038L))).GetTransform().Origin-origin).GetLength()<.0001,label+": original location restored by rollback");
    }
    Case("original-manual","HandwheelValve",0);Case("original-removal","Actuator",0);Case("origin1000mm-removal","Actuator",floorZ+1000/304.8-origin.Z);
   }catch(Exception ex){report["error"]=ex.ToString();failures.Add("Diagnosis did not complete");}
   finally{Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH",prior);report["passed"]=failures.Count==0;Save();}
  }
 }
}
