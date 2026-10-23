// Standalone offscreen QA: compile with MaintenancePreview.cs and
// PREVIEW_CONTEXT_QA, RevitAPI and WPF references. No Revit process is opened.
#if PREVIEW_CONTEXT_QA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using BIMaestro.MepBooster;

namespace BIMaestro.MepBooster
{
    internal sealed class MaintenanceAccessObstacle
    {
        internal long ElementId, LinkId = -1;
        internal string LinkName;
    }
    internal static class BoosterIds
    {
        internal static long Value(ElementId id) => id.IntegerValue;
    }
}

internal static class PreviewContextQa
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static int _checks;
    private static object Get(object instance, string name) => instance.GetType().GetField(name, Flags).GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
    private static void Check(bool result, string message) { _checks++; if (!result) throw new Exception(message); }
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => e.Name.StartsWith("RevitAPI,")
            ? Assembly.LoadFrom(@"C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll") : null;
        try { Run(); Console.WriteLine("PASS " + _checks + " context/identity/cache/fit assertions"); }
        catch (Exception e) { Console.WriteLine(e); Environment.ExitCode = 1; }
    }
    private static void Run()
    {
        var preview = (MaintenancePreview)FormatterServices.GetUninitializedObject(typeof(MaintenancePreview));
        var viewport = new Viewport3D { Width = 900, Height = 400 };
        viewport.Measure(new System.Windows.Size(900, 400));
        viewport.Arrange(new Rect(0, 0, 900, 400));
        Set(preview, "_viewport", viewport); Set(preview, "_camera", new OrthographicCamera());
        Set(preview, "_origin", new XYZ(0, 0, 0)); Set(preview, "_sourceId", 99L);
        Set(preview, "_clipMin", new Point3D(-1, -1, -1)); Set(preview, "_clipMax", new Point3D(1, 1, 1));
        Set(preview, "_sourceBounds", new Rect3D(-.1, -.1, -.1, .2, .2, .2));
        Set(preview, "_overlayBounds", Rect3D.Empty);
        Set(preview, "_selectedVisual", new ModelVisual3D()); Set(preview, "_contextVisual", new ModelVisual3D());
        Set(preview, "_blockedVisual", new ModelVisual3D());
        Set(preview, "_overlayOpaqueVisual", new ModelVisual3D()); Set(preview, "_overlayTransparentVisual", new ModelVisual3D());
        typeof(MaintenancePreview).GetMethod("AddSnapshotVisuals", Flags).Invoke(preview, null);
        Check(viewport.Children.IndexOf((ModelVisual3D)Get(preview,"_selectedVisual"))
            <viewport.Children.IndexOf((ModelVisual3D)Get(preview,"_blockedVisual")),"blocking context writes depth before selected equipment");
        Check(viewport.Children.IndexOf((ModelVisual3D)Get(preview,"_overlayOpaqueVisual"))
            <viewport.Children.IndexOf((ModelVisual3D)Get(preview,"_blockedVisual")),"blocking context writes depth before the opaque operator");
        Set(preview, "_transparentFaces", new List<GeometryModel3D>());
        Set(preview, "_modelCaptureNote", "Coupe locale + 50 cm.");

        var mesh = new MeshGeometry3D();
        typeof(MaintenancePreview).GetMethod("AddClipped", Flags).Invoke(preview,
            new object[] { mesh, new Point3D(-3, -2, 0), new Point3D(3, -2, 0), new Point3D(0, 3, 0), 100 });
        Check(mesh.TriangleIndices.Count > 0, "intersecting surface was dropped");
        Check(mesh.Positions.All(p => p.X >= -1 - 1e-8 && p.X <= 1 + 1e-8 && p.Y >= -1 - 1e-8 && p.Y <= 1 + 1e-8),
            "surface was not clipped to the local block");
        int before = mesh.TriangleIndices.Count;
        typeof(MaintenancePreview).GetMethod("AddClipped", Flags).Invoke(preview,
            new object[] { mesh, new Point3D(10, 0, 0), new Point3D(10, 1, 0), new Point3D(10, 0, 1), 100 });
        Check(mesh.TriangleIndices.Count == before, "outside surface leaked into local block");

        var gray = MakeModel(mesh, Colors.Gray); var red = MakeModel(mesh, Colors.OrangeRed);
        Check(ReferenceEquals(gray.Geometry, red.Geometry), "red recoloring copied geometry");
        var contextModel=typeof(MaintenancePreview).GetMethod("ContextModel",Flags);
        var translucentRed=(Model3D)contextModel.Invoke(null,new object[]{mesh,true});
        var translucentGray=(Model3D)contextModel.Invoke(null,new object[]{mesh,false});
        Check(Flatten(translucentRed).All(m=>((SolidColorBrush)((DiffuseMaterial)m.Material).Brush).Color.A==115),"context obstacles are not 45% opaque");
        Check(Flatten(translucentGray).All(m=>((SolidColorBrush)((DiffuseMaterial)m.Material).Brush).Color.A==59),"ordinary context opacity changed");
        Check(Flatten(translucentRed).All(m=>m.IsFrozen&&m.Geometry.IsFrozen&&ReferenceEquals(m.Material,m.BackMaterial)),"translucent obstacle lost frozen geometry or its backface");
        Check(ReferenceEquals(Flatten(translucentRed).First().Geometry,gray.Geometry),"translucent obstacle copied captured geometry");
        Check(((SolidColorBrush)((DiffuseMaterial)red.Material).Brush).Color.A==255,"selected obstacle is no longer opaque");
        CheckOccludingContext(contextModel);
        Set(preview, "_selectedSnapshot", gray); Set(preview, "_selectedBlockedSnapshot", red);
        var entryType = typeof(MaintenancePreview).GetNestedType("ContextEntry", Flags);
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
        foreach (var identity in new[] { new { Id = 10L, Link = -1L, Name = (string)null }, new { Id = 10L, Link = 100L, Name = "Link A" }, new { Id = 10L, Link = 200L, Name = "Link B" } })
        {
            var entry = Activator.CreateInstance(entryType, true);
            Set(entry, "ElementId", identity.Id); Set(entry, "LinkId", identity.Link); Set(entry, "LinkName", identity.Name);
            Set(entry, "Normal", translucentGray); Set(entry, "Blocked", translucentRed); entries.Add(entry);
        }
        Set(preview, "_contextEntries", entries);
        var host = new MaintenanceAccessObstacle { ElementId = 10 };
        preview.SetBlockedElements(new[] { host });
        Check(((Model3DGroup)Get(preview, "_blockedSnapshot")).Children.Count == 1, "host identity colored linked instances");
        Check(((Model3DGroup)Get(preview, "_contextSnapshot")).Children.Count == 2, "nonblocking context disappeared");
        var firstGroup = Get(preview, "_blockedSnapshot");
        preview.SetBlockedElements(new[] { host, host });
        Check(ReferenceEquals(firstGroup, Get(preview, "_blockedSnapshot")), "identical blocker identities rebuilt context");
        preview.SetBlockedElements(new[] { new MaintenanceAccessObstacle { ElementId = 10, LinkId = 100, LinkName = "old label" } });
        Check(((Model3DGroup)Get(preview, "_blockedSnapshot")).Children.Count == 1, "exact LinkId did not identify one instance");
        preview.SetBlockedElements(new[] { new MaintenanceAccessObstacle { ElementId = 10, LinkName = "Link B" } });
        Check(((Model3DGroup)Get(preview, "_blockedSnapshot")).Children.Count == 1, "legacy LinkName fallback failed");
        preview.SetBlockedElements(new[] { new MaintenanceAccessObstacle { ElementId = 500 } });
        Check(preview.CaptureNote.Contains("Capture incomplète") && preview.CaptureNote.Contains("1 obstacle"), "uncaptured blocker was silently hidden");
        preview.SetBlockedElements(new[] { new MaintenanceAccessObstacle { ElementId = 99 } });
        Check(ReferenceEquals(((ModelVisual3D)Get(preview, "_selectedVisual")).Content, red), "selected equipment blocking body stayed blue");
        Set(preview, "_sourceComponentIds", new HashSet<long> { 99, 98 });
        preview.SetBlockedElements(new[] { new MaintenanceAccessObstacle { ElementId = 98 } });
        Check(ReferenceEquals(((ModelVisual3D)Get(preview, "_selectedVisual")).Content, red), "selected equipment child blocker was omitted");
        Check(!preview.CaptureNote.Contains("sans surface capturée"), "nested source geometry reported missing despite aggregate capture");
        preview.SetDemonstration(true);
        Check(((ModelVisual3D)Get(preview, "_blockedVisual")).Content == null, "real blockers leaked into demonstration");
        Check(((ModelVisual3D)Get(preview, "_selectedVisual")).Content == null, "real equipment leaked into demonstration");
        preview.SetDemonstration(false);
        Check(ReferenceEquals(((ModelVisual3D)Get(preview, "_selectedVisual")).Content, red), "model conflict color was lost after demonstration");

        foreach (double yaw in new[] { -1.05, 0, 1.4 })
        foreach (double pitch in new[] { -.8, .5, 1.4 })
        foreach (var size in new[] { new System.Windows.Size(900, 400), new System.Windows.Size(400, 900) })
        {
            viewport.Width = size.Width; viewport.Height = size.Height;
            viewport.Measure(size); viewport.Arrange(new Rect(size));
            Set(preview, "_yaw", yaw); Set(preview, "_pitch", pitch); preview.Fit();
            var camera = (OrthographicCamera)Get(preview, "_camera");
            var look = camera.LookDirection; look.Normalize();
            var right = Vector3D.CrossProduct(look, camera.UpDirection); right.Normalize();
            var up = Vector3D.CrossProduct(right, look); up.Normalize();
            double aspect = viewport.ActualWidth / viewport.ActualHeight;
            var projected = Enumerable.Range(0, 8).Select(i => new Point3D((i & 1) == 0 ? -1 : 1,
                (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1) - (Point3D)Get(preview, "_target")).ToArray();
            double spanX = projected.Max(p => Vector3D.DotProduct(p, right)) - projected.Min(p => Vector3D.DotProduct(p, right));
            double spanY = projected.Max(p => Vector3D.DotProduct(p, up)) - projected.Min(p => Vector3D.DotProduct(p, up));
            Check(projected.All(p => Math.Abs(Vector3D.DotProduct(p, right)) <= camera.Width / 2 + 1e-9
                && Math.Abs(Vector3D.DotProduct(p, up)) <= camera.Width / aspect / 2 + 1e-9), "fit cropped the projected section block");
            Check(Math.Abs(camera.Width - Math.Max(spanX, spanY * aspect) * 1.1) < 1e-8,
                "fit kept excessive diagonal-based whitespace or used footer dimensions");
            Check(((Point3D)Get(preview, "_target")).Equals(new Point3D()), "fit failed to center projected section block");
        }
        Set(preview,"_yaw",0.0);Set(preview,"_pitch",0.0);preview.Fit();
        MaintenancePreviewBox Ghost(double y)
        {
            return new MaintenancePreviewBox { Color=Colors.DeepSkyBlue,Opacity=.35,
                Corners=Enumerable.Range(0,8).Select(i=>new XYZ((i&1)==0?0:.3,y+((i&2)==0?0:.3),(i&4)==0?0:.3)).ToArray() };
        }
        preview.SetScene(new[]{Ghost(0)},null);
        var persistent=Get(preview,"_transparentGroup");long orderChanges=preview.TransparencyOrderChangeCount,builds=preview.CachedModelBuildCount;
        for(int i=0;i<120;i++)preview.SetScene(new[]{Ghost(i*.001)},null);
        Check(ReferenceEquals(persistent,Get(preview,"_transparentGroup")),"transparency rebuilt the WPF group per pose");
        Check(preview.TransparencyOrderChangeCount==orderChanges,"unchanged projected depths rewrote the WPF collection");
        Check(preview.CachedModelBuildCount==builds,"rigid ghost animation rebuilt meshes");
        Check(preview.InputLatencySampleCount==0&&preview.PreviewTiming.Contains("non mesurée"),"missing input observations reported as zero latency");
        var timingType=typeof(MaintenancePreview).GetNestedType("TimingWindow",Flags);
        var timing=Activator.CreateInstance(timingType,true);
        for(int i=1;i<=100;i++)timingType.GetMethod("Add",Flags).Invoke(timing,new object[]{(double)i});
        Check((double)timingType.GetMethod("Percentile",Flags).Invoke(timing,new object[]{.95})==95,"P95 percentile incorrect");
        Check((double)timingType.GetMethod("Percentile",Flags).Invoke(timing,new object[]{.99})==99,"P99 percentile incorrect");
        var idleEdges=RenderOptions.GetEdgeMode(viewport);long beforeQuality=preview.CachedModelBuildCount;
        preview.SetMotionActive(true);
        Check(preview.IsFastRenderingActive,"playback did not disable multisampling");
        preview.SetMotionActive(false);
        Check(RenderOptions.GetEdgeMode(viewport)==idleEdges,"pause did not restore original edge rendering");
        Set(preview,"_dragging",true);preview.SetMotionActive(true);preview.SetMotionActive(false);
        Check(preview.IsFastRenderingActive,"pausing playback restored costly edges during an active drag");
        Set(preview,"_dragging",false);typeof(MaintenancePreview).GetMethod("UpdateRenderQuality",Flags).Invoke(preview,null);
        Check(RenderOptions.GetEdgeMode(viewport)==idleEdges,"drag release did not restore original edge rendering");
        Set(preview,"_wheelActive",true);typeof(MaintenancePreview).GetMethod("UpdateRenderQuality",Flags).Invoke(preview,null);
        Check(preview.IsFastRenderingActive,"zoom interaction did not reduce edge cost");
        Set(preview,"_wheelActive",false);typeof(MaintenancePreview).GetMethod("UpdateRenderQuality",Flags).Invoke(preview,null);
        Check(RenderOptions.GetEdgeMode(viewport)==idleEdges,"zoom completion did not restore edge rendering");
        Check(preview.CachedModelBuildCount==beforeQuality,"visual quality mode rebuilt physical geometry");
        var ring=Enumerable.Range(0,48).Select(i=>new MaintenancePreviewLine {
            A=new XYZ(Math.Cos(i*Math.PI/24),Math.Sin(i*Math.PI/24),0),
            B=new XYZ(Math.Cos((i+1)*Math.PI/24),Math.Sin((i+1)*Math.PI/24),0),Radius=.025,Color=Colors.DodgerBlue,Static=true}).ToList();
        var moving=new MaintenancePreviewLine {A=new XYZ(0,0,0),B=new XYZ(1,0,0),Radius=.025,Color=Colors.DodgerBlue};
        ring.Add(moving);preview.SetScene(null,ring);
        Check(preview.StaticLineCount==48,"static line metadata was lost");
        Check(preview.StaticBatchModelCount>0&&preview.StaticBatchModelCount<preview.StaticLineCount,"static lines were not batched");
        var batch=(Model3DGroup)Get(preview,"_staticLineModels");
        Check(batch.Children.Cast<GeometryModel3D>().All(m=>m.IsFrozen&&m.Geometry.IsFrozen),"static batch meshes are mutable");
        Check(batch.Children.Cast<GeometryModel3D>().All(m=>((MeshGeometry3D)m.Geometry).Positions.Count<=18000),"static batch exceeded vertex budget");
        var staticSlots=((IEnumerable)Get(preview,"_lineSlots")).Cast<object>().Where(s=>(bool)Get(s,"Static")).ToArray();
        var primitiveModels=staticSlots.SelectMany(s=>((Model3DGroup)Get(s,"Opaque")).Children.Cast<GeometryModel3D>()).ToArray();
        var fusedModels=batch.Children.Cast<GeometryModel3D>().ToArray();
        Check(primitiveModels.Length==144,"reference no longer has all tubes and joint primitives");
        Check(Triangles(primitiveModels).SequenceEqual(Triangles(fusedModels)),"batch changed transformed triangles or materials");
        var referencePixels=Render(primitiveModels);var fusedPixels=Render(fusedModels);
        long pixelDifference=0;
        for(int i=0;i<referencePixels.Length;i++)pixelDifference+=Math.Abs(referencePixels[i]-fusedPixels[i]);
        double averageDifference=pixelDifference/(double)referencePixels.Length;
        Console.WriteLine("Static batch raster: mean absolute channel difference="+averageDifference.ToString("0.000000",CultureInfo.InvariantCulture));
        Check(averageDifference<.25,"static mesh batching materially changed the rendered appearance");
        long batchBuilds=preview.StaticBatchBuildCount;var firstBatchModel=batch.Children[0];
        for(int i=0;i<120;i++){moving.B=new XYZ(Math.Cos(i*.01),Math.Sin(i*.01),0);preview.SetScene(null,ring);}
        Check(preview.StaticBatchBuildCount==batchBuilds&&ReferenceEquals(batch.Children[0],firstBatchModel),"dynamic poses rebuilt the static batch");
        preview.SetBlockedElements(new[]{new MaintenanceAccessObstacle {ElementId=10}});preview.Fit();
        Check(preview.StaticBatchBuildCount==batchBuilds,"orbit/fit/context recoloring rebuilt the static batch");
        ring[0].B=ring[0].B+new XYZ(0,0,.1);preview.SetScene(null,ring);
        Check(preview.StaticBatchBuildCount==batchBuilds+1,"moving an explicitly static line did not invalidate its batch");
        ring[0].Radius*=1.2;preview.SetScene(null,ring);
        Check(preview.StaticBatchBuildCount==batchBuilds+2,"static radius did not invalidate its batch");
        ring[0].Color=Colors.DarkViolet;preview.SetScene(null,ring);
        Check(preview.StaticBatchBuildCount==batchBuilds+3,"static material did not invalidate its batch");
        ring[0].Static=false;preview.SetScene(null,ring);
        Check(preview.StaticBatchBuildCount==batchBuilds+4&&preview.StaticLineCount==47,"static flag did not invalidate the batch");
        var oversized=new MeshGeometry3D();
        for(int i=0;i<12001;i++)
        {
            double x=-1.2+(i%120)*.02,y=-1.0+(i/120)*.02;int start=oversized.Positions.Count;
            oversized.Positions.Add(new Point3D(x,y,0));oversized.Positions.Add(new Point3D(x+.018,y,0));oversized.Positions.Add(new Point3D(x,y+.018,0));
            for(int j=0;j<3;j++)
            {oversized.TriangleIndices.Add(start+j);oversized.Normals.Add(new Vector3D(0,0,1));oversized.TextureCoordinates.Add(new System.Windows.Point(j/2.0,0));}
        }
        var make=typeof(MaintenancePreview).GetMethod("Model",Flags);
        var chunked=(Model3D)make.Invoke(null,new object[]{oversized,Colors.DodgerBlue,.45});
        var splitModels=Flatten(chunked).ToArray();
        Check(chunked is Model3DGroup&&splitModels.Length==3,"oversized snapshot did not split on whole triangle boundaries");
        Check(splitModels.All(m=>((MeshGeometry3D)m.Geometry).Positions.Count<=18000&&((MeshGeometry3D)m.Geometry).TriangleIndices.Count<=60000),"snapshot chunk exceeds mesh budgets");
        Check(splitModels.Sum(m=>((MeshGeometry3D)m.Geometry).TriangleIndices.Count)==oversized.TriangleIndices.Count,"snapshot splitting dropped triangles");
        var originalMaterial=new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromArgb(115,Colors.DodgerBlue.R,Colors.DodgerBlue.G,Colors.DodgerBlue.B)));
        var originalModel=new GeometryModel3D(oversized,originalMaterial){BackMaterial=originalMaterial};originalModel.Freeze();
        Check(Triangles(new[]{originalModel}).SequenceEqual(Triangles(splitModels)),"snapshot splitting changed triangle positions, winding or material");
        Check(splitModels.All(m=>m.IsFrozen&&m.Geometry.IsFrozen&&ReferenceEquals(m.Material,m.BackMaterial)),"snapshot chunks lost frozen material/backface behavior");
        Check(splitModels.All(m=>{var m2=(MeshGeometry3D)m.Geometry;return m2.Normals.Count==m2.Positions.Count&&m2.TextureCoordinates.Count==m2.Positions.Count;}),"snapshot splitting discarded normals or texture coordinates");
        var chunkedRed=(Model3D)make.Invoke(null,new object[]{oversized,Colors.OrangeRed,1.0});
        Check(splitModels.Zip(Flatten(chunkedRed),(a,b)=>ReferenceEquals(a.Geometry,b.Geometry)).All(x=>x),"red highlight duplicated frozen chunk geometry");
        var largeBefore=Render(new[]{originalModel});var largeAfter=Render(splitModels);long largeDifference=0;
        for(int i=0;i<largeBefore.Length;i++)largeDifference+=Math.Abs(largeBefore[i]-largeAfter[i]);
        Console.WriteLine("Snapshot chunk raster: mean absolute channel difference="+(largeDifference/(double)largeBefore.Length).ToString("0.000000",CultureInfo.InvariantCulture));
        Check(largeDifference==0,"chunked snapshot changed offscreen pixels at identical camera/material");
        Set(preview,"_selectedSnapshot",chunked);
        Check(preview.MaxVisualMeshPositions<=18000&&preview.MaxVisualMeshIndices<=60000,"visible-mesh diagnostics missed an oversized mesh");
        var shared=new MeshGeometry3D {Positions=new Point3DCollection(new[]{new Point3D(0,0,0),new Point3D(1,0,0),new Point3D(0,1,0)})};
        for(int i=0;i<21001;i++){shared.TriangleIndices.Add(0);shared.TriangleIndices.Add(1);shared.TriangleIndices.Add(2);}
        var indexedParts=Flatten((Model3D)make.Invoke(null,new object[]{shared,Colors.Gray,1.0})).ToArray();
        Check(indexedParts.Length==2&&indexedParts.All(m=>((MeshGeometry3D)m.Geometry).TriangleIndices.Count<=60000)
            &&indexedParts.Sum(m=>((MeshGeometry3D)m.Geometry).TriangleIndices.Count)==63003,"shared-vertex mesh did not respect index budget or preserve triangles");
    }
    private static void CheckOccludingContext(MethodInfo contextModel)
    {
        var preview=(MaintenancePreview)FormatterServices.GetUninitializedObject(typeof(MaintenancePreview));
        var viewport=new Viewport3D {Camera=new OrthographicCamera {Position=new Point3D(0,0,3),LookDirection=new Vector3D(0,0,-3),UpDirection=new Vector3D(0,1,0),Width=2}};
        viewport.Children.Add(new ModelVisual3D {Content=new AmbientLight(Colors.White)});
        Set(preview,"_viewport",viewport);
        foreach(string name in new[]{"_selectedVisual","_overlayOpaqueVisual","_blockedVisual","_contextVisual","_overlayTransparentVisual"})
            Set(preview,name,new ModelVisual3D());
        typeof(MaintenancePreview).GetMethod("AddSnapshotVisuals",Flags).Invoke(preview,null);
        MeshGeometry3D Quad(double minX,double maxX,double minY,double maxY,double z)
            =>new MeshGeometry3D {Positions=new Point3DCollection(new[]{new Point3D(minX,minY,z),new Point3D(maxX,minY,z),new Point3D(maxX,maxY,z),new Point3D(minX,maxY,z)}),
                TriangleIndices=new Int32Collection(new[]{0,1,2,0,2,3})};
        var source=MakeModel(Quad(-.7,-.1,-.35,.35,0),Colors.DodgerBlue);
        var human=MakeModel(Quad(.1,.7,-.35,.35,0),Colors.SeaGreen);
        var obstacle=Quad(-.8,.8,-.45,.45,.2);
        ((ModelVisual3D)Get(preview,"_selectedVisual")).Content=source;
        ((ModelVisual3D)Get(preview,"_overlayOpaqueVisual")).Content=human;
        var blocked=(ModelVisual3D)Get(preview,"_blockedVisual");
        blocked.Content=MakeModel(obstacle,Colors.OrangeRed);
        const int width=320,height=200;
        var root=new System.Windows.Controls.Grid {Width=width,Height=height,Background=Brushes.White};root.Children.Add(viewport);
        root.Measure(new System.Windows.Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        RenderTargetBitmap Bitmap()
        {var result=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);result.Render(root);return result;}
        byte[] Pixels(RenderTargetBitmap bitmap)
        {var result=new byte[width*height*4];bitmap.CopyPixels(result,width*4,0);return result;}
        var opaque=Pixels(Bitmap());
        blocked.Content=(Model3D)contextModel.Invoke(null,new object[]{obstacle,true});
        var readable=Bitmap();var visible=Pixels(readable);
        int Difference(int x)
        {int index=(height/2*width+x)*4;return Enumerable.Range(0,3).Sum(c=>Math.Abs(visible[index+c]-opaque[index+c]));}
        Check(Difference(width*3/10)>70,"selected equipment remains invisible behind a blocking context surface");
        Check(Difference(width*7/10)>70,"opaque operator remains invisible behind a blocking context surface");
        Check(((SolidColorBrush)((DiffuseMaterial)source.Material).Brush).Color.A==255
            &&((SolidColorBrush)((DiffuseMaterial)human.Material).Brush).Color.A==255,"source or operator was made translucent");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(readable));
        using(var stream=System.IO.File.Create(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PreviewContextOcclusion.png")))encoder.Save(stream);
    }
    private static GeometryModel3D MakeModel(MeshGeometry3D mesh, System.Windows.Media.Color color)
    {
        if (!mesh.IsFrozen) mesh.Freeze();
        var model = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color))); model.Freeze(); return model;
    }
    private static IEnumerable<string> Triangles(IEnumerable<GeometryModel3D> models)
    {
        var triangles=new List<string>();
        string P(Point3D p)=>p.X.ToString("R",CultureInfo.InvariantCulture)+","+p.Y.ToString("R",CultureInfo.InvariantCulture)+","+p.Z.ToString("R",CultureInfo.InvariantCulture);
        foreach(var model in models)
        {
            var mesh=(MeshGeometry3D)model.Geometry;
            string material=((SolidColorBrush)((DiffuseMaterial)model.Material).Brush).Color.ToString();
            for(int i=0;i<mesh.TriangleIndices.Count;i+=3)
                triangles.Add(material+":"+P(model.Transform.Transform(mesh.Positions[mesh.TriangleIndices[i]]))+";"
                    +P(model.Transform.Transform(mesh.Positions[mesh.TriangleIndices[i+1]]))+";"+P(model.Transform.Transform(mesh.Positions[mesh.TriangleIndices[i+2]])));
        }
        triangles.Sort(StringComparer.Ordinal);return triangles;
    }
    private static IEnumerable<GeometryModel3D> Flatten(Model3D model)
    {
        if(model is GeometryModel3D geometry)yield return geometry;
        else if(model is Model3DGroup group)foreach(var child in group.Children)foreach(var geometryChild in Flatten(child))yield return geometryChild;
    }
    private static byte[] Render(IEnumerable<GeometryModel3D> models)
    {
        const int width=640,height=480;
        var viewport=new Viewport3D {Camera=new OrthographicCamera {
            Position=new Point3D(2,-3,3),LookDirection=new Vector3D(-2,3,-3),UpDirection=new Vector3D(0,0,1),Width=3 }};
        var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Colors.Gray));
        scene.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-.4,.5,-1)));
        foreach(var model in models)scene.Children.Add(model);
        viewport.Children.Add(new ModelVisual3D {Content=scene});
        var root=new System.Windows.Controls.Grid {Width=width,Height=height,Background=Brushes.White};root.Children.Add(viewport);
        root.Measure(new System.Windows.Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);return pixels;
    }
}
#endif
