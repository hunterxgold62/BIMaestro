using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BIMaestro.HomeTests
{
 public class HomeProbe : IExternalApplication
 {
  static readonly string Root = Path.GetDirectoryName(typeof(HomeProbe).Assembly.Location);
  static readonly object Gate = new object();
  DateTime next; string stamp = ""; int scan;
  const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
  static void Log(string text) { lock(Gate) File.AppendAllText(Path.Combine(Root,"probe.log"),DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine); }
  public Result OnStartup(UIControlledApplication app) { app.Idling += Tick; File.WriteAllText(Path.Combine(Root,"started.txt"),DateTime.UtcNow.ToString("O")); Log("START "+app.ControlledApplication.VersionNumber); return Result.Succeeded; }
  public Result OnShutdown(UIControlledApplication app) { app.Idling -= Tick; return Result.Succeeded; }
  void Tick(object sender,IdlingEventArgs args)
  {
   if(DateTime.UtcNow < next) return; next=DateTime.UtcNow.AddSeconds(3);
   try {
    string file=Path.Combine(Root,"control.js"); string token=File.Exists(file)?File.GetLastWriteTimeUtc(file).Ticks.ToString():"discover";
    if(token==stamp && scan%10!=0) {scan++;return;}
    var seen=new HashSet<object>(ReferenceComparer.Instance); var browsers=new List<object>();
    if(Application.Current!=null) foreach(Window w in Application.Current.Windows) Walk(w,seen,browsers,0);
    foreach(var source in PresentationSource.CurrentSources.OfType<PresentationSource>()) Walk(source.RootVisual,seen,browsers,0);
    // Home may live in a Win32-hosted WPF tree, outside Application.Windows.
    foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name.IndexOf("ApplicationHome",StringComparison.OrdinalIgnoreCase)>=0)) {
     Type[] types;try{types=assembly.GetTypes();}catch{continue;}
     foreach(var t in types.Where(t=>t.FullName.IndexOf("ModelBrowser",StringComparison.OrdinalIgnoreCase)>=0 || t.FullName.IndexOf("Home",StringComparison.OrdinalIgnoreCase)>=0))
      foreach(var f in t.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)) try {if(!f.FieldType.IsValueType && f.FieldType!=typeof(string)) Walk(f.GetValue(null),seen,browsers,0);}catch{}
    }
    if(scan%10==0) Log("DISCOVERY "+seen.Count+" objects; "+browsers.Count+" browser candidates; assemblies "+string.Join(",",AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name.IndexOf("Home",StringComparison.OrdinalIgnoreCase)>=0 || a.GetName().Name.IndexOf("Cef",StringComparison.OrdinalIgnoreCase)>=0).Select(a=>a.GetName().Name)));
    scan++;
    foreach(var b in browsers) {
     string address=Read(b,"Address")??Read(b,"Source")??"";
     Log("CANDIDATE "+b.GetType().FullName+" "+address);
     // Never execute the user script until the page itself positively identifies Home.
     string supplied=File.Exists(file)?File.ReadAllText(file):"return JSON.stringify({title:document.title,url:location.href,html:document.documentElement.outerHTML})";
     string script="(function(){var u=String(location.href);if(!(/modelbrowser|applicationhome|revit.?home/i.test(u)))return 'HOME_GUARD_REJECT '+u;return (function(){"+supplied+"\n})();})()";
     if(Evaluate(b,script,token)) stamp=token;
    }
   }catch(Exception e){Log("ERROR "+e);}
  }
  static string Read(object x,string name){try{return Convert.ToString(x.GetType().GetProperty(name,Flags)?.GetValue(x,null));}catch{return null;}}
  static void Walk(object x,HashSet<object> seen,List<object> browsers,int depth)
  {
   if(x==null || depth>18 || seen.Count>30000 || !seen.Add(x))return;
   Type t=x.GetType();string n=t.FullName??"";
   if(n.IndexOf("ChromiumWebBrowser",StringComparison.OrdinalIgnoreCase)>=0 || n.IndexOf("WebView2",StringComparison.OrdinalIgnoreCase)>=0 || n.IndexOf("CefSharp",StringComparison.OrdinalIgnoreCase)>=0 && t.GetProperty("Address")!=null) browsers.Add(x);
   var d=x as DependencyObject;if(d!=null){try{for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++)Walk(VisualTreeHelper.GetChild(d,i),seen,browsers,depth+1);}catch{}try{foreach(var c in LogicalTreeHelper.GetChildren(d)) if(!(c is string))Walk(c,seen,browsers,depth+1);}catch{}}
   if(depth<8 && (n.IndexOf("Home",StringComparison.OrdinalIgnoreCase)>=0 || n.IndexOf("ModelBrowser",StringComparison.OrdinalIgnoreCase)>=0 || n.IndexOf("Cef",StringComparison.OrdinalIgnoreCase)>=0)) {
    foreach(var f in t.GetFields(Flags))try{if(!f.FieldType.IsValueType && f.FieldType!=typeof(string))Walk(f.GetValue(x),seen,browsers,depth+1);}catch{}
   }
   foreach(string p in new[]{"Content","Child","Browser","WebBrowser","WebView","CoreWebView2"})try{var prop=t.GetProperty(p,Flags);if(prop!=null && prop.GetIndexParameters().Length==0)Walk(prop.GetValue(x,null),seen,browsers,depth+1);}catch{}
  }
  static bool Evaluate(object browser,string script,string token)
  {
   try {
    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("CefSharp.WebBrowserExtensions",false)).FirstOrDefault(t=>t!=null);
    var method=type?.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="EvaluateScriptAsync" && m.GetParameters().Length>=2 && m.GetParameters()[0].ParameterType.IsInstanceOfType(browser) && m.GetParameters()[1].ParameterType==typeof(string) && m.GetParameters().Skip(2).All(p=>p.IsOptional));
    object task=null;
    if(method!=null)task=method.Invoke(null,method.GetParameters().Select((p,i)=>i==0?browser:i==1?(object)script:Type.Missing).ToArray());
    else {method=browser.GetType().GetMethods().FirstOrDefault(m=>(m.Name=="ExecuteScriptAsync" || m.Name=="EvaluateScriptAsync") && m.GetParameters().Length>=1 && m.GetParameters()[0].ParameterType==typeof(string) && m.GetParameters().Skip(1).All(p=>p.IsOptional));if(method!=null)task=method.Invoke(browser,method.GetParameters().Select((p,i)=>i==0?(object)script:Type.Missing).ToArray());}
    var asyncTask=task as Task;if(asyncTask==null)return false;
    asyncTask.ContinueWith(done=>{try{if(done.IsFaulted){Log("EVAL_ERROR "+done.Exception);return;}var result=done.GetType().GetProperty("Result")?.GetValue(done,null);var value=result?.GetType().GetProperty("Result")?.GetValue(result,null)??result;string output=Convert.ToString(value)??"";File.WriteAllText(Path.Combine(Root,"result-"+token+".txt"),output);Log("EVAL_RESULT "+output.Substring(0,Math.Min(output.Length,1500)));}catch(Exception e){Log("RESULT_ERROR "+e);}});
    return true;
   }catch(Exception e){Log("INVOKE_ERROR "+e);return false;}
  }
  class ReferenceComparer:IEqualityComparer<object>{public static readonly ReferenceComparer Instance=new ReferenceComparer();public new bool Equals(object a,object b){return ReferenceEquals(a,b);}public int GetHashCode(object x){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x);}}
 }
}
