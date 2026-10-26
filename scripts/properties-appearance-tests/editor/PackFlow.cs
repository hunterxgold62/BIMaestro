using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using BIMaestro.PropertiesAppearance;
using Couleur;
using Newtonsoft.Json.Linq;

// Browser rendering is outside these tests; pack validation uses these boundary models.
namespace Couleur {
 public sealed class RibbonPanelColorScheme { }
 public sealed class ProjectBrowserColorSettings { public List<object> CategoryColorRules {get;set;}=new List<object>(); }
 public sealed class BrowserIconSettings { public List<BrowserIconRule> Rules {get;set;}=new List<BrowserIconRule>();public List<BrowserIconAsset> CustomAssets {get;set;}=new List<BrowserIconAsset>(); }
 public sealed class BrowserIconRule { public string IconId {get;set;} }
 public sealed class BrowserIconAsset { public string Id {get;set;}public string Data {get;set;} }
 public static class ProjectBrowserIcons { public static IEnumerable<BrowserIconAsset> Assets(BrowserIconSettings settings)=>settings.CustomAssets;public static string EncodeImage(Stream stream)=>throw new NotSupportedException("Image decoding is covered separately"); }
}
internal static class PackFlow {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static void Reject(JObject document,string message){try{AppearancePackFile.Deserialize(document.ToString());}catch(Exception ex)when(ex is InvalidDataException||ex is Newtonsoft.Json.JsonException){Check(true,message);return;}throw new Exception(message);}
 static object Call(object editor,string name)=>editor.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);
 internal static void Run(AppearanceContext context) {
  var editor=new PropertiesAppearanceEditor(context);
  var snapshot=editor.CapturePreferences();
  var profile=snapshot.Profiles.First(p=>p.Key==context.CategoryKey);
  profile.Layout.CustomGroups=new List<AppearanceCustomGroup>{new AppearanceCustomGroup{Key="custom:backup",Name="Mes paramètres",Members=new List<string>{"detail"}}};
  profile.Layout.Parameters["bimaestro:custom-groups:v1"]=new List<string>{new string('A',64)};
  profile.Layout.Groups.Insert(0,"custom:backup");
  profile.Rules.Add(new AppearanceRule{Kind="group",TargetKey="custom:backup",Light=new PaletteColors{Text="#102030",Background="#E0F0E0"},Dark=new PaletteColors{Text="#FFFFFF",Background="#203040"}});
  var emptyOverride=new AppearanceProfile{Key="cat:empty",Layout=new AppearanceLayout{CustomGroups=new List<AppearanceCustomGroup>()}};snapshot.Profiles.Add(emptyOverride);
  var pack=new AppearancePack{IncludesRibbon=false,IncludesBrowser=false,IncludesProperties=true,IncludesHome=true,Properties=snapshot,Home=new BIMaestro.Home.HomeAppearanceSettings{Style="2025",IncludeHistory=true}};
  var json=AppearancePackFile.Serialize(pack);
  Check(!json.Contains("bimaestro:custom-groups:v1")&&json.Contains("Mes paramètres"),"pack embeds custom groups instead of local sidecar references");
  var loaded=AppearancePackFile.Deserialize(json);
  var exportScope=new AppearancePackScopeDialog();
  var choices=HomeFlow.Children<CheckBox>(exportScope).ToArray();
  Check(choices.Length==4&&choices.All(c=>c.IsChecked==true),"export includes all four personalization areas by default");
  foreach(var choice in choices)choice.IsChecked=false;
  Check(!HomeFlow.Children<Button>(exportScope).Single(b=>b.IsDefault).IsEnabled,"export requires at least one selected area");
  choices[2].IsChecked=true;Check(exportScope.IncludesProperties&&!exportScope.IncludesRibbon&&!exportScope.IncludesBrowser&&!exportScope.IncludesHome,"properties can be backed up on their own");
  var importScope=new AppearancePackScopeDialog(loaded);choices=HomeFlow.Children<CheckBox>(importScope).ToArray();
  Check(!choices[0].IsEnabled&&!choices[1].IsEnabled&&choices[2].IsEnabled&&choices[3].IsEnabled,"restore only offers areas present in the pack");
  var scopeContent=(System.Windows.FrameworkElement)importScope.Content;importScope.Content=null;HomeFlow.Render(scopeContent,"choix-restauration",500,360);importScope.Close();exportScope.Close();
  var restored=loaded.Properties.Profiles.Single(p=>p.Key==context.CategoryKey);
  Check(restored.Layout.CustomGroups.Single().Members.SequenceEqual(new[]{"detail"})&&restored.Layout.Groups[0]=="custom:backup","pack preserves custom group membership and order");
  Check(restored.Rules.Last().Dark.Background=="#203040"&&restored.Rules.Last().Light.Text=="#102030","pack preserves light and dark property colors");
  Check(loaded.Properties.Profiles.Single(p=>p.Key=="cat:empty").Layout.CustomGroups.Count==0,"pack preserves an explicit empty custom-group override");
  Check(loaded.Home.Style=="2025"&&loaded.Home.IncludeHistory,"pack preserves home presentation and history");
  var malformed=JObject.Parse(json);malformed["Properties"]["Profiles"][0]["Rules"]=null;Reject(malformed,"incomplete property profiles are rejected before import");
  malformed=JObject.Parse(json);malformed["Home"]["Style"]="unknown";Reject(malformed,"unknown home styles are rejected");
  malformed=JObject.Parse(json);var group=(JObject)malformed["Properties"]["Profiles"].Single(p=>(string)p["Key"]==context.CategoryKey)["Layout"]["CustomGroups"][0];group["Members"]=new JArray("detail","detail");Reject(malformed,"duplicate custom-group memberships are rejected");
  var legacy="{\"Format\":\"BIMaestro.Appearance\",\"Version\":1,\"Ribbon\":{},\"Browser\":{\"CategoryColorRules\":[]},\"Icons\":{\"Rules\":[],\"CustomAssets\":[]}}";
  var old=AppearancePackFile.Deserialize(legacy);Check(old.IncludesRibbon&&old.IncludesBrowser&&!old.IncludesProperties&&!old.IncludesHome,"old packs do not overwrite properties or home");
  var homeOnly=new AppearancePack{IncludesRibbon=false,IncludesBrowser=false,IncludesHome=true,Home=pack.Home};Check(AppearancePackFile.Deserialize(AppearancePackFile.Serialize(homeOnly)).IncludesHome,"home-only backups are supported");
  var propertyOnly=new AppearancePack{IncludesRibbon=false,IncludesBrowser=false,IncludesProperties=true,Properties=loaded.Properties};
  var path=Path.Combine(Path.GetDirectoryName(AppearanceStore.FilePath),"properties-backup.bimaestro-style.json");
  var before=File.ReadAllText(AppearanceStore.FilePath);
  editor.LoadPreferences(loaded.Properties);
  editor.ShowResetConfirmation();HomeFlow.Render(editor,"confirmation-sauvegarde-proprietes",1168,860);
  Check(File.ReadAllText(AppearanceStore.FilePath)==before&&editor.HasChanges,"import stages property restoration without changing saved settings");
  editor.SaveBackupRequested=()=>false;Call(editor,"BackupAndReset");Check(File.ReadAllText(AppearanceStore.FilePath)==before&&editor.HasChanges,"cancelling backup prevents reset and retains drafts");
  editor.SaveBackupRequested=()=>throw new IOException("disk unavailable");Call(editor,"BackupAndReset");Check(File.ReadAllText(AppearanceStore.FilePath)==before&&editor.HasChanges,"backup failure prevents reset and retains drafts");
  editor.SaveBackupRequested=()=>{AppearancePackFile.Export(path,new AppearancePack{IncludesRibbon=false,IncludesBrowser=false,IncludesProperties=true,Properties=editor.CapturePreferences()});return true;};
  Call(editor,"BackupAndReset");Check(AppearanceStore.Load().Profiles.Count==0&&AppearancePackFile.Import(path).Properties.Profiles.Any(p=>p.Key==context.CategoryKey),"reset only proceeds after a restorable draft backup is written");
  var oldSnapshots=AppearanceStore.FilePath+".groups";
  if(Directory.Exists(oldSnapshots)){foreach(var file in Directory.GetFiles(oldSnapshots,"*.json"))File.Delete(file);Directory.Delete(oldSnapshots);}
  editor.LoadPreferences(AppearancePackFile.Import(path).Properties);editor.SaveChanges(null);
  Check(Directory.Exists(oldSnapshots)&&AppearanceStore.Load().Profiles.Single(p=>p.Key==context.CategoryKey).Layout.CustomGroups.Single().Members.SequenceEqual(new[]{"detail"}),"restoring a backup recreates missing local custom-group snapshots");
  var keep=editor.CapturePreferences();keep.Profiles.RemoveAll(p=>p.Key==context.CategoryKey);editor.LoadPreferences(keep);editor.SaveChanges(null);
  Check(AppearanceStore.Load().Profiles.All(p=>p.Key!=context.CategoryKey),"restoring a snapshot removes profiles absent from that backup");
  editor.LoadPreferences(propertyOnly.Properties);editor.SaveChanges(null);
  AppearancePackFile.Export(path,propertyOnly);Check(AppearancePackFile.Import(path).IncludesProperties,"backup can safely replace an existing file");
  File.Delete(path);editor.Close();
 }
}
