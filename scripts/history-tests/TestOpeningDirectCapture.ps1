$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$start=$source.IndexOf('internal static HistoryRecipe CaptureDirectForRestore(',[StringComparison]::Ordinal)
if($start -lt 0){throw 'Production direct checkpoint capture helper missing.'}
$opening=$source.IndexOf('{',$start);$depth=1;$end=-1
for($i=$opening+1;$i -lt $source.Length;$i++){
    if($source[$i] -eq '{'){$depth++}
    if($source[$i] -eq '}'){$depth--}
    if(!$depth){$end=$i;break}
}
if($end -lt 0){throw 'Unterminated production helper.'}
$method=$source.Substring($start,$end-$start+1)
$fixture=@'
using System;
using System.Collections.Generic;
namespace Autodesk.Revit.Exceptions {
    sealed class RegenerationFailedException:Exception { }
}
namespace OpeningDirectCaptureFixture {
sealed class Element {
    public int Id;
    public bool Aggregate;
    public Element Root;
    public HistoryRecipe Direct;
    public Exception CoreFailure, EnrichFailure;
}
sealed class HistoryRecipe {
    public string Kind;
    public object Native;
    public bool Enriched;
}
static class ElementHistoryNativeArchive {
    public static bool RequiresAggregate(Element element){return element.Aggregate;}
    public static Element OpeningRoot(Element element){return element.Root??element;}
    // No native capture/queue implementation is supplied: the helper may only read.
}
static class Reconstruction {
    public static int CoreReads,EnrichReads;
    static HistoryRecipe CaptureCore(Element element,Action<string> diagnostic,bool includeDetails){
        CoreReads++;
        if(!includeDetails)throw new Exception("Explicit restore needs complete direct data");
        if(element.CoreFailure!=null)throw element.CoreFailure;
        return element.Direct;
    }
    static HistoryRecipe Enrich(Element element,HistoryRecipe recipe,Action<string> diagnostic,bool coreHasDetails){
        EnrichReads++;
        if(!coreHasDetails)throw new Exception("Enrich must never recapture through native fallback");
        if(element.EnrichFailure!=null)throw element.EnrichFailure;
        recipe.Enriched=true;
        return recipe;
    }
    public static HistoryRecipe Read(Element element,Action<string> diagnostic=null){return CaptureDirectForRestore(element,diagnostic);}
'@
$checks=@'
}
public static class Checks {
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static Element Supported(string kind){return new Element{Id=1,Direct=new HistoryRecipe{Kind=kind}};}
    public static string Run(){
        Check(Reconstruction.Read(null)==null,"Absent source must have no recipe");
        Check(Reconstruction.CoreReads==0&&Reconstruction.EnrichReads==0,"Absent source must not be scanned");
        foreach(var kind in new[]{"family","network","wall","floor","model_curve"}){
            var source=Supported(kind);var recipe=Reconstruction.Read(source);
            Check(Object.ReferenceEquals(recipe,source.Direct)&&recipe.Enriched,"Supported direct data and enrichment must be retained: "+kind);
            Check(source.Id==1&&source.Root==null&&source.Direct.Native==null,"The source and its native path must remain untouched");
        }
        int coreReads=Reconstruction.CoreReads,enrichReads=Reconstruction.EnrichReads;
        var aggregate=Supported("family");aggregate.Aggregate=true;
        Check(Reconstruction.Read(aggregate)==null,"An aggregate member must retain its native owner path");
        foreach(var owner in new[]{"group","railing","stairs","host"}){
            var dependent=Supported("family");dependent.Root=new Element{Id=2};
            Check(Reconstruction.Read(dependent)==null,"A dependent with another opening root must not be reconstructed independently: "+owner);
        }
        Check(Reconstruction.CoreReads==coreReads&&Reconstruction.EnrichReads==enrichReads,"Rejected aggregates must not enter direct capture or enrichment");
        Check(Reconstruction.Read(new Element{Id=3})==null,"Unsupported geometry must keep native fallback available");
        Check(Reconstruction.EnrichReads==enrichReads,"Unsupported geometry must not re-enter enrichment");
        var notes=new List<string>();var failed=Supported("network");
        failed.CoreFailure=new InvalidOperationException("Historical system type unavailable");
        Check(Reconstruction.Read(failed,notes.Add)==null&&notes.Count==1&&notes[0].Contains("Historical system type unavailable"),"An unusable direct system recipe must return its diagnostic without a native queue");
        failed=Supported("family");failed.EnrichFailure=new InvalidOperationException("Read unavailable");
        Check(Reconstruction.Read(failed,notes.Add)==null,"An incomplete enrichment must not escape as a complete direct recipe");
        failed=Supported("native_archive");failed.Direct.Native=new object();
        Check(Reconstruction.Read(failed)==null,"A native receipt must never be returned from the direct-only helper");
        failed=Supported("family");failed.EnrichFailure=new Autodesk.Revit.Exceptions.RegenerationFailedException();
        bool catastrophic=false;
        try{Reconstruction.Read(failed);}catch(Autodesk.Revit.Exceptions.RegenerationFailedException){catastrophic=true;}
        Check(catastrophic,"A failed Revit regeneration from enrichment must propagate rather than permit another restore attempt");
        return "PASS: extracted production direct-capture routing, complete enrichment, aggregate/root exclusions, unsupported and failed reads, native-receipt rejection, fatal enrichment propagation. Revit capture fidelity and performance require the real harness.";
    }
}
}
'@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n"+$checks) -IgnoreWarnings
[OpeningDirectCaptureFixture.Checks]::Run()
