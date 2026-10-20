$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$start = $source.IndexOf('        internal static bool RequiresDetailedCapture(')
$end = $source.IndexOf('        internal static HistoryRecipe Capture(', $start)
$policy = $source.Substring($start, $end - $start)
# Exercise the production classification with API-shaped metadata doubles, without Revit geometry.
$doubles = @'
using System;
namespace CaptureModeTests {
class ElementId {
    internal static readonly ElementId InvalidElementId = new ElementId();
    internal long Value; internal long GetIdLongValue() => Value;
}
enum BuiltInCategory { OST_MechanicalEquipment = -2001140, OST_Planting = -2001360 }
class Category { internal ElementId Id = new ElementId(); }
class Element {
    internal ElementId GroupId = ElementId.InvalidElementId;
    internal Category Category = new Category();
    internal bool Slow;
}
class Wall : Element {} class Floor : Element {} class Pipe : Element {}
class FamilyInstance : Element {} class Group : Element {} class Stairs : Element {}
class Railing : Element {} class Toposolid : Element {} class ImportInstance : Element {}
class DirectShape : Element {} class ElectricalSystem : Element {}
static class HistoryCaptureCosts { internal static bool KnownSlowBasic(Element e) => e.Slow; }
'@
$checks = @'
    public static void Run() {
        foreach(var e in new Element[]{new Wall(),new Floor(),new Pipe(),new FamilyInstance()})
            if(RequiresDetailedCapture(e)) throw new Exception("Ordinary direct recipes must remain light.");
        foreach(var e in new Element[]{new Group(),new Stairs(),new Railing(),new Toposolid(),
            new ImportInstance(),new DirectShape(),new ElectricalSystem()})
            if(!RequiresDetailedCapture(e)) throw new Exception("Complex class must be deferred: " + e.GetType().Name);
        var hvac = new FamilyInstance(); hvac.Category.Id.Value=(long)BuiltInCategory.OST_MechanicalEquipment;
        if(!RequiresDetailedCapture(hvac)) throw new Exception("Measured HVAC outliers must be deferred.");
        var groupedWall = new Wall(); groupedWall.GroupId=new ElementId();
        if(!RequiresDetailedCapture(groupedWall)) throw new Exception("Grouped wall needs aggregate protection.");
        if(!RequiresDetailedCapture(new Wall { Slow=true })) throw new Exception("Measured slow type must be deferred.");
        if(RequiresDetailedCapture(null)) throw new Exception("Missing element is not a capture candidate.");
    }
'@
Add-Type -TypeDefinition ($doubles + 'public static class CaptureModeChecks {' + $policy + $checks + '}}')
[CaptureModeTests.CaptureModeChecks]::Run()
Write-Output 'PASS: direct recipes stay light; complex classes, HVAC, grouped elements and measured slow types are deferred.'
