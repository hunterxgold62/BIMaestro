using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using BIMaestro.PropertiesAppearance;

internal static class CustomGroupFlow
{
    private sealed class LegacyLayout {public List<string> Groups {get;set;}public Dictionary<string,List<string>> Parameters {get;set;}}
    private sealed class LegacyProfile {public string Key {get;set;}public string Name {get;set;}public List<AppearanceRule> Rules {get;set;}public LegacyLayout Layout {get;set;}}
    private sealed class LegacyPreferences {public int Version {get;set;}public bool Enabled {get;set;}public List<LegacyProfile> Profiles {get;set;}}
    private static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    internal static void Run(AppearanceContext context)
    {
        var editor=new PropertiesAppearanceEditor(context);
        var original=context.PreviewRows.ToArray();var parameter=original.Single(r=>r.ParameterKey=="detail");
        ((TextBox)Get(editor,"_groupName")).Text="Mes paramètres";Call(editor,"CreateCustomGroup");
        var profiles=(Dictionary<string,AppearanceProfile>)Get(editor,"_drafts");var definition=profiles[context.CategoryKey].Layout.CustomGroups.Single();
        Check(definition.Name=="Mes paramètres"&&definition.Members.Count==0,"create an empty custom group in the current category draft");
        var after=AppearanceOrdering.Arrange(context.PreviewRows,profiles[context.CategoryKey].Layout);
        var header=after.Single(r=>r.Header&&r.GroupKey==definition.Key);
        Check((bool)Call(editor,"CanMove",parameter,header),"native parameter can be dropped on a new custom header");
        Call(editor,"MoveRow",parameter,header,true);
        after=AppearanceOrdering.Arrange(context.PreviewRows,profiles[context.CategoryKey].Layout);
        Check(after.Single(r=>r.ParameterKey==parameter.ParameterKey).GroupKey==definition.Key&&after.Count(r=>r.ParameterKey==parameter.ParameterKey)==1,"drag transfers a parameter exactly once into the new group");
        Check(original.SequenceEqual(context.PreviewRows)&&parameter.GroupKey=="g","custom arrangement leaves original rows and native group identity unchanged");
        definition.Members.Add("locked");
        Check(AppearanceOrdering.Arrange(context.PreviewRows,profiles[context.CategoryKey].Layout).Single(r=>r.ParameterKey=="locked").GroupKey=="g","a saved parameter that becomes readonly remains in its native group in the preview");
        definition.Members.Remove("locked");
        try{Call(editor,"AssignToGroup",original.Single(r=>r.ParameterKey=="locked"),definition.Key);throw new Exception("Readonly assignment accepted");}
        catch(TargetInvocationException error){Check(error.InnerException is InvalidOperationException,"readonly parameters cannot be assigned to custom groups");}
        Call(editor,"Select",header);((TextBox)Get(editor,"_groupRenameName")).Text="Contrôle métier";Call(editor,"RenameCustomGroup");
        Check(definition.Name=="Contrôle métier","rename retains the custom group identity and membership");
        HomeFlow.Render(editor,"proprietes-groupes");
        Call(editor,"Save");var loaded=AppearanceStore.Load().Profiles.Single(p=>p.Key==context.CategoryKey).Layout.CustomGroups.Single();
        Check(loaded.Key==definition.Key&&loaded.Name==definition.Name&&loaded.Members.SequenceEqual(new[]{"detail"}),"save and reload preserve group name, stable key and exact parameter membership");
        var legacyRoundTrip=Newtonsoft.Json.JsonConvert.DeserializeObject<LegacyPreferences>(File.ReadAllText(AppearanceStore.FilePath));
        Check(!File.ReadAllText(AppearanceStore.FilePath).Contains("CustomGroups"),"group definitions do not rely on JSON fields discarded by older BIMaestro versions");
        File.WriteAllText(AppearanceStore.FilePath,Newtonsoft.Json.JsonConvert.SerializeObject(legacyRoundTrip,Newtonsoft.Json.Formatting.Indented));
        Check(AppearanceStore.Load().Profiles.Single(p=>p.Key==context.CategoryKey).Layout.CustomGroups.Single().Members.SequenceEqual(new[]{"detail"}),"legacy-compatible preference rewrite preserves referenced custom groups");
        var inline=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(AppearanceStore.FilePath));var layout=(Newtonsoft.Json.Linq.JObject)inline["Profiles"].Single(p=>(string)p["Key"]==context.CategoryKey)["Layout"];
        foreach(var marker in ((Newtonsoft.Json.Linq.JObject)layout["Parameters"]).Properties().Where(p=>p.Name.StartsWith("bimaestro:custom-groups:")).ToArray())marker.Remove();
        layout["CustomGroups"]=Newtonsoft.Json.Linq.JArray.FromObject(new[]{loaded.Copy()});File.WriteAllText(AppearanceStore.FilePath,inline.ToString());
        Check(AppearanceStore.Load().Profiles.Single(p=>p.Key==context.CategoryKey).Layout.CustomGroups.Single().Key==loaded.Key,"initial inline experimental groups remain readable for migration");
        var saved=File.ReadAllText(AppearanceStore.FilePath);
        ((TextBox)Get(editor,"_groupName")).Text="Autre groupe";Call(editor,"CreateCustomGroup");
        var second=profiles[context.CategoryKey].Layout.CustomGroups.Last();
        Call(editor,"AssignToGroup",after.Single(r=>r.ParameterKey=="detail"),second.Key);
        Check(definition.Members.Count==0&&second.Members.SequenceEqual(new[]{"detail"}),"transfer between custom groups removes the previous membership");
        Call(editor,"Select",new PropertyPreviewRow{Header=true,GroupKey=second.Key,Label=second.Name});Call(editor,"DeleteCustomGroup");
        after=AppearanceOrdering.Arrange(context.PreviewRows,profiles[context.CategoryKey].Layout);
        Check(after.Single(r=>r.ParameterKey=="detail").GroupKey==parameter.GroupKey,"deleting a custom group returns its parameters to their native group");
        profiles[context.TypeKey].Layout.CustomGroups=profiles[context.CategoryKey].Layout.CustomGroups.Select(g=>g.Copy()).ToList();
        try{Call(editor,"CreateCustomGroup");throw new Exception("Hidden scope modification accepted");}
        catch(TargetInvocationException error){Check(error.InnerException is InvalidOperationException,"a more specific override cannot silently hide a custom-group edit");}
        editor.Close();Check(File.ReadAllText(AppearanceStore.FilePath)==saved,"closing unsaved group changes preserves saved profiles");
        var inherited=new AppearanceProfile{Key="all",Layout=new AppearanceLayout{CustomGroups=new List<AppearanceCustomGroup>{loaded.Copy()}}};
        var category=new AppearanceProfile{Key=context.CategoryKey};
        var preferences=new AppearancePreferences{Profiles=new List<AppearanceProfile>{inherited,category}};
        Check(AppearanceOrdering.Resolve(preferences,context.CategoryKey,null).CustomGroups.Count==1,"null custom groups inherit the wider scope");
        category.Layout.CustomGroups=new List<AppearanceCustomGroup>();
        Check(AppearanceOrdering.Resolve(preferences,context.CategoryKey,null).CustomGroups.Count==0,"an explicit empty custom-group override removes inherited groups");
    }
}
