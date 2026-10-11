using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

// Metadata-only check, not a substitute for native Revit execution/playback.
class CompatibilityCheck
{
    static int Main(string[] args)
    {
        try
        {
            var install = Path.GetFullPath(args[0]);
            var plugin = Path.GetFullPath(args[1]);
            var source = File.ReadAllText(args[2]);
            var roots = new[] { install, Path.GetDirectoryName(plugin),
                Path.GetDirectoryName(typeof(System.Windows.DependencyObject).Assembly.Location),
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };
            var paths = roots.SelectMany(r => Directory.GetFiles(r, "*.dll"))
                .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First());
            using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
            var ui = context.LoadFromAssemblyPath(Path.Combine(install, "RevitAPIUI.dll"));
            var ad = context.LoadFromAssemblyPath(Path.Combine(install, "AdWindows.dll"));
            var ribbonField = ui.GetType("Autodesk.Revit.UI.RibbonItem", true).GetField("m_RibbonItem", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(ribbonField != null, "RibbonItem.m_RibbonItem");
            var tipProperty = ribbonField.FieldType.GetProperty("ToolTip");
            Require(tipProperty?.CanWrite == true, "RibbonItem.ToolTip");
            var open = ad.GetType("Autodesk.Windows.ComponentManager", true).GetEvent("ToolTipOpened");
            Require(open?.EventHandlerType.FullName == "System.EventHandler", "ToolTipOpened EventHandler");
            var tip = ad.GetType("Autodesk.Windows.RibbonToolTip", true);
            Require(tipProperty.PropertyType.IsAssignableFrom(tip), "RibbonToolTip assignment");
            Require(tip.GetProperty("Content")?.CanWrite == true, "RibbonToolTip.Content");
            var popup = ad.GetType("Autodesk.Internal.Windows.ToolTip", true);
            Require(popup.GetProperty("ContentSource")?.CanWrite == true, "ContentSource");
            Require(popup.GetField("ContentSourceProperty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null, "ContentSourceProperty");
            Require(popup.GetMethods().Any(m => m.Name == "Show" && m.GetParameters().Length == 2 && m.GetParameters().All(p => p.ParameterType.FullName == "System.Int32")), "Native Show");
            Console.WriteLine("PASS native ribbon reflection contracts, Revit " + ui.GetName().Version);
            var asm = context.LoadFromAssemblyPath(plugin);
            var apiReference = asm.GetReferencedAssemblies().First(a => a.Name == "RevitAPIUI");
            Require(apiReference.Version.Major == ui.GetName().Version.Major, "Plugin targets the correct Revit API");
            var runtimeConfig = Path.Combine(install, "RevitAPI.runtimeconfig.json");
            if (File.Exists(runtimeConfig))
            {
                var runtime = Regex.Match(File.ReadAllText(runtimeConfig), "\"tfm\"\\s*:\\s*\"net(\\d+)");
                Require(runtime.Success, "Host runtime identified");
                var reference = asm.GetReferencedAssemblies().First(a => a.Name == "System.Runtime");
                Require(reference.Version.Major == int.Parse(runtime.Groups[1].Value), "Plugin matches installed host .NET runtime");
            }
            Require(asm.GetType("AppUI", true).GetMethod("AddToolTipExample", BindingFlags.Static | BindingFlags.NonPublic) != null, "Compiled tooltip examples");
            var section = source.Substring(source.IndexOf("private static bool AddToolTipExample"));
            section = section.Substring(0, section.IndexOf("private static Border ExampleCard"));
            var ids = Regex.Matches(section, "case \"([^\"]+)\"").Count;
            var mediaRoot = Path.Combine(Path.GetDirectoryName(plugin), "Content", "ToolTipExamples");
            var videos = Regex.Matches(section, "ExampleVideo\\(\"([^\"]+)\"\\)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
            foreach (var name in videos)
            {
                Require(File.Exists(Path.Combine(mediaRoot, name + ".mp4")), name + " packaged MP4");
                Require(File.Exists(Path.Combine(mediaRoot, name + "Poster.png")), name + " packaged poster");
            }
            foreach (var name in Regex.Matches(section, "(?:ModificationImage|ExampleImage)\\(\"([^\"]+)\"\\)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct())
                Require(File.Exists(Path.Combine(mediaRoot, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png")), name + " packaged PNG");
            Console.WriteLine("PASS " + ids + " tooltip mappings, " + videos.Length + " videos; correct API/runtime and packaged media");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Require(bool ok, string check) { if (!ok) throw new Exception("FAIL " + check); }
}
