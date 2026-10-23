#Requires -Version 7.0
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $repo ('tmp/history-availability-ui-tests/'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($root)
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryWindow.xaml.cs'))
function Method([string]$signature){
 $start=$source.IndexOf($signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Missing $signature"}
 $open=$source.IndexOf('{',$start);$depth=0
 for($i=$open;$i -lt $source.Length;$i++){
  if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}
 };throw 'Incomplete method'
}
$methods=(Method 'private static bool SelectionIsReady(')+(Method 'private void UpdateRestorationAvailability(')
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$display=Method 'internal static bool IsDisplayableHistoryEvent('
$fixture=@'
using System;using System.Linq;using System.Collections.Generic;using Newtonsoft.Json.Linq;
namespace System.Windows.Controls{internal static class ToolTipService{internal static void SetShowOnDisabled(Button b,bool v){b.ToolTipOnDisabled=v;}}}
internal class Button{internal bool IsEnabled,ToolTipOnDisabled;internal object Content,ToolTip;}
internal static class UiLanguage{internal static string T(string fr,string en)=>fr;}
internal class ElementHistoryEvent{internal string Id,Action,Category,UniqueId,Tx;internal int ElementId;internal Dictionary<string,object> Delta;}
internal static class ElementHistoryExternalModels{internal static bool IsExcludedClass(string kind)=>false;}
internal static class ElementHistoryDurableBridge{
 internal enum ReadinessState{Preparing,Ready,Unavailable}
 internal class EventReadiness{internal ReadinessState State;internal string Reason;}
 internal static readonly Dictionary<string,EventReadiness> States=new Dictionary<string,EventReadiness>();
 internal static EventReadiness GetReadiness(ElementHistoryEvent e)=>States[e.Id];
}
internal class Window{
 internal bool _restoringDeleted;
 private static List<ElementHistoryEvent> RestorableDeletionEvents(IEnumerable<ElementHistoryEvent> events)=>(events??Enumerable.Empty<ElementHistoryEvent>()).ToList();
 __METHODS__
 __DISPLAY__
 private static bool IsLowValueParameterChange(ElementHistoryEvent ev)=>false;
 private static bool IsNoisy3DViewEvent(ElementHistoryEvent ev)=>false;
 private static bool IsUsefulHistoryText(string text)=>!string.IsNullOrWhiteSpace(text);
 private static bool IsIgnoredCategoryName(string text)=>false;
 internal Button Render(params ElementHistoryEvent[] events){var b=new Button();UpdateRestorationAvailability(b,events,false);return b;}
}
internal class Checks{
 static int count;static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
 static ElementHistoryEvent E(string id,ElementHistoryDurableBridge.ReadinessState state,string reason=""){ElementHistoryDurableBridge.States[id]=new ElementHistoryDurableBridge.EventReadiness{State=state,Reason=reason};return new ElementHistoryEvent{Id=id};}
 static int Main(){try{
 var w=new Window();var ready=E("r",ElementHistoryDurableBridge.ReadinessState.Ready);var pending=E("p",ElementHistoryDurableBridge.ReadinessState.Preparing);var unavailable=E("u",ElementHistoryDurableBridge.ReadinessState.Unavailable,"Intégrité invalide");
 var b=w.Render(ready,pending,unavailable);Check(b.IsEnabled,"Unavailable or pending companions must not block a ready object");Check(b.Content.ToString().Contains("1 élément(s) prêts"),"Partial action must disclose exact ready count");Check(b.ToolTip.ToString().Contains("Seuls les éléments prêts")&&b.ToolTip.ToString().Contains("Intégrité invalide"),"Partial scope and reason must be visible");Check(b.ToolTip.ToString().Contains("1 en préparation")&&b.ToolTip.ToString().Contains("1 indisponible"),"Mixed selection must report pending and unavailable counts together");
 b=w.Render(unavailable);Check(!b.IsEnabled&&b.Content.ToString()=="Restauration indisponible","Permanent failure must not be displayed as pending");Check(b.ToolTipOnDisabled&&b.ToolTip.ToString().Contains("Intégrité invalide"),"Disabled button must expose failure");
 b=w.Render(pending);Check(!b.IsEnabled&&b.Content.ToString()=="Restauration en attente","Pending-only selection remains disabled");
 ElementHistoryDurableBridge.States[pending.Id].State=ElementHistoryDurableBridge.ReadinessState.Ready;b=w.Render(pending);Check(b.IsEnabled,"A later refresh must enable a completed preparation");
 b=w.Render();Check(!b.IsEnabled,"Empty selection must stay disabled");w._restoringDeleted=true;b=w.Render(ready);Check(!b.IsEnabled&&b.Content.ToString()=="Restauration…","An ongoing restore must prevent double execution");
 var cleanup=new ElementHistoryEvent{Action="delete",Category="Non capturé",UniqueId="deleted:123",Tx="BIMaestro - Restaurer élément supprimé"};
 Check(!Window.IsDisplayableHistoryEvent(cleanup),"Technical restore cleanup must not create NC cards");
 cleanup.Tx="BIMaestro - Restaurer éléments supprimés";Check(!Window.IsDisplayableHistoryEvent(cleanup),"Group restoration cleanup must also be hidden");
 cleanup.Tx="Supprime la sélection";Check(Window.IsDisplayableHistoryEvent(cleanup),"A genuinely uncaptured user deletion must stay visible");
 cleanup.Tx="BIMaestro - Restaurer élément supprimé";cleanup.Category="Murs";Check(Window.IsDisplayableHistoryEvent(cleanup),"Known physical restoration changes must retain their history");
 cleanup.Category="Non capturé";cleanup.UniqueId="real-uid";Check(Window.IsDisplayableHistoryEvent(cleanup),"Identified objects must not be hidden as technical placeholders");
 Console.WriteLine(count+" production UI availability assertions PASS");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
'@
$fixture=$fixture.Replace('__METHODS__',$methods).Replace('__DISPLAY__',$display)
[IO.File]::WriteAllText((Join-Path $root 'Checks.cs'),$fixture)
$json=Join-Path $repo 'BIMaestro/bin/Revit2025/Release/Newtonsoft.Json.dll'
$project=Join-Path $root 'Availability.Tests.csproj'
[IO.File]::WriteAllText($project, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>'+[Security.SecurityElement]::Escape($json)+'</HintPath></Reference></ItemGroup></Project>')
& dotnet build $project --nologo -v:q -p:UseSharedCompilation=false *> (Join-Path $root 'build.log')
if($LASTEXITCODE -ne 0){throw 'UI fixture compile failed'}
& dotnet (Join-Path $root 'bin/Debug/net9.0/Availability.Tests.dll')
if($LASTEXITCODE -ne 0){throw 'UI fixture failed'}
Write-Output $root
