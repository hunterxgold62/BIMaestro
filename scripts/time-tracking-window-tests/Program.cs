using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BIMaestro.Dashboard;

static class ThemeManager { internal static void EnsureThemeLoaded() { } }
namespace BIMaestro.Localization
{
    static class UiLanguage
    {
        internal static string Language = "fr";
        internal static string Get(string key) => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Strings."+Language+".json")))[key];
        internal static string Format(string key, params object[] values) => string.Format(Get(key), values);
    }
}
class Program
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    [STAThread] static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../output/time-tracking-preview")); Directory.CreateDirectory(output);
        foreach (string language in new[]{"fr","en"})
        {
            BIMaestro.Localization.UiLanguage.Language = language;
            var projects = new[] { new TimeTrackingDecisionWindow.ProjectChoice { ProjectId="original",Name="École Jean-Moulin",Path=@"C:\Projets\École Jean-Moulin\Revit 2024\École.rvt",Hours=12+35.0/60 } };
            var window = new TimeTrackingDecisionWindow(projects,@"D:\Archives\École Jean-Moulin\Revit 2026\École-renommée.rvt",1.5);
            Check(window.Choice==TimeTrackingDecisionWindow.Decision.Later,"Closing without a decision defaults to later: "+language);
            // Render the real content offscreen with its actual WPF theme, at normal and narrow widths.
            var content = (FrameworkElement)window.Content;window.Content=null;
            var surface = new Border { Resources=window.Resources,Background=(Brush)window.FindResource("App.Background"),Child=content };
            foreach(double width in new[]{644.0,504.0})
            {
                surface.Measure(new Size(width,double.PositiveInfinity));
                surface.Arrange(new Rect(0,0,width,surface.DesiredSize.Height));surface.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var buttons=Descendants(surface).OfType<Button>().Where(x=>x.ActualWidth>0).ToList();
                Check(buttons.Count==3 && buttons.All(x=>x.ActualHeight>=32),"All three choices remain usable at "+width+" px: "+language);
                var text=Descendants(surface).OfType<TextBlock>().Select(x=>x.Text).ToList();
                Check(text.Any(x=>x.Contains("12 h 35")) && text.Any(x=>x.Contains("1 h 30")),"Existing and pending durations are visible: "+language);
                Check(!text.Any(x=>x.StartsWith("TimeTracking.")),"All window text is translated: "+language);
                var bitmap=new RenderTargetBitmap((int)width,(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"decision-"+language+"-"+width+".png")))png.Save(stream);
            }
            window.Close();
        }
        // A lineage can contain separately tracked copies. The selector must update the target and its total.
        var candidates=new[]{new TimeTrackingDecisionWindow.ProjectChoice{ProjectId="A",Name="École",Path=@"C:\École.rvt",Hours=12},new TimeTrackingDecisionWindow.ProjectChoice{ProjectId="B",Name="Variante",Path=@"D:\Variante.rvt",Hours=3}};
        var multiple=new TimeTrackingDecisionWindow(candidates,@"D:\Copie.rvt",.5);
        var scroll=(ScrollViewer)multiple.Content; var panel=(StackPanel)scroll.Content;
        var combo=panel.Children.OfType<Border>().Select(x=>x.Child).OfType<StackPanel>().SelectMany(x=>x.Children.OfType<ComboBox>()).Single();
        combo.SelectedIndex=1;Check(multiple.SelectedProjectId=="B","Selecting another recognized project changes the decision target");multiple.Close();
        Console.WriteLine("Previews: "+output);
        app.Shutdown();
    }
}
