using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

// Opt-in native diagnostic. Requires enabled.flag beside the assembly.
public class RibbonBadgeProbe : IExternalApplication
{
    static string Folder => Path.GetDirectoryName(typeof(RibbonBadgeProbe).Assembly.Location);
    static string LogPath => Path.Combine(Folder, "probe.log");
    public Result OnStartup(UIControlledApplication app)
    {
        if (File.Exists(Path.Combine(Folder, "enabled.flag"))) app.Idling += Probe;
        return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app) { app.Idling -= Probe; return Result.Succeeded; }
    static void Log(string text) => File.AppendAllText(LogPath, text + Environment.NewLine);
    void Probe(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        ((UIApplication)sender).Idling -= Probe;
        try
        {
            File.WriteAllText(LogPath, DateTime.Now.ToString("O") + Environment.NewLine);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "BIMaestro");
            Log("Plugin: " + assembly.Location);
            var appUi = assembly.GetType("AppUI");
            Func<string, object> getTip = id => {
                var info = appUi.GetMethod("GetRibbonButtonById").Invoke(null, new object[] { id });
                var button = (PushButton)info.GetType().GetProperty("PushButton").GetValue(info);
                var item = typeof(RibbonItem).GetField("m_RibbonItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(button);
                return item.GetType().GetProperty("ToolTip").GetValue(item);
            };
            Capture(getTip("ViewTemplateTransfer"), "NEW", () => Capture(getTip("Qui a fait ça ?"), "MAJ", null));
        }
        catch (Exception error) { Log(error.ToString()); }
    }
    static void Capture(object tip, string label, Action next)
    {
        var type = typeof(Autodesk.Windows.RibbonToolTip).Assembly.GetType("Autodesk.Internal.Windows.ToolTip");
        var popup = (ToolTip)Activator.CreateInstance(type, true);
        type.GetProperty("ContentSource").SetValue(popup, tip);
        type.GetMethod("Show", new[] { typeof(int), typeof(int) }).Invoke(popup, new object[] { 100, 240 });
        popup.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                popup.UpdateLayout();
                var badge = Descendants(popup).OfType<TextBlock>().FirstOrDefault(t => t.Text == label);
                if (!popup.IsOpen || badge == null) throw new Exception(label + " badge absent du rendu natif");
                var position = badge.TransformToAncestor(popup).Transform(new Point(0, 0));
                if (position.X < 0 || position.X + badge.ActualWidth > popup.ActualWidth || position.Y > 50)
                    throw new Exception(label + " badge hors de l'en-tête visible");
                Log(label + " PASS size=" + popup.ActualWidth + "x" + popup.ActualHeight + " badge=" + position);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth), (int)Math.Ceiling(popup.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(popup);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(Folder, "native-tooltip-" + label + ".png"))) encoder.Save(file);
            }
            catch (Exception error) { Log(error.ToString()); }
            finally { popup.IsOpen = false; }
            if (next != null) popup.Dispatcher.BeginInvoke(next, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
