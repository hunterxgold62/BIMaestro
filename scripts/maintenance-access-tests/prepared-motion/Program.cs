using System;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using BIMaestro.MepBooster;

internal static class Program
{
    private static int _checks;
    private static void Check(bool result,string label){_checks++;if(!result)throw new Exception(label);}
    private static bool Near(XYZ a,XYZ b)=>a==null?b==null:b!=null&&(a-b).GetLength()<1e-9;
    private static MaintenanceAccessProfile Profile(MaintenanceAccessOperation op, bool crouched,double yaw,bool mirrored)
    {
        var p=MaintenanceAccessProfile.For(op);p.Frame.Origin=new XYZ(10,-4,op==MaintenanceAccessOperation.ChainwheelValve?9:4);
        p.Frame.BasisY=XYZ.BasisZ;p.Frame.BasisZ=-XYZ.BasisY;
        if(mirrored)p.Frame.BasisX=-XYZ.BasisX;
        p.OperatorOrigin=new XYZ(10,-5,0);p.OperatorPosture=crouched?MaintenanceOperatorPosture.Crouched:MaintenanceOperatorPosture.Standing;
        p.BodyYawDegrees=yaw;p.RemovalDirection=new XYZ(.3,.2,1);return p;
    }
    private static void Same(MaintenanceAccessScene a,MaintenanceAccessScene b)
    {
        Check(a.Reachable==b.Reachable&&a.OperatorPlaced==b.OperatorPlaced,"reach/placement drift");
        Check(Near(a.LeftHand,b.LeftHand)&&Near(a.RightHand,b.RightHand)&&Near(a.OperatorFoot,b.OperatorFoot),"joint drift");
        Check(a.Boxes.Count==b.Boxes.Count&&a.Lines.Count==b.Lines.Count,"scene topology drift");
        for(int i=0;i<a.Boxes.Count;i++)
        {
            var x=a.Boxes[i];var y=b.Boxes[i];
            Check(x.Role==y.Role&&x.Label==y.Label&&x.CheckCollision==y.CheckCollision&&x.CheckSource==y.CheckSource
                &&x.DependsOnOperator==y.DependsOnOperator&&Near(x.Size,y.Size)&&Near(x.Center,y.Center)&&Near(x.Translation,y.Translation),"box metadata drift");
            Check(x.Corners().Zip(y.Corners(),Near).All(v=>v),"box coordinates drift");
        }
        for(int i=0;i<a.Lines.Count;i++)Check(a.Lines[i].Points.Zip(b.Lines[i].Points,Near).All(v=>v),"line drift");
        Check(a.Warnings.SequenceEqual(b.Warnings)&&a.ConstraintNotes.SequenceEqual(b.ConstraintNotes)&&a.Assumptions.SequenceEqual(b.Assumptions),"diagnostic drift");
    }
    private static void Main()
    {
        foreach(MaintenanceAccessOperation op in Enum.GetValues(typeof(MaintenanceAccessOperation)))
        foreach(bool mirrored in new[]{false,true})
        foreach(var posture in new[]{new{Crouched=false,Yaw=0.0},new{Crouched=false,Yaw=90.0},new{Crouched=true,Yaw=-45.0}})
        {
            var p=Profile(op,posture.Crouched,posture.Yaw,mirrored);long before=MaintenanceAccessGeometry.HumanPoseEvaluationCount;
            var prepared=MaintenanceAccessGeometry.Prepare(p);
            Check(prepared.SampleCount==49&&MaintenanceAccessGeometry.HumanPoseEvaluationCount-before==49,"preparation does not evaluate exactly49postures");
            foreach(double phase in new[]{0,.001,.25,.5,.999,1.0})
            {
                var reference=MaintenanceAccessGeometry.Pose(p,phase);long current=MaintenanceAccessGeometry.HumanPoseEvaluationCount;
                Same(reference,MaintenanceAccessGeometry.PosePrepared(prepared,phase));
                Check(MaintenanceAccessGeometry.HumanPoseEvaluationCount==current,"prepared animation repeated IK");
            }
            Same(MaintenanceAccessGeometry.Build(p),MaintenanceAccessGeometry.BuildPrepared(prepared));
            Check(MaintenanceAccessGeometry.BuildPrepared(prepared).CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace||b.Role==MaintenanceAccessRole.HumanArm
                ||b.Label.StartsWith("Prise de main",StringComparison.Ordinal)).All(b=>b.DependsOnOperator),"operator-dependent volume missing flag");
            var original=MaintenanceAccessGeometry.PosePrepared(prepared,.3);
            p.Frame.Origin=new XYZ(999,999,999);p.BodyYawDegrees=180;p.OperatorHeight*=1.1;p.RemovalTravel*=2;
            Same(original,MaintenanceAccessGeometry.PosePrepared(prepared,.3));
        }
        var benchProfile=Profile(MaintenanceAccessOperation.HandwheelValve,false,0,false);var motion=MaintenanceAccessGeometry.Prepare(benchProfile);
        for(int i=0;i<200;i++)MaintenanceAccessGeometry.PosePrepared(motion,(i%100)/100.0);
        void Measure(string label,Func<int,MaintenanceAccessScene> pose)
        {
            const int count=3000;var times=new double[count];GC.Collect();long allocated=GC.GetAllocatedBytesForCurrentThread(),evaluations=MaintenanceAccessGeometry.HumanPoseEvaluationCount;
            var total=Stopwatch.StartNew();
            for(int i=0;i<count;i++){long start=Stopwatch.GetTimestamp();pose(i);times[i]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;}
            total.Stop();Array.Sort(times);
            Console.WriteLine(label+": mean="+(total.Elapsed.TotalMilliseconds/count).ToString("0.0000")+"ms p95="+times[(int)(count*.95)].ToString("0.0000")
                +"ms p99="+times[(int)(count*.99)].ToString("0.0000")+"ms bytes/pose="+((GC.GetAllocatedBytesForCurrentThread()-allocated)/count)
                +" IK="+(MaintenanceAccessGeometry.HumanPoseEvaluationCount-evaluations));
        }
        Measure("Repeated preparation / compatibility API",i=>MaintenanceAccessGeometry.Pose(benchProfile,(i%100)/100.0));
        Measure("Prepared motion reused",i=>MaintenanceAccessGeometry.PosePrepared(motion,(i%100)/100.0));
        Console.WriteLine("PASS "+_checks+" prepared snapshot/parity/IK assertions; pure substitutes, native Booleans and WPF presentation not measured.");
    }
}
