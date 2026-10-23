$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$extended=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.Extended.cs'))
function Read-Block([string]$Text,[string]$Signature) {
    $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal)
    if($start -lt 0){throw "Production block missing: $Signature"}
    $opening=$Text.IndexOf('{',$start);$depth=1
    for($i=$opening+1;$i -lt $Text.Length;$i++){
        if($Text[$i] -eq '{'){$depth++}
        if($Text[$i] -eq '}'){$depth--}
        if(!$depth){return $Text.Substring($start,$i-$start+1)}
    }
    throw "Unterminated production block: $Signature"
}
$dto=Read-Block $source 'internal sealed class HistoryPlacement'
$curve=Read-Block $source 'internal sealed class HistoryCurve'
$capture=Read-Block $source 'internal static HistoryCurve CaptureCurve(Curve curve)'
$restore=Read-Block $source 'internal static Curve RestoreCurve(HistoryCurve curve)'
$legacy=Read-Block $extended 'private static HistoryPlacement SavedHistoricalPlacement(HistoryRecipe recipe)'
$preamble=@'
using System;
using System.Collections.Generic;
namespace HistoricalPlacementFixture {
sealed class HistoryParameter { }
sealed class HistoryRecipe {
    public HistoryPlacement HistoricalPlacement {get;set;}
    public string Kind {get;set;}
    public double[] Point {get;set;}
    public double[] BasisX {get;set;}
    public double[] BasisZ {get;set;}
    public bool Mirrored {get;set;}
    public bool HandFlipped {get;set;}
    public bool FacingFlipped {get;set;}
}
sealed class XYZ {
    public double X,Y,Z;
    public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
}
abstract class Curve {
    public bool IsBound=true;
    public abstract XYZ GetEndPoint(int i);
    public abstract XYZ Evaluate(double t,bool normalized);
}
sealed class Line:Curve {
    XYZ start,end;
    public static Line CreateBound(XYZ a,XYZ b){return new Line{start=a,end=b};}
    public override XYZ GetEndPoint(int i){return i==0?start:end;}
    public override XYZ Evaluate(double t,bool normalized){return new XYZ(start.X+(end.X-start.X)*t,start.Y+(end.Y-start.Y)*t,start.Z+(end.Z-start.Z)*t);}
}
sealed class Arc:Curve {
    public XYZ Center,XDirection,YDirection; public double Radius,Start,End; public bool Legacy;
    XYZ legacyStart,legacyEnd,legacyMid;
    public static Arc Create(XYZ center,double radius,double start,double end,XYZ x,XYZ y){return new Arc{Center=center,Radius=radius,Start=start,End=end,XDirection=x,YDirection=y};}
    public static Arc Create(XYZ a,XYZ b,XYZ mid){return new Arc{Legacy=true,legacyStart=a,legacyEnd=b,legacyMid=mid};}
    public double GetEndParameter(int i){return i==0?Start:End;}
    public override XYZ GetEndPoint(int i){return Legacy?(i==0?legacyStart:legacyEnd):Evaluate(i,true);}
    public override XYZ Evaluate(double t,bool normalized){
        if(Legacy)return legacyMid;
        double angle=normalized?Start+(End-Start)*t:t,c=Math.Cos(angle)*Radius,s=Math.Sin(angle)*Radius;
        return new XYZ(Center.X+c*XDirection.X+s*YDirection.X,Center.Y+c*XDirection.Y+s*YDirection.Y,Center.Z+c*XDirection.Z+s*YDirection.Z);
    }
}
'@
$policy=@'
static class Policy {
    static double[] Pack(XYZ p){return new[]{p.X,p.Y,p.Z};}
    static XYZ Unpack(double[] p){return new XYZ(p[0],p[1],p[2]);}
    public static HistoryPlacement Legacy(HistoryRecipe recipe){return SavedHistoricalPlacement(recipe);}
'@
$checks=@'
}
public static class PlacementChecks {
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static double[] V(double x,double y,double z){return new[]{x,y,z};}
    static HistoryPlacement Frame(){return new HistoryPlacement{Point=V(45.95532354790555,213.74347622727984,127.0419873669978),Origin=V(45.95532354790555,213.74347622727984,127.0419873669978),BasisX=V(0,0,1),BasisY=V(-1,0,0),BasisZ=V(0,-1,0),Mirrored=false,HandFlipped=false,FacingFlipped=false,BBox=new List<double[]>{V(45.423991526908154,213.21214420628246,126.93864091030488),V(46.486655568902925,214.27480824827722,128.02952017539675)}};}
    public static string Run(){
        var expected=Frame();var actual=Frame();
        Check(expected.Difference(actual,true)==null,"Exact frame and bbox must pass");
        actual.Point[2]+=300.0/304.8;
        Check(expected.Difference(actual,false)!=null,"Observed 300mm reducer shift must fail");
        actual=Frame();actual.BasisX=V(0,0,-1);actual.BasisZ=V(0,1,0);
        Check(expected.Difference(actual,false)!=null,"Observed 180-degree reducer inversion must fail even with the right point");
        actual=Frame();actual.BasisY=V(1,0,0);
        Check(expected.Difference(actual,false)!=null,"The third transform axis must not be ignored");
        actual=Frame();actual.Mirrored=true;
        Check(expected.Difference(actual,false)!=null,"Mirror mismatch must fail");
        actual=Frame();actual.HandFlipped=true;
        Check(expected.Difference(actual,false)!=null,"Hand mismatch must fail");
        actual=Frame();actual.FacingFlipped=true;
        Check(expected.Difference(actual,false)!=null,"Facing mismatch must fail");
        actual=Frame();actual.BBox[1][0]+=0.11602179659857939/304.8;
        Check(expected.Difference(actual,false)==null,"A pre-relations placement check must defer bbox");
        Check(expected.Difference(actual,true)!=null,"Observed floor 0.116mm bbox drift must fail the final bbox check");
        actual=Frame();actual.Point[0]+=1e-7;
        Check(expected.Difference(actual,false)==null,"Sub-tolerance numeric noise must pass");
        actual=Frame();actual.Point[0]=double.NaN;
        Check(expected.Difference(actual,false)!=null,"NaN must never pass placement verification");
        actual=Frame();actual.Origin=null;
        Check(expected.Difference(actual,false)!=null,"Missing required transform origin must fail");
        var legacy=Policy.Legacy(new HistoryRecipe{Kind="family",Point=expected.Point,BasisX=expected.BasisX,BasisZ=expected.BasisZ});
        Check(legacy.Difference(Frame(),false)==null,"Old family recipes must retain point/axes validation");
        var line=new HistoryCurve{Start=V(0,0,0),End=V(1,0,0)};
        var reversed=new HistoryCurve{Start=V(1,0,0),End=V(0,0,0)};
        Check(new HistoryPlacement{Curve=line}.Difference(new HistoryPlacement{Curve=reversed},false)==null,"Reversed line endpoints represent the same axis");
        reversed.Mid=V(.5,.1,0);
        Check(new HistoryPlacement{Curve=line}.Difference(new HistoryPlacement{Curve=reversed},false)!=null,"An arc must not pass as a straight line with the same endpoints");
        var arc=Arc.Create(new XYZ(1e6,-2e6,115.9),1e9,1.9,1.9000001,new XYZ(0,1,0),new XYZ(1,0,0));
        var saved=Policy.CaptureCurve(arc);var restored=(Arc)Policy.RestoreCurve(saved);
        Check(!restored.Legacy && restored.Radius==arc.Radius && restored.Start==arc.Start && restored.End==arc.End,"Exact arc radius and parameter bounds must survive capture/restore dispatch");
        Check(HistoryPlacement.SameVector(saved.Center,new[]{restored.Center.X,restored.Center.Y,restored.Center.Z}),"Exact arc center must survive");
        Check(Policy.RestoreCurve(new HistoryCurve{Start=V(0,0,0),End=V(2,0,0),Mid=V(1,1,0)}) is Arc old && old.Legacy,"Old three-point arc histories must remain supported");
        Check(Policy.RestoreCurve(Policy.CaptureCurve(Line.CreateBound(new XYZ(0,0,0),new XYZ(2,0,0)))) is Line,"Straight-line histories must stay straight");
        return "PASS: actual production placement comparator (translations, frames, flips, bbox staging, NaN, legacy recipes) and exact/legacy arc serialization dispatch. Geometry adapters are plain test doubles; no Revit movement/solver validation.";
    }
}
}
'@
Add-Type -TypeDefinition ($preamble+"`n"+$dto+"`n"+$curve+"`n"+$policy+"`n"+$capture+"`n"+$restore+"`n"+$legacy+"`n"+$checks) -IgnoreWarnings
[HistoricalPlacementFixture.PlacementChecks]::Run()
