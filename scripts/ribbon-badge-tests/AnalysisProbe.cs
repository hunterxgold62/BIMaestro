using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;

public class RibbonAnalysisProbe : IExternalApplication
{
    static bool started;
    static FileStream runLock;
    static string Folder => Path.GetDirectoryName(typeof(RibbonAnalysisProbe).Assembly.Location);
    static readonly string[] Ids = { "PipeLengthByDiameterV2", "Qui a fait ça ?", "ElementHistoryHoverToggle", "AnalysePoidsButton", "Temps par projet", "Suivi maquette collaboratif", "Clash 3D" };
    public Result OnStartup(UIControlledApplication app) { if (File.Exists(Path.Combine(Folder, "analysis.flag"))) app.Idling += Run; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
    static void Log(string value) => File.AppendAllText(Path.Combine(Folder, "analysis.log"), value + Environment.NewLine);
    void Run(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs args)
    {
        ((UIApplication)sender).Idling -= Run;
        if (started) return;
        started = true;
        try { runLock = File.Open(Path.Combine(Folder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        File.WriteAllText(Path.Combine(Folder, "analysis.log"), DateTime.Now.ToString("O") + Environment.NewLine);
        Capture(0);
    }
    static bool IsVideo(int index) => index == 1 || index == 3 || index == 4 || index == 6;
    static void Capture(int index)
    {
        if (index == Ids.Length) { Log("DONE"); runLock?.Dispose(); return; }
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "BIMaestro");
            if (index == 0) Log("Plugin: " + assembly.Location);
            var info = assembly.GetType("AppUI").GetMethod("GetRibbonButtonById").Invoke(null, new object[] { Ids[index] });
            var button = (PushButton)info.GetType().GetProperty("PushButton").GetValue(info);
            var item = typeof(RibbonItem).GetField("m_RibbonItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(button);
            var tip = item.GetType().GetProperty("ToolTip").GetValue(item);
            var nativeType = typeof(Autodesk.Windows.RibbonToolTip).Assembly.GetType("Autodesk.Internal.Windows.ToolTip");
            var popup = (ToolTip)Activator.CreateInstance(nativeType, true);
            nativeType.GetProperty("ContentSource").SetValue(popup, tip);
            nativeType.GetMethod("Show", new[] { typeof(int), typeof(int) }).Invoke(popup, new object[] { 180, 260 });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IsVideo(index) ? 3 : 1.5) };
            timer.Tick += (s, e) => {
                timer.Stop();
                try
                {
                    popup.UpdateLayout();
                    if (!popup.IsOpen || !(popup.Content is StackPanel)) throw new Exception("Contenu personnalisé absent");
                    var children = Descendants(popup).ToList();
                    var texts = children.OfType<TextBlock>().Select(t => t.Text).ToList();
                    if (!children.OfType<Image>().Any(i => i.Source != null))
                        throw new Exception("Exemple absent");
                    if (popup.ActualWidth > 355 || popup.ActualHeight > 410) throw new Exception("Dimensions excessives");
                    var video = children.OfType<MediaElement>().FirstOrDefault();
                    if (IsVideo(index) && (video == null || video.Position.TotalSeconds < .2 || !video.NaturalDuration.HasTimeSpan))
                        throw new Exception("Vidéo non lue : position=" + video?.Position + " durée=" + video?.NaturalDuration);
                    Log(Ids[index] + " PASS " + popup.ActualWidth + "x" + popup.ActualHeight + (video == null ? "" : " VIDEO position=" + video.Position + " duration=" + video.NaturalDuration));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth), (int)Math.Ceiling(popup.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(popup);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(Folder, "analysis-" + index + ".png"))) encoder.Save(file);
                    popup.IsOpen = false;
                    if (video != null) Log("VIDEO CLOSED position=" + video.Position);
                }
                catch (Exception error) { Log(Ids[index] + " FAIL " + error); popup.IsOpen = false; }
                popup.Dispatcher.BeginInvoke(new Action(() => Capture(index + 1)), DispatcherPriority.ApplicationIdle);
            };
            timer.Start();
        }
        catch (Exception error) { Log(Ids[index] + " FAIL " + error); Capture(index + 1); }
    }
    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }
}







