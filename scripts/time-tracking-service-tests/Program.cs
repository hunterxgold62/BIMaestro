// The real orchestration runs against deterministic native-focus/UI and Revit boundaries.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIMaestro.Dashboard;
using Autodesk.Revit.UI;

static class TestGate
{
    internal static bool Foreground=true,MainEnabled=true,Throw;
    internal static int Questions,Warnings;
    internal static TimeTrackingDecisionWindow.Decision Choice;
    internal static Action DuringDialog;
    internal static double Pending;
    internal static IReadOnlyList<TimeTrackingDecisionWindow.ProjectChoice> Candidates;
}
namespace System.Windows
{
    public enum MessageBoxButton { OK }
    public enum MessageBoxImage { Warning }
    public static class MessageBox { public static void Show(string message,string title,MessageBoxButton button,MessageBoxImage image) { TestGate.Warnings++; } }
}
namespace System.Windows.Input
{
    public enum MouseButtonState { Released,Pressed }
    public static class Mouse { public static MouseButtonState LeftButton => MouseButtonState.Released; }
}
namespace System.Windows.Interop { public class WindowInteropHelper { public WindowInteropHelper(object window) {} public IntPtr Owner{get;set;} } }
namespace Autodesk.Revit.UI
{
    public class Document { public string OlderIdentity; }
    public class UIDocument { public Document Document{get;set;} }
    public class UIApplication { public UIDocument ActiveUIDocument{get;set;} public IntPtr MainWindowHandle=>new IntPtr(1); }
}
namespace BIMaestro.Storage { public static class UserDataStorage { public static string Root=Path.Combine(AppContext.BaseDirectory,"run-"+Guid.NewGuid().ToString("N")); } }
namespace BIMaestro.Localization { public static class UiLanguage { public static string Get(string key)=>key; } }
public class TimeLogMetadata { public string DocumentIdentity,LocalPath,CentralPath; }
public static class TimeLogMetadataExtractor { internal static string GetOlderDocumentIdentity(Document document)=>document?.OlderIdentity; }
public class WorkSession
{
    public string DocumentKey,DocumentName,TrackingLocation;
    public TimeLogMetadata Classification;
    public Document LastKnownDocument;
    public bool TrackingPromptHandled,HasOpenSegment=true,IsActivelyCounting=true;
    public DateTime TrackingRetryUtc;
    public int Pauses,Resumes;
    public void Pause() { Pauses++;IsActivelyCounting=false; }
    public void Resume() { Resumes++;IsActivelyCounting=true; }
}
public class TimeDashboardEntry { public string DocumentIdentity,DocumentId;public double Hours; }
public static class ExcelLogger
{
    internal static readonly List<TimeDashboardEntry> History=new List<TimeDashboardEntry>(),Live=new List<TimeDashboardEntry>();
    internal static List<TimeDashboardEntry> ReadTrackingHistory()=>History.ToList();
    internal static List<TimeDashboardEntry> GetDashboardEntries()=>Live.ToList();
}
namespace BIMaestro.Dashboard
{
    internal sealed class TimeTrackingDecisionWindow
    {
        internal enum Decision { Later,Continue,NewProject }
        internal sealed class ProjectChoice { public string ProjectId,Name,Path; public double Hours; }
        private readonly IReadOnlyList<ProjectChoice> _projects;
        internal Decision Choice {get;private set;}
        internal string SelectedProjectId=>_projects[0].ProjectId;
        internal TimeTrackingDecisionWindow(IReadOnlyList<ProjectChoice> projects,string path,double pending)
        { _projects=projects;TestGate.Candidates=projects;TestGate.Pending=pending; }
        internal void ShowDialog()
        {
            TestGate.Questions++;TestGate.DuringDialog?.Invoke();
            if(TestGate.Throw)throw new IOException("simulated UI failure");
            Choice=TestGate.Choice;
        }
    }
}
class Program
{
    const string Identity="creation:service-model",Old=@"C:\2024\École.rvt",New=@"D:\2026\École.rvt";
    static int checks;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message); }
    static WorkSession Session(string path,bool saved=true)=>new WorkSession{DocumentKey=path,DocumentName="École",LastKnownDocument=new Document{OlderIdentity="element:service-model"},Classification=new TimeLogMetadata{DocumentIdentity=Identity,LocalPath=saved?path:null}};
    static void Activate(UIApplication app,WorkSession ws)=>app.ActiveUIDocument=new UIDocument{Document=ws.LastKnownDocument};
    static void Main()
    {
        Directory.CreateDirectory(BIMaestro.Storage.UserDataStorage.Root);
        string file=TimeProjectTracking.RegistryPath;
        var app=new UIApplication();
        try
        {
            var original=Session(Old);Activate(app,original);TimeProjectTracking.Process(original,app);
            Check(TestGate.Questions==0,"First encounter records identity without a question");
            var copy=Session(New);Activate(app,copy);
            ExcelLogger.History.Add(new TimeDashboardEntry{DocumentIdentity=Identity,DocumentId=Old,Hours=12});
            ExcelLogger.Live.Add(new TimeDashboardEntry{DocumentIdentity=Identity,DocumentId=New,Hours=1.5});
            TestGate.Choice=TimeTrackingDecisionWindow.Decision.Later;
            TestGate.DuringDialog=()=>TimeProjectTracking.Process(copy,app);
            TimeProjectTracking.Process(copy,app);TestGate.DuringDialog=null;
            Check(TestGate.Questions==1,"New location asks once and ignores nested idle callbacks");
            Check(TestGate.Pending==1.5 && TestGate.Candidates.Single().Hours==12,"Question separates previous project time from pending work");
            Check(copy.Pauses==1 && copy.Resumes==1 && copy.IsActivelyCounting,"Counting pauses for the question and resumes afterward");
            TimeProjectTracking.Process(copy,app);
            Check(TestGate.Questions==1,"Decide later does not repeat during the same opening");
            Check(TimeTrackingRegistry.Load(file).Find(Identity,New).Pending,"Deferring leaves a durable pending assignment");
            ExcelLogger.History.Add(ExcelLogger.Live.Single());ExcelLogger.Live.Clear();ExcelLogger.Live.Add(new TimeDashboardEntry{DocumentIdentity=Identity,DocumentId=New,Hours=.5});
            var reopened=Session(New);Activate(app,reopened);TestGate.Choice=TimeTrackingDecisionWindow.Decision.Continue;
            TimeProjectTracking.Process(reopened,app);
            Check(TestGate.Questions==2 && TestGate.Pending==2,"Next opening offers all deferred sessions plus the current session");
            var registry=TimeTrackingRegistry.Load(file);
            Check(registry.Resolve(Identity,Old)==registry.Resolve(Identity,New),"Continue links both files to one project");
            Check(ExcelLogger.History.Concat(ExcelLogger.Live).Where(x=>registry.Resolve(x.DocumentIdentity,x.DocumentId)==Old).Sum(x=>x.Hours)==14,"Choosing a project keeps a single combined total");
            foreach(string path in new[]{Old,New,Old,New}) { var ws=Session(path);Activate(app,ws);TimeProjectTracking.Process(ws,app); }
            Check(TestGate.Questions==2,"Repeated returns to old and new files never prompt again");
            var independent=Session(@"D:\Independent.rvt");independent.IsActivelyCounting=false;Activate(app,independent);TestGate.Choice=TimeTrackingDecisionWindow.Decision.NewProject;
            TimeProjectTracking.Process(independent,app);registry=TimeTrackingRegistry.Load(file);
            Check(!registry.Find(Identity,independent.DocumentKey).Pending && registry.Resolve(Identity,independent.DocumentKey)!=Old,"Start new project commits a separate remembered assignment");
            Check(independent.Resumes==0 && !independent.IsActivelyCounting,"Question never restarts counting for an already inactive session");
            int questions=TestGate.Questions;
            var background=Session(@"D:\Background.rvt");TimeProjectTracking.Process(background,app);
            Check(TestGate.Questions==questions,"A background document cannot interrupt the active model");
            Activate(app,background);TestGate.Foreground=false;TimeProjectTracking.Process(background,app);
            Check(TestGate.Questions==questions,"No question appears while another application has focus");
            TestGate.Foreground=true;TestGate.MainEnabled=false;TimeProjectTracking.Process(background,app);
            Check(TestGate.Questions==questions,"Question waits while Revit is disabled by another dialog");
            TestGate.MainEnabled=true;TestGate.Choice=TimeTrackingDecisionWindow.Decision.Later;TimeProjectTracking.Process(background,app);
            Check(TestGate.Questions==questions+1,"Question appears once Revit becomes available");
            var unsaved=Session("Untitled",false);Activate(app,unsaved);TimeProjectTracking.Process(unsaved,app);
            Check(TimeTrackingRegistry.Load(file).Find(Identity,"Untitled")==null,"Unsaved models do not create a misleading durable path");
            var failure=Session(@"D:\Failure.rvt");Activate(app,failure);TestGate.Throw=true;TimeProjectTracking.Process(failure,app);
            Check(TestGate.Warnings==1 && failure.Resumes==1 && TimeTrackingRegistry.Load(file).Find(Identity,failure.DocumentKey).Pending,"Dialog failure reports the issue, restores counting and retains pending assignment");
            TestGate.Throw=false;var retry=Session(failure.DocumentKey);Activate(app,retry);TimeProjectTracking.Process(retry,app);
            Check(retry.TrackingPromptHandled && TestGate.Questions==questions+3,"A fresh opening retries after a dialog failure without a stuck global guard");
            string healthy=File.ReadAllText(file);File.WriteAllText(file,"broken XML");
            var broken=Session(@"D:\Broken.rvt");Activate(app,broken);TimeProjectTracking.Process(broken,app);
            Check(TimeProjectTracking.LastError!=null && broken.TrackingRetryUtc>DateTime.UtcNow,"Registry failure is reported and throttled");
            File.WriteAllText(file,healthy);broken.TrackingRetryUtc=DateTime.MinValue;TimeProjectTracking.Process(broken,app);
            Check(TimeProjectTracking.LastError==null,"Successful retry clears the stale failure state");
            Console.WriteLine(checks+" service checks passed");
        }
        finally { foreach(string entry in Directory.GetFiles(BIMaestro.Storage.UserDataStorage.Root))File.Delete(entry);Directory.Delete(BIMaestro.Storage.UserDataStorage.Root); }
    }
}
