$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$start=$source.IndexOf('        internal static void ApplyParameters(Document doc, Element element, List<HistoryParameter> values)')
$end=$source.IndexOf('        private static void Collect(', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production parameter restoration method was not found.' }
$method=$source.Substring($start,$end-$start)
$fixtures=@'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace Autodesk.Revit.Exceptions { public class RegenerationFailedException : Exception { } }
namespace HistoryParameterRestorationTests {
enum StorageType { None,Double,Integer,String,ElementId }
enum BuiltInParameter { RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM=-1, WALL_HEIGHT_TYPE=-1001103, WALL_TOP_OFFSET=-1001109, TestDimension=-200 }
class ElementId { internal long Value; internal long GetIdLongValue() { return Value; } }
static class ElementIdExtensions { internal static ElementId CreateElementId(int id) { return new ElementId { Value=id }; } }
class Definition { internal string Name="Angle de section"; }
class ParameterElement : Element { internal Definition GetDefinition() { return new Definition(); } }
class Document { internal Element GetElement(string uid) { return null; } internal void Regenerate() { } }
class Parameter {
    internal ElementId Id=new ElementId { Value=-200 };
    internal Definition Definition=new Definition();
    internal bool IsReadOnly,Accept;
    internal StorageType StorageType=StorageType.Double;
    internal double Number;
    internal string Text;
    internal ElementId Reference;
    internal int SetCalls;
    internal Exception Error;
    internal double AsDouble() { return Number; }
    internal int AsInteger() { return (int)Number; }
    internal string AsString() { return Text; }
    internal ElementId AsElementId() { return Reference; }
    void Attempt() { SetCalls++; if(Error!=null) throw Error; }
    internal bool Set(double number) { Attempt(); if(Accept) Number=number; return Accept; }
    internal bool Set(int number) { return Set((double)number); }
    internal bool Set(string text) { Attempt(); if(Accept) Text=text; return Accept; }
    internal bool Set(ElementId id) { Attempt(); if(Accept) Reference=id; return Accept; }
}
class Element {
    internal string Name="IPN200";
    internal ElementId Id=new ElementId { Value=123 };
    internal Parameter Parameter;
    internal Parameter get_Parameter(Guid id) { return Parameter; }
    internal Parameter get_Parameter(BuiltInParameter id) { return Parameter; }
    internal Parameter get_Parameter(Definition definition) { return Parameter; }
    internal IEnumerable<Parameter> GetParameters(string name) { return Parameter==null ? new Parameter[0] : new[] {Parameter}; }
}
class HistoryParameter {
    internal int BuiltIn=-200;
    internal string Shared=null,Definition=null,Name=null,Text=null,Reference=null;
    internal int Storage=(int)StorageType.Double;
    internal double Number;
}
public static class ParameterChecks {
'@
$checks=@'
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static void Run() {
        var doc=new Document();
        var parameter=new Parameter { Number=2,Accept=false };
        var element=new Element { Parameter=parameter };
        var historical=new HistoryParameter { Number=2.000000002 };
        var values=new List<HistoryParameter> { historical };
        string detail=null;
        var oldCulture=CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            try { ApplyParameters(doc,element,values); } catch(InvalidOperationException ex) { detail=ex.Message; }
        } finally { CultureInfo.CurrentCulture=oldCulture; }
        Check(detail!=null && parameter.SetCalls==1,"Rejected historical dimension must still fail at the original strict threshold.");
        foreach(var expected in new[] {"Historical dimension could not be restored.","parameter=Angle de section","id=-200","builtIn=TestDimension","storage=Double","historical=2.000000002","actual=2","element=Element / IPN200","elementId=123"})
            Check(detail.Contains(expected),"Missing parameter diagnostic: "+expected);

        parameter.Accept=true; ApplyParameters(doc,element,values);
        Check(parameter.Number==historical.Number,"Writable historical dimensions no longer restore.");
        int calls=parameter.SetCalls; ApplyParameters(doc,element,values);
        Check(parameter.SetCalls==calls,"Equal dimensions should avoid unnecessary API writes.");
        parameter.IsReadOnly=true; historical.Number=3; ApplyParameters(doc,element,values);
        Check(parameter.SetCalls==calls,"Read-only solver values must retain their existing policy.");
        parameter.IsReadOnly=false;
        var original=new ArgumentException("Rejected by Revit solver"); parameter.Error=original;
        bool preserved=false;
        try { ApplyParameters(doc,element,values); } catch(InvalidOperationException ex) {
            preserved=Object.ReferenceEquals(ex.InnerException,original) && ex.Message.Contains("historical=3") && ex.Message.Contains("actual=2.000000002");
        }
        Check(preserved,"API setter exceptions must preserve their cause and exact values.");
        var fatal=new Autodesk.Revit.Exceptions.RegenerationFailedException(); parameter.Error=fatal;
        preserved=false;
        try { ApplyParameters(doc,element,values); } catch(Autodesk.Revit.Exceptions.RegenerationFailedException ex) { preserved=Object.ReferenceEquals(ex,fatal); }
        Check(preserved,"Fatal Revit regeneration exceptions must propagate unchanged.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures+$method+$checks)
[HistoryParameterRestorationTests.ParameterChecks]::Run()
Write-Output 'PASS: strict parameter restoration, exact invariant diagnostic values/name/ID, successful/equal/read-only paths, preserved API cause and fatal regeneration propagation.'
