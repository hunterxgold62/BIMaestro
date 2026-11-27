// Run with: node scripts/time-tracking-logger-tests/run.mjs
// Revit/UI boundaries are stubbed; journal, snapshots, metadata and session code are production code.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Dashboard;

class Program
{
    const BindingFlags Static = BindingFlags.Static|BindingFlags.NonPublic;
    static object Call(string name, params object[] args) => typeof(ExcelLogger).GetMethod(name, Static).Invoke(null,args);
    static IDictionary Sessions => (IDictionary)typeof(ExcelLogger).GetField("_sessions",Static).GetValue(null);
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message); }
    static WorkSession SetHours(Document doc,double hours)
    {
        var ws = Sessions.Values.Cast<WorkSession>().Single(x=>x.LastKnownDocument==doc);
        ws.Pause();
        ((List<WorkTimeSlice>)typeof(WorkSession).GetField("_activeSlices",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ws)).Clear();
        var start = DateTime.Today.AddHours(8);
        ((List<WorkTimeSlice>)typeof(WorkSession).GetField("_activeSlices",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ws)).Add(new WorkTimeSlice(start,start.AddHours(hours)));
        typeof(WorkSession).GetField("_activeAccum",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ws,TimeSpan.FromHours(hours));
        return ws;
    }
    static void MarkSnapshotsDead()
    {
        var snapshots=(IDictionary)Call("LoadSnapshotsCore");
        foreach(var snapshot in snapshots.Values) snapshot.GetType().GetField("ProcessId").SetValue(snapshot,int.MaxValue);
        Call("SaveSnapshotsCore",snapshots);
    }
    static void Main()
    {
        string root=BIMaestro.Storage.UserDataStorage.Root;Directory.CreateDirectory(root);
        string snapshot=Path.Combine(root,"TimeSnapshots.dat"),registryFile=Path.Combine(root,"tracking.xml");
        var guid=Guid.NewGuid();var doc=new Document{PathName=@"C:\2024\École.rvt",Title="École",CreationGUID=guid};
        var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
        ExcelLogger.Initialize();
        Check(File.Exists(Path.Combine(root,"Historique_Temps_Revit.xlsx")),"Isolated journal initialized");
        try
        {
            ExcelLogger.OnDocumentOpened(doc,app);var original=SetHours(doc,2);string identity=original.Classification.DocumentIdentity;
            Check(identity=="creation:"+guid.ToString("N"),"Native creation identity is recorded in session metadata");
            var olderDocument = new Document { CreationGUID = Guid.Empty, ProjectInformation = new Element { UniqueId = "legacy-project" } };
            Check(TimeLogMetadataExtractor.GetDocumentIdentity(olderDocument) == "element:legacy-project", "Legacy API fallback uses the persistent project-information identity");
            TimeTrackingRegistry.Observe(registryFile,identity,doc.PathName,doc.Title);
            string oldPath=doc.PathName;
            doc.PathName=@"D:\2026\École convertie.rvt";doc.Title="École convertie";
            ExcelLogger.OnViewActivated(doc,app);var current=SetHours(doc,1);
            Check(Sessions.Count==1 && current.Classification.SessionId!=original.Classification.SessionId,"Save As closes old session and starts exactly one new session");
            var oldRows=ExcelLogger.ReadTrackingHistory();
            Check(Math.Abs(oldRows.Sum(x=>x.Hours)-2)<.00001 && oldRows.All(x=>x.DocumentId==oldPath && x.DocumentIdentity==identity),"Save As retains old duration, original path and identity");
            var pending=TimeTrackingRegistry.Observe(registryFile,identity,doc.PathName,doc.Title);
            ExcelLogger.OnDocumentClosing(doc,app);
            var rows=ExcelLogger.ReadTrackingHistory();
            Check(Math.Abs(rows.Sum(x=>x.Hours)-3)<.00001 && rows.Any(x=>x.DocumentId==doc.PathName && x.DocumentIdentity==identity),"Closing persists the last session with its new path and identity");
            Check(Math.Abs(rows.Where(x=>TimeTrackingRegistry.Load(registryFile).Resolve(x.DocumentIdentity,x.DocumentId)==pending.PendingId).Sum(x=>x.Hours)-1)<.00001,"Closed deferred session remains identifiable as pending");
            TimeTrackingRegistry.Decide(registryFile,identity,doc.PathName,oldPath,false);
            var registry=TimeTrackingRegistry.Load(registryFile);
            Check(Math.Abs(rows.Where(x=>registry.Resolve(x.DocumentIdentity,x.DocumentId)==oldPath).Sum(x=>x.Hours)-3)<.00001,"Both persisted file sessions add to the chosen project exactly once");
            // A crash leaves a snapshot without a corresponding closed row.
            doc.PathName=@"D:\Copie différée.rvt";ExcelLogger.OnDocumentOpened(doc,app);var crashed=SetHours(doc,.75);
            TimeTrackingRegistry.Observe(registryFile,identity,doc.PathName,doc.Title);
            Call("UpsertSnapshot",doc.PathName,crashed,true);Call("FlushPendingSnapshots");
            Check(File.ReadAllText(snapshot).Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity))),"Durable snapshot includes the model identity");
            Sessions.Clear();MarkSnapshotsDead();var stale=File.ReadAllBytes(snapshot);
            Call("RecoverSnapshotsToExcelCore");
            rows=ExcelLogger.ReadTrackingHistory();
            Check(Math.Abs(rows.Sum(x=>x.Hours)-3.75)<.00001 && rows.All(x=>x.DocumentIdentity==identity),"Crash recovery restores the deferred session and its identity");
            if (File.Exists(snapshot)) File.SetAttributes(snapshot,FileAttributes.Normal);
            File.WriteAllBytes(snapshot,stale);Call("RecoverSnapshotsToExcelCore");
            Check(Math.Abs(ExcelLogger.ReadTrackingHistory().Sum(x=>x.Hours)-3.75)<.00001,"Replaying a committed snapshot never duplicates its session");
            // Simulate Excel holding an exclusive file handle at the moment Revit closes the document.
            doc.PathName=@"D:\Verrouillé.rvt";ExcelLogger.OnDocumentOpened(doc,app);SetHours(doc,.25);
            using(var exclusive=new FileStream(Path.Combine(root,"Historique_Temps_Revit.xlsx"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                ExcelLogger.OnDocumentClosing(doc,app);
            Call("FlushPendingSnapshots");ExcelLogger.Shutdown();
            Check(File.Exists(snapshot),"Failed workbook write leaves its final snapshot after shutdown");
            MarkSnapshotsDead();Call("RecoverSnapshotsToExcelCore");
            Check(Math.Abs(ExcelLogger.ReadTrackingHistory().Sum(x=>x.Hours)-4)<.00001,"Last session is recoverable once the workbook is unlocked");
            string legacy=string.Join("|",new[]{"v3","","","","","0","0","1","0","","","","","","","","","","","",""});
            Check(Call("ParseSnapshotLine",legacy)!=null,"Older v3 snapshots without an identity remain readable");
            Console.WriteLine("Journal lifecycle checks passed");
        }
        finally
        {
            ExcelLogger.Shutdown();
            foreach(var item in Directory.GetFiles(Path.Combine(root,"Temp")))File.Delete(item);
            Directory.Delete(Path.Combine(root,"Temp"));
            foreach(var item in Directory.GetFiles(root))File.Delete(item);
            Directory.Delete(root);
        }
    }
}
