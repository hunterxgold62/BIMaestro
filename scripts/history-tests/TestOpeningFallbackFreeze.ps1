$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tracker=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$recipe=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs'))
$start=$tracker.IndexOf('        private static HistoryRecipe FreezeDeletionRecipe(Document doc, HistoryRecipe recipe)')
$end=$tracker.IndexOf('        private static Dictionary<string, object> BuildDeleteDelta(', $start)
if($start -lt 0 -or $end -le $start) { throw 'Production deletion recipe freeze was not found.' }
$freeze=$tracker.Substring($start,$end-$start)
$start=$recipe.IndexOf('        internal HistoryRecipe CopyWithOpeningFallback(HistoryNativeArtifact fallback)')
$end=$recipe.IndexOf('        public string NativeFallbackReason', $start)
if($start -lt 0 -or $end -le $start) { throw 'Production immutable recipe copy was not found.' }
$copy=$recipe.Substring($start,$end-$start)
$fixtures=@'
using System;
using System.Collections.Generic;
namespace HistoryOpeningFallbackFreezeTests {
class Document { }
class ElementId { internal int Value; }
static class ElementIdExtensions { internal static ElementId CreateElementId(int id) { return new ElementId { Value=id }; } }
class HistoryNativeArtifact { internal bool WholeModel,Frozen; internal int SourceElementId; }
class HistoryRecipe {
    internal string Kind;
    internal HistoryNativeArtifact Native,OpeningFallback;
    internal List<string> Parameters;
    internal List<double[]> Geometry;
'@
$checkFixtures=@'
}
static class ElementHistoryOpeningArchive {
    internal static bool Invalid;
    internal static int Calls,LastId;
    internal static HistoryRecipe FreezeForDeletion(Document doc,HistoryRecipe recipe,ElementId id) {
        Calls++; LastId=id.Value;
        return Invalid ? null : new HistoryRecipe { Kind="native_archive", Native=new HistoryNativeArtifact {
            WholeModel=true,Frozen=true,SourceElementId=id.Value
        }};
    }
}
public static class FreezeChecks {
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
'@
$checks=@'
    public static void Run() {
        var doc=new Document();
        var originalBackup=new HistoryNativeArtifact { WholeModel=true,SourceElementId=123 };
        var primary=new HistoryNativeArtifact();
        var parameters=new List<string> { "Diameter=0.2","Offset=0.5" };
        var geometry=new List<double[]> { new[] {1.0,2.0,3.0} };
        var direct=new HistoryRecipe { Kind="family",Native=primary,OpeningFallback=originalBackup,
            Parameters=parameters,Geometry=geometry };
        var frozen=FreezeDeletionRecipe(doc,direct);
        Check(!Object.ReferenceEquals(frozen,direct),"Freezing a backup must not mutate the cached direct recipe.");
        Check(frozen.Kind=="family" && Object.ReferenceEquals(frozen.Native,primary),"Opening backup replaced the primary direct recipe.");
        Check(frozen.OpeningFallback!=null && frozen.OpeningFallback.Frozen && !Object.ReferenceEquals(frozen.OpeningFallback,originalBackup),"Eligible backup was not replaced by its frozen receipt.");
        Check(Object.ReferenceEquals(direct.OpeningFallback,originalBackup) && !originalBackup.Frozen,"Freezing changed the original live receipt.");
        Check(Object.ReferenceEquals(frozen.Parameters,parameters) && Object.ReferenceEquals(frozen.Geometry,geometry),"Deletion freeze unnecessarily copied/serialized immutable parameter/geometry payloads.");
        Check(parameters.Count==2 && parameters[0]=="Diameter=0.2" && geometry[0][2]==3,"Deletion freeze mutated shared immutable payloads.");
        Check(ElementHistoryOpeningArchive.Calls==1 && ElementHistoryOpeningArchive.LastId==123,"Backup eligibility used the wrong original element ID.");

        ElementHistoryOpeningArchive.Invalid=true;
        var dirty=FreezeDeletionRecipe(doc,direct);
        Check(dirty!=null && dirty.Kind=="family" && dirty.OpeningFallback==null,"Dirty opening state must remove only the backup, retaining direct restoration.");
        Check(Object.ReferenceEquals(direct.OpeningFallback,originalBackup) && Object.ReferenceEquals(dirty.Parameters,parameters),"Dirty-backup rejection changed cached state or historical parameters.");
        var whole=new HistoryRecipe { Kind="native_archive",Native=originalBackup };
        Check(FreezeDeletionRecipe(doc,whole)==null,"A dirty primary whole-model recipe must not remain restorable.");
        ElementHistoryOpeningArchive.Invalid=false;
        var frozenWhole=FreezeDeletionRecipe(doc,whole);
        Check(frozenWhole?.Native?.Frozen==true && Object.ReferenceEquals(whole.Native,originalBackup),"Valid primary whole-model recipe must be frozen without changing its original receipt.");

        var ordinary=new HistoryRecipe { Kind="wall",Parameters=parameters };
        int calls=ElementHistoryOpeningArchive.Calls;
        Check(Object.ReferenceEquals(FreezeDeletionRecipe(doc,ordinary),ordinary),"Ordinary direct recipes should keep their existing payload.");
        Check(FreezeDeletionRecipe(doc,null)==null && ElementHistoryOpeningArchive.Calls==calls,"Missing/ordinary recipes should not invoke opening checkpoint validation.");
    }
}}
'@
Add-Type -TypeDefinition ($fixtures+$copy+$checkFixtures+$freeze+$checks)
[HistoryOpeningFallbackFreezeTests.FreezeChecks]::Run()
Write-Output 'PASS: production deletion freeze and immutable recipe copy preserve direct recipes/shared parameters, freeze eligible backups, reject dirty backups independently and reject dirty primary whole-model restoration without JSON serialization.'
