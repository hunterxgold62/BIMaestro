$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.Extended.cs'))
$start=$source.IndexOf('        private static List<HistoryParameter> ParametersForCurveDrivenFamily(HistoryRecipe recipe)')
$end=$source.IndexOf('        private static FamilyInstance CreateExtendedFamily(', $start)
if($start -lt 0 -or $end -le $start) { throw 'Production structural-curve parameter and placement policies were not found.' }
$policies=$source.Substring($start,$end-$start)
if(!$source.Contains('recipe.Parameters = ParametersForCurveDrivenFamily(recipe);') -or !$source.Contains('ApplyParameters(doc, instance, ParametersForCurveDrivenFamily(recipe));')) {
    throw 'Production capture and restoration must both use the structural-curve parameter policy.'
}
$fixtures=@'
using System;
using System.Collections.Generic;
using System.Linq;
namespace HistoryCurveDrivenFamilyTests {
enum FamilyPlacementType { CurveDrivenStructural,CurveBased,WorkPlaneBased,TwoLevelsBased }
enum BuiltInParameter { STRUCTURAL_BEAM_END0_ELEVATION=-1001571,STRUCTURAL_BEAM_END1_ELEVATION=-1001572,
    STRUCTURAL_BEND_DIR_ANGLE=-1001581,STRUCTURAL_REFERENCE_LEVEL_ELEVATION=-1001591,STRUCTURAL_Y_JUSTIFICATION=-1001501 }
class XYZ {
    internal double X,Y,Z;
    internal XYZ(double x,double y,double z) { X=x;Y=y;Z=z; }
    internal double DistanceTo(XYZ p) { return Math.Sqrt(Math.Pow(X-p.X,2)+Math.Pow(Y-p.Y,2)+Math.Pow(Z-p.Z,2)); }
}
class Curve {
    internal XYZ Start,End,Mid;
    internal bool IsBound=true;
    internal XYZ GetEndPoint(int index) { return index==0 ? Start : End; }
    internal XYZ Evaluate(double parameter,bool normalized) { return Mid; }
}
class LocationCurve { internal Curve Curve; }
class FamilyInstance { internal object Location; }
class Document { internal int Regenerations; internal Action OnRegenerate; internal void Regenerate() { Regenerations++;OnRegenerate?.Invoke(); } }
class HistoryCurve { internal Curve Native; }
class HistoryParameter { internal int BuiltIn; internal string Name; internal double Number; }
class HistoryRecipe { internal string Placement; internal List<List<HistoryCurve>> Loops; internal List<HistoryParameter> Parameters; }
public static class CurveDrivenChecks {
    static Curve RestoreCurve(HistoryCurve curve) { return curve.Native; }
'@
$checks=@'
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static Curve Axis(bool reverse=false,double endpointDrift=0,double middleDrift=0) {
        var a=new XYZ(0,0,2.2681413554290373);
        var b=new XYZ(10,0,2.2681413554290373+endpointDrift);
        return new Curve { Start=reverse ? b : a,End=reverse ? a : b,Mid=new XYZ(5,0,2.2681413554290373+middleDrift) };
    }
    static void MustReject(Document doc,FamilyInstance instance,HistoryRecipe recipe,string message) {
        bool rejected=false;
        try { VerifyCurveDrivenFamilyPlacement(doc,instance,recipe); } catch(InvalidOperationException) { rejected=true; }
        Check(rejected,message);
    }
    public static void Run() {
        var parameters=new List<HistoryParameter> {
            new HistoryParameter { BuiltIn=(int)BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION,Number=2.2681413554290373 },
            new HistoryParameter { BuiltIn=(int)BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION,Number=2.2681413554290373 },
            new HistoryParameter { BuiltIn=(int)BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE,Number=0.23 },
            new HistoryParameter { BuiltIn=(int)BuiltInParameter.STRUCTURAL_REFERENCE_LEVEL_ELEVATION,Number=3 },
            new HistoryParameter { BuiltIn=(int)BuiltInParameter.STRUCTURAL_Y_JUSTIFICATION,Number=2 },
            new HistoryParameter { BuiltIn=0,Name="IPN custom dimension",Number=0.2 }
        };
        var recipe=new HistoryRecipe { Placement="CurveDrivenStructural",Parameters=parameters,
            Loops=new List<List<HistoryCurve>> { new List<HistoryCurve> { new HistoryCurve { Native=Axis() } } } };
        var replay=ParametersForCurveDrivenFamily(recipe);
        Check(replay.Count==4 && parameters.Count==6,"Only the two duplicate end-level offsets may be filtered, without mutating old payloads.");
        for(int index=0;index<replay.Count;index++) Check(Object.ReferenceEquals(replay[index],parameters[index+2]),"Rotation/reference level/justification/custom values must retain their exact captured objects and order.");
        Check(recipe.Parameters[0].Number==2.2681413554290373 && recipe.Parameters[1].Number==2.2681413554290373,"Filtering changed historical source values.");
        foreach(var placement in new[] {"CurveBased","WorkPlaneBased","TwoLevelsBased"}) {
            recipe.Placement=placement;
            Check(Object.ReferenceEquals(ParametersForCurveDrivenFamily(recipe),parameters),"Offset policy escaped the CurveDrivenStructural family scope.");
        }
        recipe.Placement="CurveDrivenStructural";
        var savedLoops=recipe.Loops; recipe.Loops=null;
        Check(Object.ReferenceEquals(ParametersForCurveDrivenFamily(recipe),parameters),"Without a saved absolute axis, offsets must not be discarded.");
        recipe.Loops=savedLoops;

        var doc=new Document();
        var instance=new FamilyInstance { Location=new LocationCurve { Curve=Axis() } };
        VerifyCurveDrivenFamilyPlacement(doc,instance,recipe);
        Check(doc.Regenerations==1,"The structural solver must regenerate before placement validation.");
        instance.Location=new LocationCurve { Curve=Axis(reverse:true) };
        VerifyCurveDrivenFamilyPlacement(doc,instance,recipe);
        instance.Location=new LocationCurve { Curve=Axis(endpointDrift:0.00002) };
        MustReject(doc,instance,recipe,"A displaced structural endpoint must fail restoration.");
        instance.Location=new LocationCurve { Curve=Axis(middleDrift:0.00002) };
        MustReject(doc,instance,recipe,"Matching endpoints must not hide a changed curve midpoint.");
        instance.Location=new LocationCurve { Curve=Axis(endpointDrift:0.000001) };
        VerifyCurveDrivenFamilyPlacement(doc,instance,recipe);
        var unbound=Axis(); unbound.IsBound=false; instance.Location=new LocationCurve { Curve=unbound };
        MustReject(doc,instance,recipe,"Missing/unbounded structural axes must not count as restored.");
        instance.Location=null;
        MustReject(doc,instance,recipe,"Missing structural placement must fail.");
        doc.OnRegenerate=()=>instance.Location=new LocationCurve { Curve=Axis() };
        VerifyCurveDrivenFamilyPlacement(doc,instance,recipe);
        recipe.Placement="CurveBased";
        int before=doc.Regenerations;
        VerifyCurveDrivenFamilyPlacement(doc,null,recipe);
        Check(doc.Regenerations==before,"Other family placements should retain their current restore path.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures+$policies+$checks)
[HistoryCurveDrivenFamilyTests.CurveDrivenChecks]::Run()
Write-Output 'PASS: production structural-curve policy filters only duplicate end offsets, preserves old/custom/rotation parameters, and validates regenerated absolute endpoints and midpoint at 1e-5 feet without extending to other family placements.'
