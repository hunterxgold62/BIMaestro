using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Compatibility;
namespace Autodesk.Revit.DB
{
    public class Document { }
    public sealed class ElementId : IEquatable<ElementId>
    {
        public int Value; public ElementId(int value){Value=value;}
        public bool Equals(ElementId other)=>other!=null&&other.Value==Value;
        public override bool Equals(object other)=>Equals(other as ElementId);
        public override int GetHashCode()=>Value;
    }
}
namespace Autodesk.Revit.UI.Events
{
    public class IdlingEventArgs:EventArgs { }
#if NATIVE_SELECTION
    public class SelectionChangedEventArgs:EventArgs
    {
        internal Document Document;internal ISet<ElementId> Ids;
        public Document GetDocument()=>Document;
        public ISet<ElementId> GetSelectedElements()=>Ids;
    }
#endif
}
namespace Autodesk.Revit.UI
{
    public class Selection { internal List<ElementId> Ids=new List<ElementId>();public ICollection<ElementId> GetElementIds()=>Ids; }
    public class UIDocument { public Document Document{get;set;}public Selection Selection{get;}=new Selection(); }
    public class UIApplication { public UIDocument ActiveUIDocument{get;set;} }
    public class UIControlledApplication
    {
        private event EventHandler<Events.IdlingEventArgs> _idle;
        internal int IdleSubscribers,NativeSubscribers;
        public event EventHandler<Events.IdlingEventArgs> Idling { add{IdleSubscribers++;_idle+=value;}remove{IdleSubscribers--;_idle-=value;} }
        internal void Idle(UIApplication app)=>_idle?.Invoke(app,new Events.IdlingEventArgs());
#if NATIVE_SELECTION
        private event EventHandler<Events.SelectionChangedEventArgs> _selection;
        public event EventHandler<Events.SelectionChangedEventArgs> SelectionChanged { add{NativeSubscribers++;_selection+=value;}remove{NativeSubscribers--;_selection-=value;} }
        internal void Select(Document doc,params int[] ids)=>_selection?.Invoke(this,new Events.SelectionChangedEventArgs{Document=doc,Ids=new HashSet<ElementId>(ids.Select(x=>new ElementId(x)))});
#endif
    }
}
class Program
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static void Main()
    {
        var app=new UIControlledApplication();var doc=new Document();var ui=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
        int calls=0;Document lastDoc=null;ICollection<ElementId> lastIds=null;
        using(var subscription=new RevitSelectionChangedSubscription(app,(d,ids)=>{calls++;lastDoc=d;lastIds=ids;}))
        {
#if NATIVE_SELECTION
            Check(app.NativeSubscribers==1&&app.IdleSubscribers==0,"Modern host uses its native selection event without polling");
            app.Select(doc,2,1);Check(calls==1&&lastDoc==doc&&lastIds.Count==2,"Native event forwards document and selected IDs without a hard event-argument reference");
            app.Idle(ui);Check(calls==1,"Native host does not duplicate notifications through idling");
            subscription.Dispose();subscription.Dispose();app.Select(doc,3);
            Check(app.NativeSubscribers==0&&calls==1,"Native handler is detached exactly once on shutdown");
#else
            Check(app.IdleSubscribers==1,"Revit 2022 without the event uses idle polling");
            ui.ActiveUIDocument.Selection.Ids.AddRange(new[]{new ElementId(1),new ElementId(2)});app.Idle(ui);
            Check(calls==1&&lastDoc==doc&&lastIds.Count==2,"Legacy host forwards its first selection snapshot");
            Thread.Sleep(110);ui.ActiveUIDocument.Selection.Ids.Reverse();app.Idle(ui);
            Check(calls==1,"Reordered identical IDs do not create spurious changes");
            Thread.Sleep(110);ui.ActiveUIDocument.Selection.Ids.Add(new ElementId(3));app.Idle(ui);
            Check(calls==2&&lastIds.Count==3,"Changed selection is delivered on the legacy host");
            Thread.Sleep(110);var second=new Document();ui.ActiveUIDocument.Document=second;app.Idle(ui);
            Check(calls==3&&lastDoc==second,"Changing document is detected even with the same selected IDs");
            Thread.Sleep(110);ui.ActiveUIDocument=null;app.Idle(ui);
            Check(calls==4&&lastDoc==null&&lastIds.Count==0,"Closing all documents clears the legacy selection context");
            subscription.Dispose();subscription.Dispose();app.Idle(ui);
            Check(app.IdleSubscribers==0&&calls==4,"Legacy polling is detached exactly once on shutdown");
#endif
        }
        Console.WriteLine("Selection compatibility checks passed");
    }
}
