using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Transform=Autodesk.Revit.DB.Transform;

namespace BIMaestro.MotorTests
{
    internal static class MaintenanceFeatureInspection
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static double[] V(XYZ p) => new[] {p.X,p.Y,p.Z};
        private static double[] OptionalV(object p) => p is XYZ xyz?V(xyz):null;
        private static object Get(object value,string name) => value?.GetType().GetField(name,Hidden)?.GetValue(value)
            ??value?.GetType().GetProperty(name,Hidden)?.GetValue(value);
        private static object[] Items(object value,string name) => Get(value,name) is IEnumerable items?items.Cast<object>().ToArray():Array.Empty<object>();
        private static object Detection(Document document,FamilyInstance source,string preferredOperation=null)
        {
            var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="BIMaestro");
            if(assembly==null)return new {error="BIMaestro production assembly is not loaded"};
            try
            {
                var detector=assembly.GetType("BIMaestro.MepBooster.MaintenanceFeatureDetection",true);
                string operation=preferredOperation??(source.Id.Value==953028L||source.Id.Value==958173L||source.Id.Value==950354L||source.Id.Value==983094L?"HandwheelValve":"Filter");
                var op=Enum.Parse(assembly.GetType("BIMaestro.MepBooster.MaintenanceAccessOperation",true),operation);
                var result=detector.GetMethod("Detect",Hidden).Invoke(null,new object[] {document,source,op});
                var local=source.GetTransform().Inverse;
                var command=Get(result,"CommandPoint")as XYZ;
                // Capture detached raw observations as well as scored candidates.
                // This distinguishes an absent circle (<radius threshold, e.g.)
                // from ambiguity and records the exact branch/cap geometry.
                object Probe(object snapshot,string origin)
                {
                    return new {origin, warnings=Items(snapshot,"Warnings"),dimensions=Items(snapshot,"Dimensions").Select(p=>new {
                        kind=Get(p,"Kind")?.ToString(),name=Get(p,"Name"),component=Get(p,"Component"),value=Get(p,"Value"),origin=Get(p,"Origin")}).ToArray(),
                        features=Items(snapshot,"Features").Select(o=>new {component=Get(o,"Component"),origin=Get(o,"Origin"),center=OptionalV(Get(o,"Center")),axis=OptionalV(Get(o,"Axis")),
                            radius=Get(o,"Radius"),axialLength=Get(o,"AxialLength"),coverage=Get(o,"Coverage"),cylinder=Get(o,"Cylinder"),linear=Get(o,"Linear"),explicitComponent=Get(o,"ComponentExplicit"),
                            centerlineRadius=Get(o,"CenterlineRadius"),rimThickness=Get(o,"RimThickness")}).ToArray()};
                }
                object projectProbe=null,familyProbe=null;string probeError=null;
                try
                {
                    var snapshot=Activator.CreateInstance(detector.GetNestedType("Snapshot",BindingFlags.NonPublic),true);
                    detector.GetMethod("ReadElement",Hidden).Invoke(null,new object[] {source,Transform.Identity,source.Symbol.Family.Name,"diagnostic instance",snapshot,false});
                    projectProbe=Probe(snapshot,"world");
                    familyProbe=Probe(detector.GetMethod("ProbeFamily",Hidden).Invoke(null,new object[] {document,source}),"family local");
                }
                catch(Exception error){probeError=error.ToString();}
                return new {assembly=assembly.Location,preferredOperation=operation,operation=Get(result,"Operation")?.ToString(),confidence=Get(result,"Confidence")?.ToString(),
                    command=OptionalV(command),localCommand=command==null?null:V(local.OfPoint(command)),axis=OptionalV(Get(result,"Axis")),radius=Get(result,"Radius"),
                    handleThickness=Get(result,"HandleThickness"),chainDrop=Get(result,"ChainDrop"),chainSpacing=Get(result,"ChainSpacing"),removalSize=OptionalV(Get(result,"RemovalSize")),
                    removalCenterOffset=OptionalV(Get(result,"RemovalCenterOffset")),removalTravel=Get(result,"RemovalTravel"),ambiguous=Get(result,"Ambiguous"),directionAmbiguous=Get(result,"DirectionAmbiguous"),
                    evidence=Items(result,"Evidence"),warnings=Items(result,"Warnings"),candidates=Items(result,"Candidates").Select(c=>new {
                        label=Get(c,"Label"),score=Get(c,"Score"),command=OptionalV(Get(c,"CommandPoint")),localCommand=Get(c,"CommandPoint")is XYZ point?V(local.OfPoint(point)):null,
                        axis=OptionalV(Get(c,"Axis")),radius=Get(c,"Radius"),evidence=Items(c,"Evidence")}).ToArray(),projectProbe,familyProbe,probeError};
            }
            catch(Exception error){return new {assembly=assembly.Location,error=error.ToString()};}
        }
        private static object Frame(Transform t) => new {origin=V(t.Origin),x=V(t.BasisX),y=V(t.BasisY),z=V(t.BasisZ)};
        private static object Parameters(Element e) => e.Parameters.Cast<Parameter>().Select(p=>new {
            name=p.Definition.Name,kind=p.StorageType.ToString(),text=p.AsValueString()??(p.StorageType==StorageType.String?p.AsString():null),
            number=p.StorageType==StorageType.Double?(double?)p.AsDouble():p.StorageType==StorageType.Integer?p.AsInteger():(double?)null,
            length=p.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length}).ToArray();
        private static object Shape(Element e, Transform coordinates)
        {
            var solids=new List<object>();var instances=new List<object>();var curves=new List<object>();
            using(var options=new Options {DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=false})
            using(var geometry=e.get_Geometry(options))
            {
                Read(geometry,0);
                void Read(GeometryElement g,int depth)
                {
                    if(g==null||depth>8)return;
                    foreach(var item in g)
                    {
                        if(item is GeometryInstance nested)
                        {
                            Element symbol=e.Document.GetElement(nested.GetSymbolGeometryId().SymbolId);
                            instances.Add(new {depth,type=symbol?.Name,family=(symbol as FamilySymbol)?.Family.Name,frame=Frame(nested.Transform)});
                            using(var expanded=nested.GetInstanceGeometry()) Read(expanded,depth+1);
                        }
                        else if(item is Curve curve)
                        {
                            try {curves.Add(new {kind=curve.GetType().Name,length=curve.IsBound?(double?)curve.Length:null,center=curve is Arc arc?V(coordinates.OfPoint(arc.Center)):null,
                                axis=curve is Arc circle?V(coordinates.OfVector(circle.Normal)):null,radius=curve is Arc ring?(double?)ring.Radius:null,
                                points=curve.IsBound?curve.Tessellate().Select(coordinates.OfPoint).Select(V).ToArray():null});}
                            catch(Exception curveError){curves.Add(new {kind=curve.GetType().Name,error=curveError.GetType().Name});}
                        }
                        else if(item is Solid solid&&solid.Volume>1e-12)
                        {
                            var points=solid.Edges.Cast<Edge>().SelectMany(edge=>edge.Tessellate()).Select(coordinates.OfPoint).ToArray();
                            if(points.Length==0)continue;
                            var cylinders=solid.Faces.Cast<Face>().OfType<CylindricalFace>().Select(f=>new {origin=V(coordinates.OfPoint(f.Origin)),axis=V(coordinates.OfVector(f.Axis)),radius=f.get_Radius(0).GetLength(),area=f.Area}).Take(150).ToArray();
                            var planes=solid.Faces.Cast<Face>().OfType<PlanarFace>().Select(f=>new {origin=V(coordinates.OfPoint(f.Origin)),normal=V(coordinates.OfVector(f.FaceNormal)),area=f.Area}).Take(150).ToArray();
                            var arcs=solid.Edges.Cast<Edge>().Select(edge=>edge.AsCurve()).OfType<Arc>().Select(a=>new {center=V(coordinates.OfPoint(a.Center)),axis=V(coordinates.OfVector(a.Normal)),radius=a.Radius,length=a.Length}).Take(150).ToArray();
                            solids.Add(new {depth,style=(e.Document.GetElement(solid.GraphicsStyleId)as GraphicsStyle)?.GraphicsStyleCategory.Name,
                                min=V(new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z))),max=V(new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z))),volume=solid.Volume,cylinders,planes,arcs});
                        }
                    }
                }
            }
            return new {solids,instances,curves};
        }
        private static object FamilyParameterValue(FamilyType type,FamilyParameter parameter)
        {
            try
            {
                if(type==null)return null;
                if(parameter.StorageType==StorageType.Double)return type.AsDouble(parameter);
                if(parameter.StorageType==StorageType.Integer)return type.AsInteger(parameter);
                if(parameter.StorageType==StorageType.String)return type.AsString(parameter);
                if(parameter.StorageType==StorageType.ElementId)return type.AsElementId(parameter)?.Value;
            }
            catch(Exception error){return error.GetType().Name;}
            return null;
        }
        private static object CaptureEquipment(Document document,FamilyInstance source,UIApplication app,string folder)
        {
            var output=new Dictionary<string,object>();var files=new List<string>();output["files"]=files;
            Window window=null;
            try
            {
                var assembly=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="BIMaestro");
                var type=assembly.GetType("BIMaestro.MepBooster.MaintenancePreview",true);
                var preview=(FrameworkElement)Activator.CreateInstance(type,Hidden,null,new object[] {document,source},null);
                var layout=new DockPanel();
                var caption=new TextBlock {Text="Inspection de l’équipement "+source.Id.Value+" · "+source.Symbol.Family.Name+" / "+source.Name
                    +"\nGéométrie modélisée seulement. Aucun type d’ouverture, axe, course ou poste opérateur n’est déduit de cette image.",
                    TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12),FontSize=15};
                DockPanel.SetDock(caption,Dock.Top);layout.Children.Add(caption);layout.Children.Add(preview);
                window=new Window {Title="Inspection ciblée · "+source.Id.Value,Width=1100,Height=850,Content=layout,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                new System.Windows.Interop.WindowInteropHelper(window){Owner=app.MainWindowHandle};
                window.Loaded+=(_,__)=>window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
                {
                    try
                    {
                        void Save(string suffix)
                        {
                            type.GetMethod("Fit",Hidden).Invoke(preview,null);window.UpdateLayout();
                            string name="equipment-"+source.Id.Value+"-"+suffix+".png";
                            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
                            bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                            using(var file=File.Create(Path.Combine(folder,name)))png.Save(file);files.Add(name);
                        }
                        output["captureNote"]=Get(preview,"CaptureNote");Save("context");
                        // Test-only isolation and camera adjustment of detached
                        // WPF snapshots. No model, installed DLL or detection
                        // state is modified and no operating ghost is invented.
                        if(Get(preview,"_contextVisual")is ModelVisual3D context)context.Content=null;
                        if(Get(preview,"_blockedVisual")is ModelVisual3D blocked)blocked.Content=null;
                        if(Get(preview,"_legendLabel")is TextBlock legend)legend.Text="Équipement sélectionné isolé · Géométrie de la famille · Aucun geste déduit";
                        if(Get(preview,"_sourceBounds")is Rect3D bounds&&!bounds.IsEmpty)
                        {
                            type.GetField("_clipMin",Hidden).SetValue(preview,new Point3D(bounds.X,bounds.Y,bounds.Z));
                            type.GetField("_clipMax",Hidden).SetValue(preview,new Point3D(bounds.X+bounds.SizeX,bounds.Y+bounds.SizeY,bounds.Z+bounds.SizeZ));
                        }
                        Save("isolated");
                        type.GetField("_yaw",Hidden).SetValue(preview,2.09);Save("opposite");
                        type.GetField("_pitch",Hidden).SetValue(preview,1.38);Save("top");
                    }
                    catch(Exception error){output["error"]=error.ToString();}
                    finally{window.Close();}
                }));
                window.ShowDialog();
            }
            catch(Exception error){output["error"]=error.ToString();}
            finally{if(window?.IsVisible==true)window.Close();}
            return output;
        }
        internal static void Run(Document doc,string folder,long[] inspectionIds=null,UIApplication app=null)
        {
            var results=new List<object>();
            bool targeted=inspectionIds!=null;
            foreach(long id in inspectionIds??new[] {1014886L,958173L,953028L,649237L,950354L,983094L,649268L})
            {
                var e=doc.GetElement(new ElementId(id))as FamilyInstance;
                if(e==null){results.Add(new {id,missing=true,model=doc.PathName,title=doc.Title});continue;}
                // An unknown targeted valve is inspected against both existing
                // manual operation entry points. Neither operation is presumed
                // correct and it is never implicitly classified as a filter.
                object detection=Detection(doc,e,targeted?"HandwheelValve":null);
                object detectionModes=targeted?new[] {new {requested="HandwheelValve",result=detection},
                    new {requested="LeverValve",result=Detection(doc,e,"LeverValve")}}:null;
                Document familyDoc=null;object familyData=null;string error=null;
                try
                {
                    familyDoc=doc.EditFamily(e.Symbol.Family);
                    var manager=familyDoc.FamilyManager;
                    using(var transaction=new Transaction(familyDoc,"Read selected maintenance family type"))
                    {
                        transaction.Start();
                        var type=manager.Types.Cast<FamilyType>().FirstOrDefault(t=>t.Name==e.Symbol.Name);
                        if(type!=null)manager.CurrentType=type;
                        foreach(FamilyParameter fp in manager.Parameters)
                        {
                            if(!fp.IsInstance||fp.IsReadOnly||fp.IsDeterminedByFormula)continue;
                            var source=e.LookupParameter(fp.Definition.Name);
                            if(source==null)continue;
                            try{if(source.StorageType==StorageType.Double)manager.Set(fp,source.AsDouble());else if(source.StorageType==StorageType.Integer)manager.Set(fp,source.AsInteger());}catch{}
                        }
                        familyDoc.Regenerate();
                        familyData=new {
                            parameters=manager.Parameters.Cast<FamilyParameter>().Select(p=>new {name=p.Definition.Name,formula=p.Formula,instance=p.IsInstance,
                                kind=p.StorageType.ToString(),value=FamilyParameterValue(manager.CurrentType,p),dataType=p.Definition.GetDataType().TypeId}).ToArray(),
                            nested=new FilteredElementCollector(familyDoc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(n=>new {id=n.Id.Value,family=n.Symbol.Family.Name,type=n.Name,frame=Frame(n.GetTransform()),parameters=Parameters(n),shape=Shape(n,Transform.Identity)}).ToArray(),
                            shape=Shape(familyDoc.OwnerFamily,Transform.Identity),
                            forms=new FilteredElementCollector(familyDoc).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(f=>f.IsSolid).Select(f=>new {id=f.Id.Value,kind=f.GetType().Name,subcategory=f.Subcategory?.Name,shape=Shape(f,Transform.Identity)}).ToArray()
                        };
                        transaction.RollBack();
                    }
                }
                catch(Exception ex){error=ex.ToString();}
                finally{familyDoc?.Close(false);}
                object capture=targeted&&app!=null?CaptureEquipment(doc,e,app,folder):null;
                results.Add(new {id,uniqueId=e.UniqueId,model=doc.PathName,title=doc.Title,family=e.Symbol.Family.Name,type=e.Name,frame=Frame(e.GetTransform()),parameters=Parameters(e),typeParameters=Parameters(e.Symbol),shape=Shape(e,e.GetTransform().Inverse),familyData,error,detection,detectionModes,capture,
                    connectors=e.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().Select(c=>new {origin=V(e.GetTransform().Inverse.OfPoint(c.Origin)),normal=V(e.GetTransform().Inverse.OfVector(c.CoordinateSystem.BasisZ)),radius=c.Shape==ConnectorProfileType.Round?c.Radius:0}).ToArray()});
            }
            File.WriteAllText(Path.Combine(folder,"maintenance-features-inspection.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
        }
    }
}
