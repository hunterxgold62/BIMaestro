using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMaestro.Home;
using Newtonsoft.Json;

namespace BIMaestro.HomeTests
{
 public sealed class HomeIntegration : IExternalApplication
 {
  static readonly string Root = Path.GetDirectoryName(typeof(HomeIntegration).Assembly.Location);
  DateTime next; long stamp; string lastStatus;
  public Result OnStartup(UIControlledApplication app)
  {
   HomeAppearanceService.ConfigureTestSettingsPath(Path.Combine(Root,"preferences.json"));
   HomeAppearanceService.Initialize(app);
   const string tab="BIMaestro Home Test";
   try { app.CreateRibbonTab(tab); } catch { }
   var panel=app.CreateRibbonPanel(tab,"Couleur et information");
   var data=new PushButtonData("BIMaestro.Home.Integration","Accueil\nTest",typeof(HomeIntegration).Assembly.Location,typeof(HomeAppearanceCommand).FullName);
   data.AvailabilityClassName=typeof(HomeAppearanceAvailability).FullName;
   panel.AddItem(data);
   app.Idling+=Tick;
   File.WriteAllText(Path.Combine(Root,"started.txt"),DateTime.UtcNow.ToString("O"));
   return Result.Succeeded;
  }
  public Result OnShutdown(UIControlledApplication app)
  { app.Idling-=Tick; HomeAppearanceService.Shutdown(app); return Result.Succeeded; }
  void Tick(object sender,IdlingEventArgs args)
  {
   if(DateTime.UtcNow<next)return; next=DateTime.UtcNow.AddSeconds(1);
   string status=JsonConvert.SerializeObject(new {style=HomeAppearanceService.CurrentStyle,history=HomeAppearanceService.HistoryEnabled,status=HomeAppearanceService.LastStatus,dark=HomeAppearanceService.IsDarkTheme()});
   if(status!=lastStatus){File.WriteAllText(Path.Combine(Root,"status.json"),status);lastStatus=status;}
   string file=Path.Combine(Root,"control.json"); if(!File.Exists(file))return;
   long token=File.GetLastWriteTimeUtc(file).Ticks; if(token==stamp)return; stamp=token;
   try {
    string json=File.ReadAllText(file);
    string style=Read(json,"style");
    if(style!=null && !Regex.IsMatch(style,"^(native|2023|2024|2025|2026)$"))throw new ArgumentException("Unknown style");
    var history=Regex.Match(json,"\"history\"\\s*:\\s*(true|false)",RegexOptions.IgnoreCase);
    if(style!=null || history.Success)HomeAppearanceService.SetPreferences(style??HomeAppearanceService.CurrentStyle,history.Success?history.Groups[1].Value.Equals("true",StringComparison.OrdinalIgnoreCase):HomeAppearanceService.HistoryEnabled);
    string theme=Read(json,"theme");
    if(theme!=null) {
     if(theme!="Light" && theme!="Dark")throw new ArgumentException("Theme must be Light or Dark");
     var manager=typeof(UIApplication).Assembly.GetType("Autodesk.Revit.UI.UIThemeManager");
     var property=manager==null?null:manager.GetProperty("CurrentTheme",BindingFlags.Public|BindingFlags.Static);
     if(property==null || !property.CanWrite)throw new NotSupportedException("Native theme API unavailable in this Revit version");
     property.SetValue(null,Enum.Parse(property.PropertyType,theme),null);
    }
    HomeAppearanceService.RequestRefresh();
    File.WriteAllText(Path.Combine(Root,"result-"+token+".txt"),"OK style="+HomeAppearanceService.CurrentStyle+" history="+HomeAppearanceService.HistoryEnabled+" theme="+theme);
   } catch(Exception e) {File.WriteAllText(Path.Combine(Root,"result-"+token+".txt"),"ERROR "+e);}
  }
  static string Read(string json,string key) {var match=Regex.Match(json,"\""+key+"\"\\s*:\\s*\"([^\"]*)\"");return match.Success?match.Groups[1].Value:null;}
 }
}
