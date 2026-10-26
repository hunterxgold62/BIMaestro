using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Analyse;

public sealed class Probe : IExternalApplication
{
    UIControlledApplication host;
    UIApplication ui;
    Document doc;
    SmartScanSession scan;
    string folder = Path.GetDirectoryName(typeof(Probe).Assembly.Location);
    int stage;
    Type scene => typeof(SmartScanSession).Assembly.GetType("BIMaestro.Tutorials.DemoClashExercise", true);
    object Call(string method, params object[] args) => scene.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    void Log(string text) => File.AppendAllText(Path.Combine(folder, "result.txt"), text + "\n");
    public Result OnStartup(UIControlledApplication app)
    { host = app; app.Idling += Tick; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication app)
    { app.Idling -= Tick; File.WriteAllText(Path.Combine(folder, "exited-cleanly.txt"), "yes"); return Result.Succeeded; }
    void Tick(object sender, IdlingEventArgs e)
    {
        try
        {
            if (stage == 0)
            {
                ui = (UIApplication)sender;
                if (ui.Application.Documents.Size != 0) { Log("REFUSED: existing documents"); host.Idling -= Tick; return; }
                File.WriteAllText(Path.Combine(folder, "started.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
                using (var tx = new Transaction(doc, "Fixture"))
                { tx.Start(); if (!new FilteredElementCollector(doc).OfClass(typeof(Level)).Any()) Level.Create(doc, 0);
                  if (!new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Any()) PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Test"); tx.Commit(); }
                doc.SaveAs(Path.Combine(folder, "BIMaestro_Apprentissage_2026.rvt"));
                Call("Prepare", doc);
                StartScan(SmartScanScope.Selection); stage = 1;
            }
            else if (stage == 1 || stage == 2 || stage == 3)
            {
                scan.Advance(25);
                if (!scan.Complete) return;
                Log("Stage " + stage + ": sources=" + scan.SourceCount + " issues=" + scan.Issues.Count + " error=" + scan.Error);
                foreach (string diagnostic in scan.Diagnostics) Log(diagnostic);
                if (scan.Error != null) throw new Exception(scan.Error);
                int expected = stage == 3 ? 1 : 2;
                if (scan.Issues.Count != expected || scan.Issues.Any(i => i.IsApproximate)) throw new Exception("Unexpected intersections");
                scan.Dispose();
                if (stage == 1) { StartScan(SmartScanScope.Model); stage = 2; }
                else if (stage == 2) { Call("Correct", doc); StartScan(SmartScanScope.Selection); stage = 3; }
                else { Log("PASS: Revit 2026 progressive selection/model/correction"); Cleanup(); }
            }
        }
        catch (Exception ex) { Log("FAIL: " + ex); Cleanup(); }
    }
    void StartScan(SmartScanScope scope)
    {
        var ids = (System.Collections.Generic.List<ElementId>)Call("Sources", doc);
        var options = new SmartScanOptions { Scope = scope, Pipes = true, Ducts = false, CableTrays = false, Conduits = false,
            Fittings = false, LocalClashes = true, LinkedClashes = false, IncludeInsulation = false, MinimumVolumeMm3 = 10 };
        scan = new SmartScanSession(new SmartScanSetup { Document = doc, Selection = ids }, options);
    }
    void Cleanup()
    {
        host.Idling -= Tick; scan?.Dispose(); doc?.Close(false);
        File.WriteAllText(Path.Combine(folder, "finished.txt"), "yes");
        ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
    }
}
