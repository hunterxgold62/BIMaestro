using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using BIMaestro.PropertiesAppearance;
class Verify {
 static void Check(bool b,string name){if(!b)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static AppearanceRule Rule(string kind,string key,string text)=>new AppearanceRule{Kind=kind,TargetKey=key,Light=new PaletteColors{Text=text}};
 static void Main(){
  AppearanceStore.TestFilePath=Path.Combine(Path.GetTempPath(),"BIMaestro-appearance-test-"+Guid.NewGuid()+"/settings.json");
  var all=new AppearanceProfile{Key="all"};all.Rules.Add(Rule("group","g","#112233"));all.Rules.Add(Rule("parameter","p","#223344"));
  var cat=new AppearanceProfile{Key="cat:1"};cat.Rules.Add(Rule("group","g","#334455"));
  var type=new AppearanceProfile{Key="cat:1/type:T"};type.Rules.Add(Rule("parameter","p","#445566"));
  var s=AppearanceStore.SaveMany(new[]{all,cat},true);s=AppearanceStore.SaveMany(new[]{type},true);
  Check(s.Profiles.Count==3,"separate saves preserve other scopes");
  Check(s.Resolve(cat.Key,type.Key,"g","p",false).Text=="#445566","specific type precedence");
  Check(s.Resolve(cat.Key,null,"g","p",false).Text=="#334455","category precedence");
  Check(s.Resolve(null,null,"g","p",false).Text=="#223344","parameter precedence within profile");
  all.Rules[0].Dark.Text="#FFFFFF";s=AppearanceStore.SaveMany(new[]{all},true);
  Check(s.Resolve(cat.Key,type.Key,"g","p",true).Text=="#FFFFFF","empty dark variants inherit broader rule");
  Check(AppearancePreferences.Normalize(" É tendues ")=="ETENDUES","accent insensitive native label matching");
  var colors=new PaletteColors{Text="#FFFFFF",Background="#FFFFFF"};
  Check(AppearancePreferences.Contrast(AppearancePreferences.ReadableText(colors,false),Colors.White)>=4.5,"unreadable text corrected");
  type.Rules.Clear();s=AppearanceStore.SaveMany(new[]{type},false);
  Check(s.Profiles.Count==2&&!s.Enabled,"reset scope preserves other profiles and disables");
  Check(!Directory.GetFiles(Path.GetDirectoryName(AppearanceStore.FilePath),"*.tmp").Any(),"atomic save leaves no temporary file");
  File.WriteAllText(AppearanceStore.FilePath,"broken-json");bool failed=false;try{AppearanceStore.SaveMany(new[]{all},true);}catch{failed=true;}
  Check(failed&&File.ReadAllText(AppearanceStore.FilePath)=="broken-json","corrupt preferences preserved");
  File.Delete(AppearanceStore.FilePath); File.Delete(AppearanceStore.FilePath+".bak"); Directory.Delete(Path.GetDirectoryName(AppearanceStore.FilePath));
 }
}
