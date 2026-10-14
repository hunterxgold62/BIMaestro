$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'BIMaestro/app et excel/Supabase/BaseTrackedCommand.cs')
$start = $source.IndexOf('    internal sealed class CommandChangeMeasurement')
$end = $source.IndexOf('    /// <summary>', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Production measurement class could not be located.' }
$testSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
namespace Autodesk.Revit.DB {
    public class Document { public bool IsValidObject = true; }
    public class ElementId {
        private int Value;
        public ElementId(int value) { Value=value; }
        public override string ToString() { return Value.ToString(); }
    }
}
namespace Autodesk.Revit.DB.Events {
    public enum UndoOperation { TransactionCommitted, TransactionGroupRolledBack }
    public class DocumentChangedEventArgs : EventArgs {
        public Document Doc;
        public UndoOperation Operation;
        public List<ElementId> Added=new List<ElementId>(),Modified=new List<ElementId>(),Deleted=new List<ElementId>();
        public Document GetDocument() { return Doc; }
        public ICollection<ElementId> GetAddedElementIds() { return Added; }
        public ICollection<ElementId> GetModifiedElementIds() { return Modified; }
        public ICollection<ElementId> GetDeletedElementIds() { return Deleted; }
    }
}
namespace Autodesk.Revit.ApplicationServices {
    public class Application {
        public event EventHandler<DocumentChangedEventArgs> DocumentChanged;
        public int Subscribers { get { return DocumentChanged==null ? 0 : DocumentChanged.GetInvocationList().Length; } }
        public void Raise(DocumentChangedEventArgs args) { if(DocumentChanged!=null) DocumentChanged(this,args); }
    }
}
namespace Autodesk.Revit.UI {
    public class Selection {
        public List<ElementId> Ids = new List<ElementId>();
        public ICollection<ElementId> GetElementIds() { return Ids; }
    }
    public class UIDocument { public Document Document=new Document(); public Selection Selection=new Selection(); }
    public class UIApplication {
        public Autodesk.Revit.ApplicationServices.Application Application=new Autodesk.Revit.ApplicationServices.Application();
        public UIDocument ActiveUIDocument=new UIDocument();
    }
}
namespace Licensing {
'@
$testSource += $source.Substring($start,$end-$start)
$testSource += @'
    public static class MeasurementTests {
        private static List<ElementId> Ids(params int[] values) { return values.Select(i=>new ElementId(i)).ToList(); }
        public static string Run() {
            var app=new UIApplication();
            using(var measurement=new CommandChangeMeasurement(app)) {
                app.Application.Raise(new DocumentChangedEventArgs { Doc=app.ActiveUIDocument.Document,
                    Added=Ids(1,2),Modified=Ids(5) });
                app.Application.Raise(new DocumentChangedEventArgs { Doc=app.ActiveUIDocument.Document,
                    Modified=Ids(1,5,8),Deleted=Ids(2,8) });
                app.ActiveUIDocument.Selection.Ids=Ids(1,5);
                var counts=measurement.Counts();
                if(counts["revit_elements_created"]!=1 || counts["revit_elements_modified"]!=1 ||
                    counts["revit_elements_deleted"]!=1 || counts["elements_selected"]!=2)
                    throw new Exception("Net element counts or selection failed");
                // IDs are only unique within a document.
                app.Application.Raise(new DocumentChangedEventArgs { Doc=new Document(),Added=Ids(1) });
                if(measurement.Counts()["revit_elements_created"]!=2) throw new Exception("Cross-document IDs merged");
                app.Application.Raise(new DocumentChangedEventArgs { Doc=app.ActiveUIDocument.Document,
                    Operation=UndoOperation.TransactionGroupRolledBack });
                if(measurement.Counts().Count!=0) throw new Exception("Rolled-back group produced a result");
            }
            if(app.Application.Subscribers!=0) throw new Exception("Observer left active after command completion");
            using(var measurement=new CommandChangeMeasurement(app)) {
                if(measurement.Counts().Values.Any(v=>v!=0)) throw new Exception("Unchanged operation produced a result");
            }
            return "PASS: net creations, unique modifications, deletions, selections, documents, rollback and observer disposal";
        }
    }
}
'@
Add-Type -TypeDefinition $testSource
[Licensing.MeasurementTests]::Run()
