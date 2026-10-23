#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Read-Method([string]$File,[string]$Signature) {
 $text=[IO.File]::ReadAllText((Join-Path $repo $File));$start=$text.IndexOf($Signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Missing production method: $Signature"}
 $brace=$text.IndexOf('{',$start);$depth=0
 for($i=$brace;$i -lt $text.Length;$i++){
  if($text[$i] -eq '{'){$depth++}elseif($text[$i] -eq '}'){$depth--;if($depth -eq 0){return $text.Substring($start,$i-$start+1)}}
 }
 throw 'Unbalanced production method'
}
$context=(Read-Method 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.Definitions.cs' 'internal static void ValidateHistoricalContext(').Replace('internal static void','public static void')
$remap=(Read-Method 'BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs' 'private static void RemapDurableDefinitionReferences(').Replace('private static void','public static void')
$prefix=@"
using System;using System.Collections.Generic;using System.Linq;
namespace FrozenDefinitionFixture {
public class Element {public int Id;public string UniqueId,Name;}
public class Phase:Element {}
public class Document {public List<Phase> Phases=new List<Phase>();public Dictionary<string,Element> Elements=new Dictionary<string,Element>();public Element GetElement(string uid){Element e;return Elements.TryGetValue(uid,out e)?e:null;}}
public class HistoryNativeContextDefinition {public string SourceUniqueId,Class,Name;public int Ordinal;}
public class HistoryParameter {public string Reference,Definition;}
public class HistoryPlacement {public string LevelSourceUniqueId;public List<HistoryParameter> ElevationParameters;}
public class HistoryNetwork {public string SystemType;}
public class HistoryPort {public string SystemType,SystemVersion;}
public class HistoryConnection {public HistoryPort Port,PeerPort;}
public class HistoryCircuit {public HistoryRecipe Recipe;}
public class HistoryRecipe {public string Type,Level,Host;public double[] Point;public HistoryNetwork Network;public HistoryPlacement HistoricalPlacement;public List<HistoryParameter> Parameters;public List<HistoryPort> Ports;public List<HistoryConnection> Connections;public List<HistoryCircuit> ElectricalCircuits;}
public static class ProductionMethods {
$context
$remap
}
"@
$suffix=@'
public static class Tests {
 static int count;
 static void Assert(bool ok,string name){count++;if(!ok)throw new Exception(name);}
 static void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Assert(rejected,name);}
 public static int Run(){
 var doc=new Document();var phase=new Phase{Id=1,UniqueId="phase-old",Name="Existing"};doc.Phases.Add(phase);doc.Elements.Add(phase.UniqueId,phase);
 var context=new HistoryNativeContextDefinition{SourceUniqueId=phase.UniqueId,Name=phase.Name,Class=phase.GetType().FullName,Ordinal=0};
 ProductionMethods.ValidateHistoricalContext(doc,new[]{context});Assert(true,"Exact phase accepted");
 ProductionMethods.ValidateHistoricalContext(doc,null);Assert(true,"No context accepted");
 phase.Name="Changed";Reject(()=>ProductionMethods.ValidateHistoricalContext(doc,new[]{context}),"Rename refused");phase.Name="Existing";
 context.SourceUniqueId="same-name-other-uid";Reject(()=>ProductionMethods.ValidateHistoricalContext(doc,new[]{context}),"Name-only substitution refused");context.SourceUniqueId=phase.UniqueId;
 context.Ordinal=1;Reject(()=>ProductionMethods.ValidateHistoricalContext(doc,new[]{context}),"Phase order mismatch refused");context.Ordinal=0;
 context.Class="Other";Reject(()=>ProductionMethods.ValidateHistoricalContext(doc,new[]{context}),"Class mismatch refused");context.Class=phase.GetType().FullName;
 context.Ordinal=-1;Reject(()=>ProductionMethods.ValidateHistoricalContext(doc,new[]{context}),"Invalid ordinal refused");context.Ordinal=0;
 var point=new[]{1d,2d,3d};var recipe=new HistoryRecipe{Type="type-old",Level="level-old",Host="physical-host",Point=point,
 Network=new HistoryNetwork{SystemType="system-old"},Parameters=new List<HistoryParameter>{new HistoryParameter{Reference="material-old",Definition="parameter-old"},new HistoryParameter{Reference="phase-old"}},
 HistoricalPlacement=new HistoryPlacement{LevelSourceUniqueId="level-old",ElevationParameters=new List<HistoryParameter>{new HistoryParameter{Reference="level-old"}}},
 Ports=new List<HistoryPort>{new HistoryPort{SystemType="system-old",SystemVersion="revision"}},
 Connections=new List<HistoryConnection>{new HistoryConnection{Port=new HistoryPort{SystemType="system-old"},PeerPort=new HistoryPort{SystemType="system-old"}}},
 ElectricalCircuits=new List<HistoryCircuit>{new HistoryCircuit{Recipe=new HistoryRecipe{Type="circuit-old"}}}};
 var map=new Dictionary<string,string>{{"type-old","type-new"},{"level-old","level-new"},{"system-old","system-new"},{"material-old","material-new"},{"parameter-old","parameter-new"},{"circuit-old","circuit-new"}};
 ProductionMethods.RemapDurableDefinitionReferences(recipe,map);
 Assert(recipe.Type=="type-new"&&recipe.Level=="level-new","Type and level remap");
 Assert(recipe.Network.SystemType=="system-new","Network remap");
 Assert(recipe.Parameters[0].Reference=="material-new"&&recipe.Parameters[0].Definition=="parameter-new","Parameter refs remap");
 Assert(recipe.Parameters[1].Reference=="phase-old","Exact phase UID preserved");
 Assert(recipe.HistoricalPlacement.ElevationParameters[0].Reference=="level-new"&&recipe.HistoricalPlacement.LevelSourceUniqueId=="level-new","Placement level remap");
 Assert(recipe.Ports[0].SystemType=="system-new"&&recipe.Ports[0].SystemVersion=="revision","Port remap retains version");
 Assert(recipe.Connections[0].Port.SystemType=="system-new"&&recipe.Connections[0].PeerPort.SystemType=="system-new","Both peer ports remap");
 Assert(recipe.ElectricalCircuits[0].Recipe.Type=="circuit-new","Nested circuit remap");
 Assert(Object.ReferenceEquals(recipe.Point,point)&&recipe.Point.SequenceEqual(new[]{1d,2d,3d})&&recipe.Host=="physical-host","Frozen point and host preserved");
 return count;
 }
}
}
'@
Add-Type -TypeDefinition ($prefix+$suffix)
$count=[FrozenDefinitionFixture.Tests]::Run()
[pscustomobject]@{Passed=$true;Assertions=$count;Scope='Extracted production Phase context and definition remap with stub DTOs; no native Revit CopyElements validation'}|ConvertTo-Json -Compress
