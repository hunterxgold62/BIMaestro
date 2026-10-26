using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMaestro.PropertiesAppearance;
namespace BIMaestro.Localization { public static class UiLanguage { public static string T(string fr,string en)=>fr; } }
namespace BIMaestro.PropertiesAppearance {
 internal class AppearanceContext {
  internal string CategoryKey="cat:walls",CategoryName="Murs",TypeKey="cat:walls/type:basic",TypeName="Mur générique",Caption="Murs · Mur générique",PreviewHint="Démonstration";
  internal List<PropertyPreviewRow> PreviewRows=new List<PropertyPreviewRow>();
  internal static AppearanceContext ForEditor(object app)=>Program.Context;
 }
 internal static class PropertiesAppearanceService {
  internal static bool IsDark;internal static AppearancePreferences Saved=new AppearancePreferences();internal static string Status="Test";internal static int CancelCount;
  internal static void SetPreview(IEnumerable<AppearanceProfile> p,bool enabled){}
  internal static void ProcessIdling(object app){}
  internal static void Commit(IEnumerable<AppearanceProfile> p,bool enabled){Saved=AppearanceStore.SaveMany(p,enabled);}
  internal static void ResetAll(){Saved=AppearanceStore.ResetAll();}
  internal static void CancelPreview(){CancelCount++;}
 }
 internal static class PropertiesAppearanceCommand {
  internal static bool Hold;internal static Queue<Action<object>> Pending=new Queue<Action<object>>();
  internal static void Request(Action<object> action){if(Hold)Pending.Enqueue(action);else action(null);}
  internal static void Drain(){while(Pending.Count>0)Pending.Dequeue()(null);}
 }
}
class Program {
 internal static AppearanceContext Context=new AppearanceContext();
 static BindingFlags B=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 static object Field(object w,string n)=>w.GetType().GetField(n,B).GetValue(w);
 static object Invoke(object w,string n,params object[] args)=>w.GetType().GetMethod(n,B).Invoke(w,args);
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static void Preset(object w,int index){var item=((IEnumerable)Field(w,"_presets")).Cast<object>().ElementAt(index);((Button)item.GetType().GetProperty("Item1").GetValue(item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
 [STAThread]static void Main(){
  new System.Windows.Application();
  string folder=Path.Combine(Environment.CurrentDirectory,"output","tests","BIMaestro-EditorFlow-"+Guid.NewGuid());AppearanceStore.TestFilePath=Path.Combine(folder,"settings.json");
  var group=new PropertyPreviewRow{Header=true,GroupKey="g",Label="Graphismes"};
  var scale=new PropertyPreviewRow{GroupKey="g",ParameterKey="scale",Label="Échelle de la vue",Value="1 : 100"};
  var locked=new PropertyPreviewRow{GroupKey="g",ParameterKey="locked",Label="Valeur de l’échelle",Value="100",ReadOnly=true};
  var detail=new PropertyPreviewRow{GroupKey="g",ParameterKey="detail",Label="Niveau de détail",Value="Élevé"};
  var extents=new PropertyPreviewRow{Header=true,GroupKey="ext",Label="Étendues"};
  var crop=new PropertyPreviewRow{GroupKey="ext",ParameterKey="crop",Label="Cadrer la vue",Value="Non"};
  Context.PreviewRows.AddRange(new[]{group,scale,locked,detail,extents,crop});
  LayoutFlow.Run(Context);
  var w=new PropertiesAppearanceEditor(Context);
  Check(w is UserControl && !w.HasChanges,"embedded editor starts from saved preferences without changes");
  Check(((System.Windows.Media.SolidColorBrush)w.Resources["Ink"]).Color==((System.Windows.Media.SolidColorBrush)w.Resources["Text.Primary"]).Color,"embedded editor uses the shared Couleur palette");
  Check(((TextBlock)Field(w,"_selection")).Text.Contains("Graphismes"),"initial selection identifies the group");
  HomeFlow.Render(w,"proprietes");
  HomeFlow.Run();
  Preset(w,1);var profiles=(Dictionary<string,AppearanceProfile>)Field(w,"_drafts");
  Check(profiles[Context.CategoryKey].Rules.Count==1&&!File.Exists(AppearanceStore.FilePath),"preset changes the category draft without saving");
  Check(((PaletteColors)Invoke(w,"Effective",detail)).Background=="#FFE7F1FB","group preset immediately appears on writable rows");
  Check(Invoke(w,"Effective",locked)==null,"read-only row retains native appearance in comparison");
  Invoke(w,"Select",detail);Preset(w,2);
  Check(((PaletteColors)Invoke(w,"Effective",detail)).Background=="#FFFBF1D9"&&((PaletteColors)Invoke(w,"Effective",scale)).Background=="#FFE7F1FB","one parameter overrides its group without changing its neighbours");
  ((TextBox)Field(w,"_search")).Text="échelle";
  Check(((StackPanel)Field(w,"_rows")).Children.Count==3,"accent-insensitive search retains the group and matching rows");
  Invoke(w,"SetTone",true);Preset(w,3);
  var rule=profiles[Context.CategoryKey].Rules.Single(x=>x.Kind=="parameter");
  Check(rule.Dark.Background=="#FF604483"&&rule.Light.Background=="#FFFBF1D9","dark preset changes only the dark variant");
  Invoke(w,"ResetSelection");Check(rule.Dark.IsEmpty&&!rule.Light.IsEmpty,"reset keeps the other theme variant");
  Invoke(w,"MoveRow",detail,scale,false);
  var ordering=AppearanceOrdering.Resolve(new AppearancePreferences{Profiles=profiles.Values.ToList()},Context.CategoryKey,Context.TypeKey);
  Check(AppearanceOrdering.Arrange(Context.PreviewRows,ordering)[1]==detail,"parameter drag reorders only the right-hand draft");
  Invoke(w,"MoveRow",extents,group,false);
  ordering=AppearanceOrdering.Resolve(new AppearancePreferences{Profiles=profiles.Values.ToList()},Context.CategoryKey,Context.TypeKey);
  var arranged=AppearanceOrdering.Arrange(Context.PreviewRows,ordering);
  Check(arranged[0]==extents&&arranged[1]==crop&&Context.PreviewRows[0]==group,"group drag carries its parameters while the original stays fixed");
  Check(!(bool)Invoke(w,"CanMove",detail,crop),"cross-group parameter moves are kept disabled pending native editing qualification");
  Invoke(w,"Save");Check(AppearanceStore.Load().Enabled&&AppearanceStore.Load().Profiles.Single().Key==Context.CategoryKey,"save persists the intended category and activation");
  Check(AppearanceStore.Load().Profiles.Single().Layout.Groups[0]=="ext","saved layout survives serialization");
  var reopened=new PropertiesAppearanceEditor(Context);var reopenedProfiles=(Dictionary<string,AppearanceProfile>)Field(reopened,"_drafts");Check(reopenedProfiles[Context.CategoryKey].Rules.Count==2,"reopening restores both group and parameter customization");
  ((CheckBox)Field(reopened,"_live")).IsChecked=true;((CheckBox)Field(reopened,"_live")).IsChecked=false;Check(PropertiesAppearanceService.CancelCount==1,"stopping the live test restores the saved palette");
  var originalRows=((StackPanel)Field(reopened,"_rows")).Children.Count;
  Invoke(reopened,"ClickRow",group);Check(((StackPanel)Field(reopened,"_rows")).Children.Count==originalRows-3,"single click collapses the group on both sides");
  ((TextBox)Field(reopened,"_search")).Text="Échelle";Check(((StackPanel)Field(reopened,"_rows")).Children.Count==3,"search temporarily reveals matching rows in a collapsed group");
  ((TextBox)Field(reopened,"_search")).Text="";Invoke(reopened,"ClickRow",group);Check(((StackPanel)Field(reopened,"_rows")).Children.Count==originalRows,"second click expands the group again");
  Invoke(reopened,"ResetEverything");var reset=AppearanceStore.Load();Check(!reset.Enabled&&reset.Profiles.Count==0,"global reset clears order and colors across all scopes and themes");
  Check(((Dictionary<string,AppearanceProfile>)Field(reopened,"_drafts")).Values.All(p=>p.Rules.Count==0&&p.Layout.IsEmpty),"global reset also clears the visible draft and comparison");
  bool completed=false;Preset(reopened,1);PropertiesAppearanceCommand.Hold=true;
  reopened.SaveChanges(()=>completed=true);
  Check(!completed&&AppearanceStore.Load().Profiles.Count==0,"shared save waits for the Revit event before completing");
  PropertiesAppearanceCommand.Drain();Check(completed&&!reopened.HasChanges,"shared save completes only after the draft is persisted");
  Preset(reopened,2);reopened.SaveChanges(()=>throw new Exception("closed editor must not complete a save"));reopened.Close();
  var saved=File.ReadAllText(AppearanceStore.FilePath);PropertiesAppearanceCommand.Drain();
  Check(File.ReadAllText(AppearanceStore.FilePath)==saved,"closing an editor prevents a queued draft from being saved");
  PropertiesAppearanceCommand.Hold=false;
  CustomGroupFlow.Run(Context);
  PackFlow.Run(Context);
  w.Close();File.Delete(AppearanceStore.FilePath);File.Delete(AppearanceStore.FilePath+".bak");
  string snapshots=AppearanceStore.FilePath+".groups";if(Directory.Exists(snapshots)){foreach(var file in Directory.GetFiles(snapshots,"*.json"))File.Delete(file);Directory.Delete(snapshots);}
  Directory.Delete(folder);
 }
}
