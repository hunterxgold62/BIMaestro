using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Analyse;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace BIMaestro.Tests
{
    public sealed class ReferenceValidation : IExternalApplication
    {
        private string _folder;
        private UIControlledApplication _app;
        private bool _owns;
        public Result OnStartup(UIControlledApplication app)
        {
            _app = app; _folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try { using (var file = new FileStream(Path.Combine(_folder, "claim-pid.txt"), FileMode.CreateNew))
                using (var writer = new StreamWriter(file)) writer.Write(Process.GetCurrentProcess().Id); }
            catch (IOException) { return Result.Succeeded; }
            _owns = true; app.DialogBoxShowing += Dialog; app.Idling += Run;
            return Result.Succeeded;
        }
        private void Dialog(object sender, DialogBoxShowingEventArgs args)
        {
            if (args is TaskDialogShowingEventArgs dialog && dialog.DialogId == "TaskDialog_Default_Family_Template_File_Invalid")
                dialog.OverrideResult(1);
        }
        private void Log(string text) => File.AppendAllText(Path.Combine(_folder, "progress.txt"), text + Environment.NewLine);
        private void Check(bool condition, string text) { if (!condition) throw new Exception(text); Log("PASS " + text); }
        private static IEnumerable<DependencyObject> Children(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject dependency) { yield return dependency; foreach (var item in Children(dependency)) yield return item; }
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            _app.Idling -= Run; var ui = (UIApplication)sender;
            File.WriteAllText(Path.Combine(_folder, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            if (ui.Application.Documents.Size != 0) { Log("FAIL instance must have no user document"); return; }
            try
            {
                var production = typeof(BIMaestro.Tutorials.CreateDemoProjectCommand).Assembly;
                    foreach (string name in new[] { "DemoChoiceWindow", "DemoDiscoveryWindow" })
                    {
                        var window = (Window)Activator.CreateInstance(production.GetType("BIMaestro.Tutorials." + name), true);
                        var color = Children(window).OfType<Button>().Single(button =>
                            Children(button).OfType<TextBlock>().Any(label => label.Text.Contains("Couleurs et vues")) ||
                            (button.Content as string)?.Contains("Couleurs et vues") == true);
                        Check(color.IsEnabled, name + " colors tutorial remains enabled");
                        window.Close();
                    }
                string reference = Path.Combine(_folder, "BIMaestro_Apprentissage_2024.rvt");
                if (ui.Application.VersionNumber == "2024")
                {
                    Log("Generating reference in Revit 2024");
                    production.GetType("BIMaestro.Tutorials.DemoProjectBuilder").GetMethod("BuildReference", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { ui, reference });
                    using (var info = BasicFileInfo.Extract(reference)) Check(info.Format == "2024", "reference format is Revit 2024");
                }
                else Check(File.Exists(reference), "reference supplied for upgrade test");
                string packaged = Path.Combine(_folder, "Demo", "Maquette", "BIMaestro_Apprentissage_2024.rvt");
                if (!File.Exists(packaged)) File.Copy(reference, packaged, false);
                string copies = Path.Combine(_folder, "personal-copies");
                Directory.CreateDirectory(copies);
                string witness = Path.Combine(copies, "BIMaestro_Apprentissage_" + ui.Application.VersionNumber + ".rvt");
                File.WriteAllText(witness, "existing user file");
                var copyMethod = production.GetType("BIMaestro.Tutorials.DemoProjectBuilder").GetMethod("CopyReference", BindingFlags.Static | BindingFlags.NonPublic);
                string first = (string)copyMethod.Invoke(null, new object[] { ui.Application.VersionNumber, copies });
                string second = (string)copyMethod.Invoke(null, new object[] { ui.Application.VersionNumber, copies });
                Check(first != second && File.ReadAllText(witness) == "existing user file", "personal copies preserve existing files");
                using (var info = BasicFileInfo.Extract(first)) Check(info.Format == "2024", "personal copy starts in reference format");
                reference = first;
                var doc = ui.Application.OpenDocumentFile(reference);
                try
                {
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Select(view => view.Name).ToList();
                    Check(views.Contains("BIMaestro - 10 Clash 3D") && views.Contains("BIMaestro - 09 MEP Booster") &&
                        views.Contains("BIMaestro - 01 Auto réservation"), "training views preserved");
                    var sources = (List<ElementId>)production.GetType("BIMaestro.Tutorials.DemoClashExercise")
                        .GetMethod("Sources", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { doc });
                    Check(sources.Count == 3, "three Clash 3D training pipes preserved");
                    using (var scan = new SmartScanSession(new SmartScanSetup { Document = doc, DocumentKey = "reference-validation",
                        ViewId = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().First(view => !view.IsTemplate).Id,
                        Selection = sources }, new SmartScanOptions { Scope = SmartScanScope.Selection, Pipes = true,
                        Ducts = false, CableTrays = false, Conduits = false, Fittings = false, LinkedClashes = false,
                        IncludeInsulation = false, LocalClashes = true, MinimumVolumeMm3 = 10 }))
                    {
                        var watch = Stopwatch.StartNew();
                        while (!scan.Complete && watch.Elapsed < TimeSpan.FromSeconds(60)) scan.Advance();
                        Check((bool)production.GetType("BIMaestro.Tutorials.DemoClashExercise").GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new object[] { doc, scan, false }), "both initial Clash 3D intersections verified after opening");
                    }
                    string[] required = { "CML_Parking", "CML_Table ronde + chaise", "CML_Réservation rectangulaire murale" };
                    var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Select(family => family.Name).ToList();
                    Check(required.All(families.Contains), "reservation parking and history families embedded");
                }
                finally { doc.Close(false); }
                using (var info = BasicFileInfo.Extract(packaged)) Check(info.Format == "2024", "packaged reference stays in 2024 after opening a copy");
                File.WriteAllText(Path.Combine(_folder, "finished.txt"), "0");
            }
            catch (Exception ex) { Log("FAIL " + ex); File.WriteAllText(Path.Combine(_folder, "finished.txt"), "1"); }
            ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
        public Result OnShutdown(UIControlledApplication app)
        { if (_owns) { app.Idling -= Run; app.DialogBoxShowing -= Dialog; File.WriteAllText(Path.Combine(_folder, "exited-cleanly.txt"), "OnShutdown"); } return Result.Succeeded; }
    }
}
