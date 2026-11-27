using System;
using System.Collections.Generic;
namespace Autodesk.Revit.DB
{
    public class Document
    {
        public string PathName { get; set; }
        public string Title { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsValidObject => true;
        public bool IsLinked => false;
        public bool IsFamilyDocument => false;
        public Guid CreationGUID { get; set; }
        public Element OwnerFamily => null;
        public Element ProjectInformation { get; set; } = new Element();
        public ModelPath GetWorksharingCentralModelPath() => null;
    }
    public class ModelPath { }
    public static class ModelPathUtils { public static string ConvertModelPathToUserVisiblePath(ModelPath p) => null; }
    public class Element { public string UniqueId { get; set; } = "project-info"; public List<Parameter> Parameters => new List<Parameter>(); }
    public class Parameter { public Definition Definition => null; public StorageType StorageType => StorageType.String; public string AsValueString()=>null;public string AsString()=>null;public int AsInteger()=>0;public double AsDouble()=>0;public ElementId AsElementId()=>null; }
    public class Definition { public string Name => ""; }
    public enum StorageType { String, Integer, Double, ElementId }
    public class ElementId { public long GetIdLongValue()=>0; }
}
namespace Autodesk.Revit.UI
{
    public class UIApplication { public UIDocument ActiveUIDocument { get; set; } public RevitApplication Application { get; set; } = new RevitApplication(); }
    public class UIDocument { public Autodesk.Revit.DB.Document Document { get; set; } }
    public class RevitApplication { public string VersionNumber => "2026"; public string Username => "test"; }
}
namespace Microsoft.Win32
{
    public enum PowerModes { Suspend, Resume }
    public class PowerModeChangedEventArgs : EventArgs { public PowerModes Mode { get; set; } }
    public class SessionEndingEventArgs : EventArgs { }
    public static class SystemEvents { public static event EventHandler<PowerModeChangedEventArgs> PowerModeChanged { add {} remove {} } public static event EventHandler<SessionEndingEventArgs> SessionEnding { add {} remove {} } }
}
namespace BIMaestro.Storage
{
    public static class UserDataStorage { public static string Root = System.IO.Path.Combine(AppContext.BaseDirectory,"run-"+Guid.NewGuid().ToString("N")); }
}
namespace BIMaestro.Dashboard
{
    internal static class TimeProjectTracking { internal static void Observe(WorkSession session) {} internal static void Process(WorkSession session, Autodesk.Revit.UI.UIApplication app) {} }
}
