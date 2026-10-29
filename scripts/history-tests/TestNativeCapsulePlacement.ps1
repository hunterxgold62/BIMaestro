#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('tmp/history-capsule-placement-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
function Slice-Source([string]$text,[string]$first,[string]$next) {
 $a=$text.IndexOf($first,[StringComparison]::Ordinal);$b=if($a -ge 0){$text.IndexOf($next,$a,[StringComparison]::Ordinal)}else{-1}
 if($a -lt 0 -or $b -le $a){throw ('Missing production block: '+$first)}
 return $text.Substring($a,$b-$a)
}
$base=Join-Path $repo 'BIMaestro/commands/qui est le coupable'
$batch=[IO.File]::ReadAllText((Join-Path $base 'ElementHistoryNativeArchive.BatchCopy.cs'))
$reconstruction=[IO.File]::ReadAllText((Join-Path $base 'ElementHistoryReconstruction.cs'))
$extended=[IO.File]::ReadAllText((Join-Path $base 'ElementHistoryReconstruction.Extended.cs'))
$helper=Slice-Source $batch '        internal static bool VerifyNativeCapsulePlacement' '        // Test-only, read-only snapshots'
$placement=Slice-Source $reconstruction '    internal sealed class HistoryPlacement' '    internal sealed class HistoryCircuit'
$read=Slice-Source $extended '        private static HistoryPlacement ReadHistoricalPlacement' '        private static HistoryPlacement SavedHistoricalPlacement'
$verify=Slice-Source $extended '        internal static bool VerifyHistoricalPlacement' '        // Caller must restrict this helper'
$production='using System;using System.Linq;using System.Collections.Generic;using Autodesk.Revit.DB; namespace Analyse {'+$placement+' internal static partial class ElementHistoryNativeArchive {'+$helper+'} internal static partial class ElementHistoryReconstruction {'+$read+$verify+'} }'
[IO.File]::WriteAllText((Join-Path $output 'Production.cs'),$production)
$fixture=@'
using System;using System.Linq;using System.Collections.Generic;using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB.Structure { public enum StructuralType { Column, Beam } }
namespace Autodesk.Revit.DB {
 public enum StorageType { Double, Integer, String, ElementId }
 public enum BuiltInParameter { STRUCTURAL_BEND_DIR_ANGLE, SLANTED_COLUMN_TYPE_PARAM }
 public sealed class Parameter { public StorageType StorageType; public bool HasValue=true;public double Number;public int Integer;public double AsDouble()=>Number;public int AsInteger()=>Integer; }
 public sealed class XYZ { public double X,Y,Z;public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} }
 public sealed class Transform { public XYZ Origin=new XYZ(0,0,0),BasisX=new XYZ(1,0,0),BasisY=new XYZ(0,1,0),BasisZ=new XYZ(0,0,1); }
 public sealed class BoundingBoxXYZ { public XYZ Min,Max; }
 public abstract class Location { }
 public sealed class LocationPoint:Location { public XYZ Point; }
 public sealed class Curve { internal Analyse.HistoryCurve Data; }
 public sealed class LocationCurve:Location { public Curve Curve; }
 public class Document { }
 public class Element { public string UniqueId="source",LevelUID; public bool IsValidObject=true;public Location Location;public Document Document=new Document(); public BoundingBoxXYZ get_BoundingBox(object view)=>null; }
 public class Level:Element { }
 public class Group:Element { }
 public sealed class FamilyInstance:Element { public Structure.StructuralType StructuralType=Structure.StructuralType.Column;public Element Host;public bool Attached,HandFlipped,FacingFlipped,Mirrored;public Transform Frame=new Transform();public readonly Dictionary<BuiltInParameter,Parameter> Parameters=new Dictionary<BuiltInParameter,Parameter>();public Transform GetTransform()=>Frame;public Parameter get_Parameter(BuiltInParameter p)=>Parameters.TryGetValue(p,out var value)?value:null; }
 public sealed class ColumnAttachment { public static bool IsValidColumn(FamilyInstance e)=>e.StructuralType==Structure.StructuralType.Column;public static ColumnAttachment GetColumnAttachment(FamilyInstance e,int end)=>e.Attached&&end==1?new ColumnAttachment():null; }
}
namespace Analyse {
 internal sealed class HistoryCurve { public double[] Start,End,Mid; }
 internal sealed class HistoryParameter { }
 internal sealed class HistoryGeometryRelation { public string Kind,First,Second; }
 internal sealed class HistoryRecipe { public HistoryPlacement HistoricalPlacement;public List<HistoryGeometryRelation> GeometryRelations; }
 internal static partial class ElementHistoryReconstruction {
  private static HistoryPlacement SavedHistoricalPlacement(HistoryRecipe recipe)=>recipe?.HistoricalPlacement;
  private static Level FindLevel(Element e)=>e.LevelUID==null?null:new Level{UniqueId=e.LevelUID};
  private static bool NativeReferenceMatches(Document d,string uid,Element actual)=>uid==actual?.UniqueId;
  private static double[] Pack(XYZ p)=>new[]{p.X,p.Y,p.Z};
  private static HistoryCurve CaptureCurve(Curve c)=>c.Data;
 }
 internal static class Checks {
  private static int count;
  private static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  private static FamilyInstance Column(string uid)=>new FamilyInstance{UniqueId=uid,LevelUID="level-a",Attached=true,Location=new LocationCurve{Curve=new Curve{Data=new HistoryCurve{Start=new[]{0d,0d,0d},End=new[]{1d,0d,8d}}}}};
  private static Parameter Angle(double value)=>new Parameter{StorageType=StorageType.Double,Number=value};
  private static Parameter Style(int value)=>new Parameter{StorageType=StorageType.Integer,Integer=value};
  private static Tuple<FamilyInstance,FamilyInstance,HistoryRecipe> New(bool contextual=true) {
   var source=Column("source");var copy=Column("copy");
   source.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE]=Angle(4.712388980384685);copy.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE]=Angle(4.712388980384685);
   source.Parameters[BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM]=Style(2);copy.Parameters[BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM]=Style(2);
   if(contextual)copy.Frame.BasisZ=new XYZ(0,0.00006,1);
   var recipe=new HistoryRecipe{HistoricalPlacement=new HistoryPlacement{LevelSourceUniqueId="level-a",Origin=new[]{0d,0d,0d},BasisX=new[]{1d,0d,0d},BasisY=new[]{0d,1d,0d},BasisZ=new[]{0d,0d,1d},Curve=((LocationCurve)source.Location).Curve.Data,Mirrored=false,HandFlipped=false,FacingFlipped=false}};
   return Tuple.Create(source,copy,recipe);
  }
  private static bool Verify(Tuple<FamilyInstance,FamilyInstance,HistoryRecipe> t,out string failure)=>ElementHistoryNativeArchive.VerifyNativeCapsulePlacement(t.Item1,t.Item2,t.Item3,out failure);
  private static void Reject(Action<Tuple<FamilyInstance,FamilyInstance,HistoryRecipe>> mutation,string label) {var t=New();mutation(t);Check(!Verify(t,out _),label);}
  static int Main(){try {
   var regular=New(false);Check(Verify(regular,out var failure)&&failure==null,"Exact original placement must pass.");
   var attached=New();Check(Verify(attached,out failure)&&failure==null,"Attached column with matching axis/roll must allow contextual frame only.");
   var saved=attached.Item3.HistoricalPlacement;var x=saved.BasisX;var y=saved.BasisY;var z=saved.BasisZ;
   Check(ReferenceEquals(x,attached.Item3.HistoricalPlacement.BasisX)&&ReferenceEquals(y,attached.Item3.HistoricalPlacement.BasisY)&&ReferenceEquals(z,attached.Item3.HistoricalPlacement.BasisZ),"Historical axes arrays must stay assigned.");
   Check(saved.BasisZ.SequenceEqual(new[]{0d,0d,1d})&&attached.Item2.Frame.BasisZ.Y==0.00006,"Recipe and copied frame must stay unchanged.");
   Reject(t=>t.Item1.Attached=false,"Unattached column must not bypass frame validation.");
   Reject(t=>{t.Item1.Attached=false;t.Item3.GeometryRelations=new List<HistoryGeometryRelation>{new HistoryGeometryRelation{Kind="column_attach",First="other",Second="target"}};},"Unrelated attachment evidence must not qualify.");
   var proof=New();proof.Item1.Attached=false;proof.Item3.GeometryRelations=new List<HistoryGeometryRelation>{new HistoryGeometryRelation{Kind="column_attach",First="source",Second="target"}};Check(Verify(proof,out _),"An exact attachment record qualifies.");
   Reject(t=>((LocationCurve)t.Item2.Location).Curve.Data=new HistoryCurve{Start=new[]{0d,0d,0d},End=new[]{1d,0d,8.01}},"A real axis-curve change must fail.");
   Reject(t=>t.Item2.Frame.Origin=new XYZ(.01,0,0),"A real origin change must fail.");
   Reject(t=>t.Item2.Mirrored=true,"A real mirror change must fail.");
   Reject(t=>t.Item2.HandFlipped=true,"A real hand flip must fail.");
   Reject(t=>t.Item2.FacingFlipped=true,"A real facing flip must fail.");
   Reject(t=>t.Item2.LevelUID="level-b","A level change must fail.");
   var roll=New();roll.Item2.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number+=.01;Check(!Verify(roll,out failure)&&failure.Contains("section"),"A real section roll change must fail with a useful message.");
   Reject(t=>t.Item2.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number=double.NaN,"NaN copied roll must fail.");
   Reject(t=>t.Item1.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number=double.PositiveInfinity,"Infinite source roll must fail.");
   Reject(t=>t.Item2.Parameters.Remove(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE),"Missing copied angle must fail.");
   Reject(t=>t.Item1.Parameters.Remove(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE),"Missing source angle must fail.");
   Reject(t=>t.Item2.Parameters[BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM].Integer=1,"Changed slanted style must fail.");
   Reject(t=>t.Item2.Parameters.Remove(BuiltInParameter.SLANTED_COLUMN_TYPE_PARAM),"Missing slanted style must fail.");
   Reject(t=>t.Item1.StructuralType=Autodesk.Revit.DB.Structure.StructuralType.Beam,"An ordinary beam must not qualify.");
   Reject(t=>t.Item2.Location=new LocationPoint{Point=new XYZ(0,0,0)},"A point-based copied family must not qualify.");
   var wrapped=New();wrapped.Item2.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number+=2*Math.PI;Check(Verify(wrapped,out _),"A full section turn is equivalent.");
   var near=New();near.Item2.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number+=5e-8;Check(Verify(near,out _),"Section angle tolerance must accept subthreshold numeric error.");
   Reject(t=>t.Item2.Parameters[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE].Number+=2e-7,"Section angle tolerance must reject genuine change beyond its bound.");
   Check(saved.BasisX==x&&saved.BasisY==y&&saved.BasisZ==z&&saved.BasisZ[1]==0,"Historical DTO axes remain intact after every branch.");
   Console.WriteLine("PASS: "+count+" production capsule placement checks.");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 }
}
'@
[IO.File]::WriteAllText((Join-Path $output 'Fixtures.cs'),$fixture)
$project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="Production.cs"/><Compile Include="Fixtures.cs"/></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $output 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
$projectPath=Join-Path $output 'CapsulePlacement.Tests.csproj'
[IO.File]::WriteAllText($projectPath,$project)
& dotnet build $projectPath --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $output 'build.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'build.log');throw 'Capsule placement fixture compilation failed.'}
& dotnet (Join-Path $output 'bin/Debug/net8.0/CapsulePlacement.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'Capsule placement fixture failed.'}
Write-Output $output
