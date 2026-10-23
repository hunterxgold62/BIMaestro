$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$start=$source.IndexOf('internal static HistoryRecipe CaptureDirectForChange(',[StringComparison]::Ordinal)
if($start -lt 0){throw 'Immediate change capture helper missing.'}
$opening=$source.IndexOf('{',$start);$depth=1;$end=-1
for($i=$opening+1;$i -lt $source.Length;$i++){
    if($source[$i] -eq '{'){$depth++}
    if($source[$i] -eq '}'){$depth--}
    if(!$depth){$end=$i;break}
}
if($end -lt 0){throw 'Unterminated immediate capture helper.'}
$method=$source.Substring($start,$end-$start+1)
$fixture=@'
using System;
using System.Collections.Generic;
namespace Autodesk.Revit.Exceptions { sealed class RegenerationFailedException:Exception { } }
namespace ImmediateChangeCaptureFixture {
sealed class Element {
    public int Id=1; public bool Aggregate; public Element Root;
    public string Kind="family",Comment="new value"; public double X=50;
    public bool HasNative,Unsupported; public Exception Failure;
    public bool External;
}
sealed class HistoryRecipe {
    public string Kind,Comment;public double X;public object Native;
    public bool Enriched;
}
static class ElementHistoryNativeArchive {
    public static bool RequiresAggregate(Element element)=>element.Aggregate;
    public static Element OpeningRoot(Element element)=>element.Root??element;
    // No native copy, archive, task or document-creation API is available here.
}
static class ElementHistoryExternalModels { internal static bool IsExcluded(Element element)=>element?.External==true; }
static class Reconstruction {
    public static int CoreReads,RelationReads;
    static HistoryRecipe CaptureCore(Element e,Action<string> diagnostic,bool includeDetails){
        CoreReads++;if(includeDetails)throw new Exception("Immediate capture requested geometry work");
        if(e.Failure!=null)throw e.Failure;
        return e.Unsupported?null:new HistoryRecipe{Kind=e.Kind,X=e.X,Comment=e.Comment,Native=e.HasNative?new object():null};
    }
    static HistoryRecipe Enrich(Element e,HistoryRecipe recipe,Action<string> diagnostic,bool includeBoundingBox){
        if(includeBoundingBox)throw new Exception("Immediate relations requested bounding geometry");
        RelationReads++;recipe.Enriched=true;return recipe;
    }
    public static HistoryRecipe Read(Element e,Action<string> diagnostic=null)=>CaptureDirectForChange(e,diagnostic);
'@
$checks=@'
}
public static class Checks {
    static int count;static void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
    public static string Run(){
        Check(Reconstruction.Read(null)==null,"Missing object must not be captured");
        foreach(var kind in new[]{"family","wall","network","floor","model_curve"}){
            var source=new Element{Kind=kind};var first=Reconstruction.Read(source);
            Check(first.X==50&&first.Comment=="new value"&&first.Enriched,"Current coordinates, parameters and relations must be retained");
            source.X=100;source.Comment="next change";var next=Reconstruction.Read(source);
            Check(first.X==50&&first.Comment=="new value"&&next.X==100&&next.Comment=="next change","A later edit must not mutate the former snapshot");
        }
        int reads=Reconstruction.CoreReads;
        Check(Reconstruction.Read(new Element{External=true})==null,"External link/import must never acquire an immediate object recipe");
        Check(reads==Reconstruction.CoreReads,"Excluded link/import must be rejected before direct reads");
        Check(Reconstruction.Read(new Element{Aggregate=true})==null,"Groups require a truthful native capture failure");
        Check(Reconstruction.Read(new Element{Root=new Element{Id=2}})==null,"Dependent cannot be captured as an independent root");
        Check(reads==Reconstruction.CoreReads,"Aggregates must be rejected before any geometry read");
        Check(Reconstruction.Read(new Element{Unsupported=true})==null,"Unsupported object cannot be called protected");
        Check(Reconstruction.Read(new Element{HasNative=true})==null,"A native receipt cannot escape the immediate direct path");
        var notes=new List<string>();
        Check(Reconstruction.Read(new Element{Failure=new InvalidOperationException("read failed")},notes.Add)==null&&notes.Count==1,"Read failure must be visible");
        bool propagated=false;try{Reconstruction.Read(new Element{Failure=new Autodesk.Revit.Exceptions.RegenerationFailedException()});}catch(Autodesk.Revit.Exceptions.RegenerationFailedException){propagated=true;}
        Check(propagated,"Fatal regeneration must propagate");
        return count+" immediate change capture assertions PASS (production helper, API doubles; native fidelity measured separately).";
    }
}
}
'@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n"+$checks) -IgnoreWarnings
[ImmediateChangeCaptureFixture.Checks]::Run()
