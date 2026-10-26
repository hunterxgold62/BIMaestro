using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace Autodesk.Revit.Attributes {
 internal enum TransactionMode { Manual }
 [AttributeUsage(AttributeTargets.Class)] internal sealed class TransactionAttribute : Attribute { public TransactionAttribute(TransactionMode mode) {} }
}
namespace Autodesk.Revit.DB { public sealed class ElementSet {} public sealed class CategorySet {} }
namespace Autodesk.Revit.UI {
 public enum Result { Succeeded, Failed }
 public sealed class UIApplication {}
 public sealed class ExternalCommandData { public UIApplication Application = new UIApplication(); }
 public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements); }
 public interface IExternalCommandAvailability { bool IsCommandAvailable(UIApplication app, Autodesk.Revit.DB.CategorySet categories); }
}
namespace Couleur {
 internal static class CustomizeRibbonColorsCommand {
  internal static string Section;
  internal static void ShowPreferences(Autodesk.Revit.UI.UIApplication app, string section) { Section=section; }
 }
}
namespace BIMaestro.Home {
 internal static class HomeAppearanceService {
  internal static string CurrentStyle="2024"; internal static bool HistoryEnabled=true; internal static int Saves;
  internal static void SetPreferences(string style,bool history) { CurrentStyle=style;HistoryEnabled=history;Saves++; }
 }
}
internal static class HomeFlow {
 static void Check(bool ok,string label) { if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label); }
 internal static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject {
  foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) {
   if(child is T match)yield return match;
   foreach(var nested in Children<T>(child))yield return nested;
  }
 }
 internal static void Render(FrameworkElement content,string name,double width=1168,double height=660) {
  var frame=new Border { Child=content, Padding=new Thickness(16), Background=Brushes.White };
  frame.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/EditorFlow;component/Themes/BIMaestroTheme.xaml",UriKind.Relative) });
  frame.Measure(new Size(width,height));frame.Arrange(new Rect(0,0,width,height));frame.UpdateLayout();
  var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);bitmap.Render(frame);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
  string folder=Path.Combine(Environment.CurrentDirectory,"output","appearance-tabs");Directory.CreateDirectory(folder);
  using(var stream=File.Create(Path.Combine(folder,name+".png")))encoder.Save(stream);
  frame.Child=null;
 }
 internal static void Run() {
  var editor=new BIMaestro.Home.HomeAppearanceEditor();
  Check(!editor.HasChanges&&BIMaestro.Home.HomeAppearanceService.Saves==0,"home editor loads existing presentation and history without saving");
  var radios=Children<RadioButton>(editor).ToArray();Check(radios.Length==5&&radios[2].IsChecked==true,"home editor restores the selected presentation");
  radios[4].IsChecked=true;Children<CheckBox>(editor).Single().IsChecked=false;
  Check(editor.HasChanges&&BIMaestro.Home.HomeAppearanceService.CurrentStyle=="2024"&&BIMaestro.Home.HomeAppearanceService.HistoryEnabled,"home choices remain drafts until the shared save");
  Render(editor,"accueil-revit");
  editor.SaveChanges();Check(!editor.HasChanges&&BIMaestro.Home.HomeAppearanceService.CurrentStyle=="2026"&&!BIMaestro.Home.HomeAppearanceService.HistoryEnabled,"home shared save persists presentation and history together");
  bool requested=false;editor.ShowHomeRequested+=()=>requested=true;
  Children<Button>(editor).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Check(requested&&BIMaestro.Home.HomeAppearanceService.Saves==1,"show-home button delegates to the common save flow");
  string message=null;var result=new BIMaestro.Home.HomeAppearanceCommand().Execute(new Autodesk.Revit.UI.ExternalCommandData(),ref message,new Autodesk.Revit.DB.ElementSet());
  Check(result==Autodesk.Revit.UI.Result.Succeeded&&Couleur.CustomizeRibbonColorsCommand.Section=="home","home ribbon shortcut selects the central home tab");
 }
}
