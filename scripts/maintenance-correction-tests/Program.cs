using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIMaestro.MepBooster;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _checks;
    private static string _folder, _path;
    private static void Check(bool value,string message){_checks++;if(!value)throw new Exception(message);}
    private static void Near(XYZ a,XYZ b,string message)=>Check((a-b).GetLength()<1e-8,message+" distance="+(a-b).GetLength());
    private static void Near(double a,double b,string message)=>Check(Math.Abs(a-b)<1e-8,message+" "+a+" != "+b);
    private static double Num(MaintenanceWorkbenchState s,string key)=>double.Parse(s.Fields[key],CultureInfo.CurrentCulture);
    private static Transform Rigid(double yaw,double tilt=0,int mirror=0)
    {
        double c=Math.Cos(yaw),s=Math.Sin(yaw),ct=Math.Cos(tilt),st=Math.Sin(tilt);
        var t=new Transform{Origin=new XYZ(120.125,-42.25,7),BasisX=new XYZ(c,s,0),BasisY=new XYZ(-s*ct,c*ct,st),BasisZ=new XYZ(s*st,-c*st,ct)};
        if(mirror==1)t.BasisX=-t.BasisX;if(mirror==2)t.BasisY=-t.BasisY;if(mirror==3)t.BasisZ=-t.BasisZ;return t;
    }
    private static FamilyInstance Entity(Document doc,Transform frame=null,bool alternate=false,double size=1)
    {
        var mesh=new Mesh();var points=Enumerable.Range(0,8).Select(i=>new XYZ((i&1)==0?-.3:.3,(i&2)==0?-.2:.2,(i&4)==0?0:size)).ToArray();
        int[][] faces={new[]{0,1,3,2},new[]{4,5,7,6},new[]{0,4,5,1},new[]{2,3,7,6},new[]{0,2,6,4},new[]{1,5,7,3}};
        foreach(var f in faces)
        {
            int[][] tri=alternate?new[]{new[]{f[0],f[1],f[3]},new[]{f[1],f[2],f[3]}}:new[]{new[]{f[0],f[1],f[2]},new[]{f[0],f[2],f[3]}};
            foreach(var v in tri)mesh.Triangles.Add(new MeshTriangle{Points=v.Select(i=>points[i]).ToArray()});
        }
        mesh.Vertices.AddRange(points);
        var e=new FamilyInstance{Frame=frame??Transform.Identity,Shape=new GeometryElement(mesh),MEPModel=new MEPModel()};
        foreach(var p in new[]{new XYZ(0,-.2,.2),new XYZ(0,.2,.2)})
            e.MEPModel.ConnectorManager.Connectors.Add(new Connector{Origin=e.Frame.OfPoint(p),CoordinateSystem=new Transform{BasisZ=e.Frame.OfVector(XYZ.BasisY)}});
        e.Parameters.Add(new Parameter{Definition=new Definition{Name="Hauteur géométrique"},Number=size});
        e.Symbol.Parameters.Add(new Parameter{Definition=new Definition{Name="Course levier",Spec="autodesk.spec.aec:angle-2.0.0"},Number=1.2});
        e.Parameters.Add(new Parameter{Id=new ElementId(-1000),Definition=new Definition{Name="Altitude"},Number=e.Frame.Origin.Z});
        doc.Elements[e.UniqueId]=e;return e;
    }
    private static MaintenanceWorkbenchState State(FamilyInstance e,MaintenanceAccessOperation operation=MaintenanceAccessOperation.LeverValve)
    {
        var command=e.Frame.OfPoint(new XYZ(.1,.05,.8));var t=Transform.Identity;t.Origin=command;
        var s=new MaintenanceWorkbenchState{Operation=operation,CommandPoint=command,DetectedFrame=t,Axis=6,RotationAssumed=true,Measured=true};
        s.Fields["Radius"]="125";s.Fields["Thickness"]="12";s.Fields["InitialAngle"]="31";s.Fields["Angle"]="-73";s.Fields["OffsetX"]="15";s.Fields["OffsetY"]="27";s.Fields["OffsetZ"]="-80";s.Fields["Pull"]="225";
        s.FeatureWarnings.Add("Course supposée, à confirmer.");return s;
    }
    private static void NewFile(string name)
    {
        _path=Path.Combine(_folder,name+".json");Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH",_path);
    }
    public static int Main()
    {
        string before=Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH");var culture=CultureInfo.CurrentCulture;
        _folder=Path.GetFullPath(Path.Combine("tmp","maintenance-correction-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(_folder);
        try
        {
            Transformations();QuantisationAndBakedFlips();Invalidations();InterventionScopes();Persistence();CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");RoundTrips();
            Console.WriteLine("Maintenance correction memory: "+_checks+" checks passed. Artifacts: "+_folder);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH",before);CultureInfo.CurrentCulture=culture;}
    }
    private static void QuantisationAndBakedFlips()
    {
        NewFile("native-grid-and-baked-flips");var doc=new Document();
        void Boundary(FamilyInstance entity)
        {
            var mesh=(Mesh)entity.Shape.Items[0];foreach(var point in mesh.Vertices)point.X=Math.Sign(point.X)*(30.75/304.8);
        }
        var source=Entity(doc);Boundary(source);var initial=State(source);Check(MaintenanceCorrectionMemory.Save(doc,source,initial,out var note),"Save exact native30.75mm half-grid fixture: "+note);
        var p=MaintenanceAutoconfiguration.ReadSavedProfile(source,initial);
        foreach(double yaw in new[]{0,.17,37*Math.PI/180,Math.PI/2,-.4})foreach(double offset in new[]{20000,100000})
        {
            var frame=Rigid(yaw,.2);frame.Origin=new XYZ(offset+65.89004955464934,offset+190.1565317872504,118.52820783943875);
            var e=Entity(doc,frame,alternate:true);Boundary(e);var s=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};
            Check(MaintenanceCorrectionMemory.TryApply(doc,e,s,out note),"Same half-grid shape atkilometres fromorigin stays compatible: "+note);
            Near(s.CommandPoint,e.Frame.OfPoint(p.Frame.Origin),"Far-origin command stays physical");
        }
        foreach(bool facing in new[]{false,true})foreach(double yaw in new[]{0,.7,2.8})
        {
            var target=Entity(doc,Rigid(yaw,.3));Boundary(target);
            var flip=Transform.Identity;if(facing)flip.BasisY=-flip.BasisY;else flip.BasisX=-flip.BasisX;
            target.Shape=new GeometryElement(new GeometryInstance{Transform=flip,Geometry=target.Shape});
            target.Mirrored=true;target.FacingFlipped=facing;target.HandFlipped=!facing;
            foreach(var connector in target.MEPModel.ConnectorManager.Connectors)
            {
                connector.Origin=target.Frame.OfPoint(flip.OfPoint(target.Frame.Inverse.OfPoint(connector.Origin)));
                connector.CoordinateSystem.BasisZ=target.Frame.OfVector(flip.OfVector(target.Frame.Inverse.OfVector(connector.CoordinateSystem.BasisZ)));
            }
            var expected=target.Frame.Multiply(flip);var restored=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};
            Check(MaintenanceCorrectionMemory.TryApply(doc,target,restored,out note),"Native properGetTransform with baked "+(facing?"FacingFlipped":"HandFlipped")+": "+note);
            Near(restored.CommandPoint,expected.OfPoint(p.Frame.Origin),"Baked mirror command follows physicalreflection");
            var q=MaintenanceAutoconfiguration.ReadSavedProfile(target,restored);
            Near(Num(restored,"InitialAngle"),-31,"Baked flip initialangle inverts");Near(Num(restored,"Angle"),73,"Baked flip course inverts");Near(Num(restored,"OffsetY"),-27,"Baked flip offsetY inverts");
            foreach(double phase in new[]{0,.125,.5,.875,1})
            {
                double a=(p.InitialAngleDegrees+p.RotationDegrees*phase)*Math.PI/180,b=(q.InitialAngleDegrees+q.RotationDegrees*phase)*Math.PI/180;
                Near(q.Frame.OfPoint(new XYZ(q.HandleRadius*Math.Cos(b),q.HandleRadius*Math.Sin(b),0)),expected.OfPoint(p.Frame.OfPoint(new XYZ(p.HandleRadius*Math.Cos(a),p.HandleRadius*Math.Sin(a),0))),"Baked mirror actual grip path");
                Near(q.Frame.OfPoint(q.RemovalCenterOffset+XYZ.BasisZ*q.RemovalTravel*phase),expected.OfPoint(p.Frame.OfPoint(p.RemovalCenterOffset+XYZ.BasisZ*p.RemovalTravel*phase)),"Baked mirror removal volume path");
            }
            Check(MaintenanceCorrectionMemory.Save(doc,target,restored,out note),"Re-save from baked mirror");
            var back=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};Check(MaintenanceCorrectionMemory.TryApply(doc,source,back,out note),"Baked mirror recipe returns to normal source: "+note);
            Near(Num(back,"InitialAngle"),31,"Baked reverse initialangle");Near(Num(back,"Angle"),-73,"Baked reverse course");Near(Num(back,"OffsetY"),27,"Baked reverse offsetY");
        }
        var improper=Entity(doc,Rigid(.4,.3,1));Boundary(improper);improper.Mirrored=true;improper.HandFlipped=true;
        var negative=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};Check(MaintenanceCorrectionMemory.TryApply(doc,improper,negative,out note),"Already improper native transform is not flipped twice: "+note);
        Near(negative.CommandPoint,improper.Frame.OfPoint(p.Frame.Origin),"No double reflection");
        var ambiguous=Entity(doc);Boundary(ambiguous);ambiguous.Mirrored=true;
        Check(!MaintenanceCorrectionMemory.TryApply(doc,ambiguous,new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve},out _),"Ambiguous mirror metadata abstains");
        ambiguous.HandFlipped=ambiguous.FacingFlipped=true;Check(!MaintenanceCorrectionMemory.TryApply(doc,ambiguous,new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve},out _),"Contradictory mirror metadata abstains");
        var changed=Entity(doc);Boundary(changed);foreach(var point in ((Mesh)changed.Shape.Items[0]).Vertices)point.X+=.5/304.8;
        Check(!MaintenanceCorrectionMemory.TryApply(doc,changed,new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve},out _),"Half-millimetre geometry change still refused; no grid enlargement");
        var zeroMargin=State(source);zeroMargin.Fields["Margin"]="0";Check(MaintenanceCorrectionMemory.Save(doc,source,zeroMargin,out _),"Zero margin is legal");
        zeroMargin.Fields["Margin"]="-1";Check(!MaintenanceCorrectionMemory.Save(doc,source,zeroMargin,out _),"Negative margin refused");
        zeroMargin.Fields["Margin"]="1001";Check(!MaintenanceCorrectionMemory.Save(doc,source,zeroMargin,out _),"Marginover1000mm refused");
        zeroMargin.Fields["Margin"]="1";zeroMargin.Fields["Grip"]="1";Check(!MaintenanceCorrectionMemory.Save(doc,source,zeroMargin,out _),"Gripunder2mm refused");
    }
    private static void Transformations()
    {
        NewFile("transforms");var doc=new Document();var source=Entity(doc);var state=State(source);
        Check(MaintenanceCorrectionMemory.Save(doc,source,state,out var note),"Save unconfirmed hypothesis: "+note);
        var json=File.ReadAllText(_path);Check(!json.Contains("Foot")&&!json.Contains("Posture")&&!json.Contains("Confirmed")&&!json.Contains(source.UniqueId),"No feet/posture/confirmed/Revit ids persisted");
        var original=MaintenanceAutoconfiguration.ReadSavedProfile(source,state);
        foreach(double yaw in new[]{0,.17,1.7,Math.PI,-2.4})foreach(double tilt in new[]{0,.4,1.3})foreach(int mirror in new[]{0,1,2,3})
        {
            var target=Entity(doc,Rigid(yaw,tilt,mirror),alternate:true);target.Symbol.Name="Renamed type";target.Symbol.Family.Name="Renamed family";
            var restored=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.HandwheelValve,Confirmed=true,FootPoint=XYZ.Zero,SupportVerified=true,WholeGestureChecked=true,WholeGestureClear=true,RejectedFoot=XYZ.Zero};
            restored.PlacementWarnings.Add("stale");restored.NoOperatorReason="Previous gesture out of reach";restored.BlockingObstacles.Add(new MaintenanceAccessObstacle());restored.GestureUnknown.Add("stale");
            Check(MaintenanceCorrectionMemory.TryApply(doc,target,restored,out note),"Apply transform "+yaw+"/"+tilt+"/"+mirror+": "+note);
            Check(restored.Operation==MaintenanceAccessOperation.LeverValve&&!restored.Confirmed&&restored.FootPoint==null&&!restored.SupportVerified&&!restored.WholeGestureClear&&!restored.WholeGestureChecked,"No validation/foot recycled");
            Check(restored.BlockingObstacles.Count==0&&restored.GestureUnknown.Count==0&&restored.RejectedFoot==null&&restored.PlacementWarnings.Count==0&&string.IsNullOrEmpty(restored.NoOperatorReason),"Stale results and failure reason cleared");
            Check(restored.RotationAssumed&&restored.Measured&&!restored.Manufacturer,"Provenance restored");Near(Num(restored,"Pull"),225,"Chain pull restored");
            var applied=MaintenanceAutoconfiguration.ReadSavedProfile(target,restored);
            Near(applied.Frame.Origin,target.Frame.OfPoint(original.Frame.Origin),"Local command transformed");
            Near(applied.Frame.BasisX.CrossProduct(applied.Frame.BasisY),applied.Frame.BasisZ,"Maintenance frame remains right handed");
            foreach(double phase in new[]{0,.125,.5,.875,1})
            {
                double a=(original.InitialAngleDegrees+original.RotationDegrees*phase)*Math.PI/180;
                double b=(applied.InitialAngleDegrees+applied.RotationDegrees*phase)*Math.PI/180;
                XYZ oldHand=original.Frame.OfPoint(new XYZ(original.HandleRadius*Math.Cos(a),original.HandleRadius*Math.Sin(a),0));
                XYZ newHand=applied.Frame.OfPoint(new XYZ(applied.HandleRadius*Math.Cos(b),applied.HandleRadius*Math.Sin(b),0));
                Near(newHand,target.Frame.OfPoint(oldHand),"Actual initial/middle/final hand direction mirrored correctly");
                XYZ oldCenter=original.Frame.OfPoint(original.RemovalCenterOffset+XYZ.BasisZ*(original.RemovalTravel*phase));
                XYZ newCenter=applied.Frame.OfPoint(applied.RemovalCenterOffset+XYZ.BasisZ*(applied.RemovalTravel*phase));
                Near(newCenter,target.Frame.OfPoint(oldCenter),"Removal centre including OffsetY mirrored correctly");
            }
        }
        // Saving from a mirror must also be reusable on an unmirrored source.
        var mirrored=Entity(doc,Rigid(.8,.3,1));var fromMirror=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.HandwheelValve};
        Check(MaintenanceCorrectionMemory.TryApply(doc,mirrored,fromMirror,out note),"Prepare mirrored re-save");Check(MaintenanceCorrectionMemory.Save(doc,mirrored,fromMirror,out note),"Save from mirror");
        var back=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};Check(MaintenanceCorrectionMemory.TryApply(doc,source,back,out note),"Return from mirror");Near(Num(back,"Angle"),-73,"Double parity inversion returns initial rotation");Near(Num(back,"InitialAngle"),31,"Double parity initial angle");Near(Num(back,"OffsetY"),27,"Double parity offset");
    }
    private static void Invalidations()
    {
        NewFile("invalidation");var doc=new Document();var source=Entity(doc);var state=State(source);Check(MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Save invalidation fixture");
        bool Apply(FamilyInstance e)=>MaintenanceCorrectionMemory.TryApply(doc,e,new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.HandwheelValve},out _);
        var size=Entity(doc,size:1.1);Check(!Apply(size),"Dimensioned variant rejected");
        var parameter=Entity(doc);parameter.Parameters[0].Number+=.000001;Check(!Apply(parameter),"Parameter change below geometry grid rejected");
        var angle=Entity(doc);angle.Symbol.Parameters[0].Number+=.01;Check(!Apply(angle),"Type determining parameter invalidated");
        var connector=Entity(doc);connector.MEPModel.ConnectorManager.Connectors[0].Radius+=.01;Check(!Apply(connector),"Connector size invalidated");
        var location=Entity(doc,Rigid(.7));Check(Apply(location),"Project elevation excluded; local shape still exact");
        var missing=Entity(doc);missing.Shape=null;Check(!Apply(missing),"Missing geometry abstains");
        var unknown=Entity(doc);unknown.Shape.Items.Add(new GeometryObject());Check(!Apply(unknown),"Unsupported geometry abstains, no partial signature");
        var scale=Entity(doc);scale.Frame.BasisX=scale.Frame.BasisX*2;Check(!Apply(scale),"No global rescaling inferred");
        var product=Entity(doc);product.Symbol.Parameters.Add(new Parameter{StorageType=StorageType.String,Definition=new Definition{Name="Référence produit"},Text="OTHER"});Check(!Apply(product),"Product reference mismatch rejected");
        var invalid=State(source);invalid.CommandPoint=null;Check(!MaintenanceCorrectionMemory.Save(doc,source,invalid,out _),"Missing pivot cannot be saved");
        invalid=State(source);invalid.DetectedFrame.BasisX=new XYZ(double.NaN,0,0);Check(!MaintenanceCorrectionMemory.Save(doc,source,invalid,out _),"Nonfinite axes rejected");
        invalid=State(source);invalid.Fields["Height"]="NaN";Check(!MaintenanceCorrectionMemory.Save(doc,source,invalid,out _),"Invalid full profile not saved");
        invalid=State(source);invalid.Axis=7;Check(!MaintenanceCorrectionMemory.Save(doc,source,invalid,out _),"Unknown axis rejected");
        // Different mechanisms can coexist; generic valve recognition abstains.
        var chain=State(source,MaintenanceAccessOperation.ChainwheelValve);Check(MaintenanceCorrectionMemory.Save(doc,source,chain,out _),"Second mechanism saved");
        Check(!Apply(source),"Ambiguous generic valve memory abstains");var explicitLever=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve};Check(MaintenanceCorrectionMemory.TryApply(doc,source,explicitLever,out _),"Explicit mechanism unambiguous");
        var filter=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.Filter};Check(!MaintenanceCorrectionMemory.TryApply(doc,source,filter,out _),"Filter never inherits valve recipe");
        Check(MaintenanceCorrectionMemory.Remove(doc,source,MaintenanceAccessOperation.LeverValve,out _),"Forget current mechanism only");Check(Apply(source),"Remaining chain uniquely selected");
        Check(!MaintenanceCorrectionMemory.Remove(doc,source,MaintenanceAccessOperation.LeverValve,out _),"Forget missing does not delete chain");
        // A manufacturer's note is not transferred to a visually identical but
        // differently referenced product, even in another family/type.
        NewFile("product");source.Symbol.Parameters.Add(new Parameter{StorageType=StorageType.String,Definition=new Definition{Name="Référence produit"},Text="ABC-1"});state.Manufacturer=true;state.Notice="Manufacturer ABC-1 rev2";
        Check(MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Save documented product");var equivalent=Entity(doc);equivalent.Symbol.Parameters.Add(new Parameter{StorageType=StorageType.String,Definition=new Definition{Name="Référence produit"},Text="ABC-1"});Check(Apply(equivalent),"Same product reference portable");Check(!Apply(product),"Different product cannot inherit manufacturer provenance");
    }
    private static void InterventionScopes()
    {
        NewFile("intervention-scopes");var doc=new Document();var source=Entity(doc);
        var removal=State(source,MaintenanceAccessOperation.Actuator);
        Check(MaintenanceCorrectionMemory.Save(doc,source,removal,out _),"Actuator removal saved independently");
        var manual=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.HandwheelValve};
        Check(!MaintenanceCorrectionMemory.TryApply(doc,source,manual,out _),"Manual operation cannot inherit actuator removal");
        Check(manual.Operation==MaintenanceAccessOperation.HandwheelValve&&manual.CommandPoint==null,"Rejected removal leaves manual intent and pivot unchanged");
        var explicitRemoval=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.Actuator};
        Check(MaintenanceCorrectionMemory.TryApply(doc,source,explicitRemoval,out _),"Explicit actuator removal reuses its own correction");
        Check(explicitRemoval.Operation==MaintenanceAccessOperation.Actuator,"Explicit removal remains removal");
        Check(MaintenanceCorrectionMemory.Save(doc,source,State(source,MaintenanceAccessOperation.LeverValve),out _),"Manual correction coexists with actuator removal");
        Check(MaintenanceCorrectionMemory.TryApply(doc,source,manual,out _)&&manual.Operation==MaintenanceAccessOperation.LeverValve,"Generic manual entry selects only the manual correction");
        explicitRemoval=new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.Actuator};
        Check(MaintenanceCorrectionMemory.TryApply(doc,source,explicitRemoval,out _)&&explicitRemoval.Operation==MaintenanceAccessOperation.Actuator,"Manual correction does not make explicit removal ambiguous");
    }
    private static void RoundTrips()
    {
        NewFile("roundtrips-fr");var doc=new Document();var source=Entity(doc);var state=State(source,MaintenanceAccessOperation.ChainwheelValve);state.Fields["Radius"]="125,125";state.Fields["Pull"]="225,75";state.Fields["Grip"]="12,75";state.Fields["Margin"]="3,125";state.FeatureWarnings.Add(state.FeatureWarnings[0]);
        for(int i=0;i<40;i++)
        {
            Check(MaintenanceCorrectionMemory.Save(doc,source,state,out var note),"Repeated save "+i+": "+note);
            Check(MaintenanceCorrectionMemory.TryApply(doc,source,state,out note),"Repeated apply "+i+": "+note);
            Check(state.FeatureWarnings.Count==3&&state.FeatureWarnings.Distinct().Count()==3,"Warnings never grow/duplicate across reuse");
            Near(Num(state,"Radius"),125.125,"FR decimal retained");Near(Num(state,"Pull"),225.75,"FR pull retained");
            Near(Num(state,"Grip"),12.75,"Grip clearance retained");Near(Num(state,"Margin"),3.125,"Collision margin retained");
        }
        var file=MaintenanceCorrectionData.Read(_path);Check(file.Corrections.Count==1&&file.Corrections[0].Warnings.Count==1,"One recipe and original warning only on disk");
        Check(!File.ReadAllText(_path).Contains("WorkHeight"),"Chain grip height linked to floor is not memorised");
    }
    private static void Persistence()
    {
        NewFile("atomic");var doc=new Document();var source=Entity(doc);var state=State(source);Check(MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Atomic first save");
        byte[] original=File.ReadAllBytes(_path);var valid=MaintenanceCorrectionData.Read(_path);
        for(int i=0;i<20;i++){state.Fields["Radius"]=(125+i).ToString(CultureInfo.CurrentCulture);Check(MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Atomic replace");Check(MaintenanceCorrectionData.Read(_path).Corrections.Count==1,"Replacement parses as complete file");}
        Check(Directory.GetFiles(_folder,"*.tmp").Length==0,"No temporary file leaked");
        using(var locked=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.None))Check(!MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Sharing failure nonblocking");
        Check(MaintenanceCorrectionData.Read(_path).Corrections.Count==1,"Existing file retained after sharing failure");
        var invalid=new MaintenanceCorrectionFile();invalid.Corrections.Add(null);bool rejected=false;try{MaintenanceCorrectionData.Write(_path,invalid);}catch(InvalidDataException){rejected=true;}Check(rejected&&MaintenanceCorrectionData.Read(_path).Corrections.Count==1,"Bad update cannot destroy previous memory");
        var contents=File.ReadAllText(_path);foreach(var corrupt in new[]{"{","{}","null","{\"Version\":1,\"Version\":1,\"Corrections\":[]}",contents+" {}",contents.Replace("\"Version\": 1","\"Version\": 99"),contents.Replace("\"Angle\": -73.0","\"Angle\": 9999.0")})
        {
            File.WriteAllText(_path,corrupt);Check(!MaintenanceCorrectionMemory.TryApply(doc,source,new MaintenanceWorkbenchState{Operation=MaintenanceAccessOperation.LeverValve},out _),"Corrupt/unsupported record abstains");
            Check(!MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Corrupt memory not overwritten silently");Check(File.ReadAllText(_path)==corrupt,"Corrupt bytes preserved for recovery");
        }
        File.WriteAllText(_path,new string('x',MaintenanceCorrectionData.MaximumBytes+1));Check(!MaintenanceCorrectionMemory.TryApply(doc,source,state,out _),"Oversized read bounded");
        File.WriteAllBytes(_path,original);var parsed=JObject.Parse(File.ReadAllText(_path));parsed["Corrections"][0]["Command"][0]=double.NaN;File.WriteAllText(_path,parsed.ToString());Check(!MaintenanceCorrectionMemory.TryApply(doc,source,state,out _),"NaN persisted coordinate rejected");
        File.WriteAllBytes(_path,original);var many=MaintenanceCorrectionData.Read(_path);for(int i=1;i<MaintenanceCorrectionData.MaximumRecords;i++)
        {var clone=JObject.FromObject(many.Corrections[0]).ToObject<MaintenanceCorrectionRecord>();clone.Geometry=MaintenanceCorrectionData.Digest(i.ToString());clone.Signature=MaintenanceCorrectionData.Signature(clone.Geometry,clone.Parameters,clone.Connectors,clone.Type);many.Corrections.Add(clone);}
        MaintenanceCorrectionData.Write(_path,many);var variant=Entity(doc,size:1.3);Check(!MaintenanceCorrectionMemory.Save(doc,variant,State(variant),out _),"Entry limit does not evict other corrections silently");Check(MaintenanceCorrectionData.Read(_path).Corrections.Count==MaintenanceCorrectionData.MaximumRecords,"Limit retained");
        NewFile("parallel");var fixture=valid.Corrections[0];Parallel.For(0,10,i=>MaintenanceCorrectionData.WithLock(_path,()=>{var data=MaintenanceCorrectionData.Read(_path);var clone=JObject.FromObject(fixture).ToObject<MaintenanceCorrectionRecord>();clone.Geometry=MaintenanceCorrectionData.Digest(i.ToString());clone.Signature=MaintenanceCorrectionData.Signature(clone.Geometry,clone.Parameters,clone.Connectors,clone.Type);data.Corrections.Add(clone);MaintenanceCorrectionData.Write(_path,data);return true;}));Check(MaintenanceCorrectionData.Read(_path).Corrections.Count==10,"Competing writers preserve every update");
        Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH",Path.Combine(_folder,"folder-mode"));Check(MaintenanceCorrectionData.FilePath().EndsWith("corrections-v1.json"),"Directory override supported");Environment.SetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH","relative.json");Check(!MaintenanceCorrectionMemory.Save(doc,source,state,out _),"Relative path override rejected");
    }
}
