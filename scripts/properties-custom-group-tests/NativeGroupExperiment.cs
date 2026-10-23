using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.PropertiesAppearance;
using Newtonsoft.Json;

namespace BIMaestro.PropertiesCustomGroupTests
{
    // Disposable-fixture experiment only. No production source or parameter schema changes.
    internal static class NativeGroupExperiment
    {
        private sealed class CellRow { internal string Label, Value, Marker; }
        private sealed class State
        {
            internal NativePropertiesGrid Grid;
            internal List<CellRow> Original;
            internal List<int> Tokens;
            internal Dictionary<long, int> ParameterTokens;
            internal Dictionary<long, string> Labels;
            internal long[] Members;
            internal object[] MemberOrigins;
            internal string Title;
            internal string OwnerUniqueId;
            internal bool HeaderReady, MembersReady;
        }
        private static State _state;
        private static readonly Queue<DateTime> Reapplications=new Queue<DateTime>();
        private static bool _automaticStopped, _rebuildPending, _rebuildIssued;
        private static string _rebuildReason;
        private static string _lastPreflightFailure;
        internal static bool AutomaticFullAudit;
        internal static long ModelRevision { get; private set; }
        internal static long LayoutRevision { get; private set; }
        internal static void RecordModelChange() { ModelRevision++; }
        private static string _budgetOwner;
        private static long _budgetRevision;
        internal static object HeaderTest(NativePropertiesGrid grid)
        {
            if(_state==null)return null;
            uint row=0;int nativeResult=0;string error=null;
            try
            {
                if(!_state.HeaderReady||grid==null)return new {row,result=false,nativeResult,error};
                var rows=Read(grid);
                var matches=rows.Select((item,index)=>new {item,index}).Where(x=>x.item.Label==_state.Title&&NativePropertiesGrid.IsGroupMarker(x.item.Marker)).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("The custom header is absent or ambiguous.");
                row=(uint)matches[0].index+1;
                var desktop=NativeModuleCache.Loaded("DesktopMFC.dll");
                nativeResult=Function<NativeGroupFlag>(desktop.BaseAddress,"?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z")(ElementGrid(grid),row,1);
            }
            catch(Exception exception){error=exception.GetType().Name+": "+exception.Message;}
            return new {row,result=nativeResult!=0,nativeResult,error};
        }
        internal static object StateInfo => _state==null?null:new {title=_state.Title,headerReady=_state.HeaderReady,membersReady=_state.MembersReady,members=_state.Members,memberOrigins=_state.MemberOrigins,owner=_state.OwnerUniqueId,rebuildPending=_rebuildPending,rebuildIssued=_rebuildIssued,automaticStopped=_automaticStopped};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RowCountDelegate(IntPtr core);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InsertRows(IntPtr core, uint row, uint count, IntPtr heights, IntPtr cells, IntPtr rowStyles, uint columns, uint flags, int command);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddGroupHeader(IntPtr elementGrid, ref int row, IntPtr title);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr StringConstructor(IntPtr instance, IntPtr wideText);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StringDestructor(IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResetCell(IntPtr core, int store);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeGroupFlag(IntPtr elementGrid, uint row, uint column);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static T Function<T>(IntPtr module, string name) where T:Delegate
        {
            return NativeModuleCache.Export<T>(module,name);
        }
        private static string Hash(byte[] bytes)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
        }
        private static ProcessModule Module(string name,string expectedHash)
        {
            return NativeModuleCache.Qualified(name,expectedHash);
        }
        private static IntPtr ElementGrid(NativePropertiesGrid grid)
        {
            if(!grid.IsCurrent())throw new InvalidOperationException("The native grid has been replaced.");
            // Production Find has already verified exact ElemGrid RTTI and module ownership.
            var field=typeof(NativePropertiesGrid).GetField("_elementGrid",BindingFlags.Instance|BindingFlags.NonPublic);
            var pointer=(IntPtr)field.GetValue(grid);
            if(pointer==IntPtr.Zero)throw new NotSupportedException("The runtime type is not the verified ElemGrid.");
            return pointer;
        }
        private static void RefreshCount(NativePropertiesGrid grid)
        {
            if(!grid.IsCurrent())throw new InvalidOperationException("Native properties grid unavailable.");
            // WM_SETREDRAW temporarily clears WS_VISIBLE: validate the object, not visibility, inside DeferDrawing.
            var module=NativeModuleCache.Loaded("og1100asu.dll");
            grid.RowCount=Function<RowCountDelegate>(module.BaseAddress,"?GetRowCount@CGXGridCore@@UEAAKXZ")(grid.Core);
            if(grid.RowCount>2000)throw new InvalidOperationException("Unexpected native row count.");
        }
        private static List<CellRow> Read(NativePropertiesGrid grid)
        {
            RefreshCount(grid);
            var result=new List<CellRow>();
            for(uint row=1;row<=grid.RowCount;row++)result.Add(new CellRow {Label=grid.Value(row,1),Value=grid.Value(row,2),Marker=grid.Value(row,3)});
            return result;
        }
        private static string Fingerprint(Document document,Element focused=null)
        {
            long timing=PropertiesAppearanceMetrics.Begin();
            IEnumerable<Element> elements=focused==null
                ? new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(document).WhereElementIsElementType().ToElements())
                : new[]{focused,document.GetElement(focused.GetTypeId())}.Where(element=>element!=null);
            var values=new List<string>();
            foreach(var element in elements)foreach(Parameter parameter in element.Parameters)
            {
                string value;
                switch(parameter.StorageType)
                {
                    case StorageType.String:value=parameter.AsString()??"";break;
                    case StorageType.Integer:value=parameter.AsInteger().ToString(CultureInfo.InvariantCulture);break;
                    case StorageType.Double:value=parameter.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;
                    case StorageType.ElementId:value=parameter.AsElementId().ToString();break;
                    default:value="";break;
                }
                values.Add(JsonConvert.SerializeObject(new[]{element.UniqueId,parameter.Id.ToString(),parameter.Definition.Name,parameter.Definition.GetGroupTypeId().TypeId,parameter.StorageType.ToString(),value}));
            }
            values.Sort(StringComparer.Ordinal);
            string hash=Hash(Encoding.UTF8.GetBytes(string.Join("\n",values)));
            PropertiesAppearanceMetrics.End(focused==null?"group.fingerprint.full-document":"group.fingerprint.selected-element-and-type",timing);
            return hash;
        }
        private static void Write(string directory,string name,object value)=>File.WriteAllText(Path.Combine(directory,name+".json"),JsonConvert.SerializeObject(value,Formatting.Indented));
        private static void Verify(State state,string directory,string step,bool audit=true)
        {
            var rows=Read(state.Grid);
            var mappings=state.ParameterTokens.Select(pair=>new {
                id=pair.Key, expectedRow=state.Tokens.IndexOf(pair.Value)+1, actualRow=state.Grid.FindParameterRow(pair.Key), expectedLabel=state.Labels[pair.Key]
            }).ToArray();
            bool labelsMatch=mappings.All(p=>p.actualRow>0 && p.actualRow<=rows.Count && rows[p.actualRow-1].Label==p.expectedLabel);
            bool mappingMatches=mappings.All(p=>p.actualRow==p.expectedRow);
            bool countMatches=rows.Count==state.Tokens.Count;
            if(audit)Write(directory,"group-step-"+step,new {time=DateTime.UtcNow,pass=countMatches&&mappingMatches&&labelsMatch,countMatches,mappingMatches,labelsMatch,mappings,
                rows=rows.Select((row,index)=>new {row=index+1,row.Label,row.Value,row.Marker}).ToArray()});
            if(!countMatches||!mappingMatches||!labelsMatch)throw new InvalidOperationException("Native identity verification failed after "+step+"; a native rebuild is required.");
        }
        private static bool AuthorizedFixture(UIApplication app,string directory)
        {
            if(app?.ActiveUIDocument==null||app.Application.VersionNumber!="2025")return false;
            string path=app.ActiveUIDocument.Document.PathName;
            if(string.IsNullOrWhiteSpace(path)||!string.Equals(Path.GetFullPath(path),Path.GetFullPath(Path.Combine(directory,"PropertiesCustomGroupFixture.rvt")),StringComparison.OrdinalIgnoreCase))return false;
            int pid;
            string pidFile=Path.Combine(directory,"target-pid.txt");
            return File.Exists(pidFile)&&int.TryParse(File.ReadAllText(pidFile).Trim(),out pid)&&pid==Process.GetCurrentProcess().Id;
        }
        private static NativePropertiesGrid RequireIdleGrid(UIApplication app)
        {
            var grid=NativePropertiesGrid.Find(app)??throw new InvalidOperationException("Native properties grid unavailable; no selection change is allowed.");
            if(!grid.IsCurrent()||grid.IsEditing)throw new InvalidOperationException("Finish or cancel native editing before changing this experiment.");
            return grid;
        }
        private static bool OriginalMatches(State state,NativePropertiesGrid grid)
        {
            RefreshCount(grid);
            if(grid.RowCount!=state.Original.Count)return false;
            for(uint row=1;row<=grid.RowCount;row++)
            {
                var original=state.Original[(int)row-1];
                if(grid.Value(row,1)!=original.Label||NativePropertiesGrid.IsGroupMarker(grid.Value(row,3))!=NativePropertiesGrid.IsGroupMarker(original.Marker))return false;
            }
            return state.ParameterTokens.All(p=>grid.FindParameterRow(p.Key)==p.Value+1);
        }
        internal static bool ForgetStateForRebuild(UIApplication app,string directory,string reason)
        {
            if(!AuthorizedFixture(app,directory))return false;
            var grid=NativePropertiesGrid.Find(app);
            if(grid==null||grid.IsEditing)return false;
            if(_state!=null&&!OriginalMatches(_state,grid))return false;
            _state=null;_rebuildPending=false;_rebuildIssued=false;_rebuildReason=null;
            Write(directory,"group-state-forgotten",new {time=DateTime.UtcNow,reason,originalVerified=true});
            return true;
        }
        private static void RequestRebuild(UIApplication app,string directory,string reason)
        {
            if(!AuthorizedFixture(app,directory))throw new InvalidOperationException("Refusing rebuild outside the authorized fixture/process.");
            _rebuildPending=true;_rebuildReason=reason;
            var grid=NativePropertiesGrid.Find(app);
            if(grid==null||!grid.IsCurrent()||grid.IsEditing)
            {
                Write(directory,"group-rebuild-deferred",new {time=DateTime.UtcNow,reason,note="No selection change: a native editor is active or its state cannot be verified."});
                return;
            }
            if(_rebuildIssued)return;
            var selection=app.ActiveUIDocument.Selection;var ids=selection.GetElementIds().ToArray();
            selection.SetElementIds(new ElementId[0]);selection.SetElementIds(ids);_rebuildIssued=true;
            Write(directory,"group-rebuild-requested",new {time=DateTime.UtcNow,reason,note="Native reselection requested; inspect the next native report to confirm completion."});
            ForgetStateForRebuild(app,directory,reason);
        }
        private static bool HasNativeHeader(NativePropertiesGrid grid,string title)
        {
            RefreshCount(grid);uint found=0;
            for(uint row=1;row<=grid.RowCount;row++)
                if(grid.Value(row,1)==title&&NativePropertiesGrid.IsGroupMarker(grid.Value(row,3)))
                {
                    if(found!=0)throw new InvalidOperationException("Ambiguous custom native header.");
                    found=row;
                }
            if(found==0)return false;
            var desktop=NativeModuleCache.Loaded("DesktopMFC.dll");
            return Function<NativeGroupFlag>(desktop.BaseAddress,"?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z")(ElementGrid(grid),found,1)!=0;
        }
        private static void VerifyCheap(State state,NativePropertiesGrid grid)
        {
            RefreshCount(grid);
            if(grid.RowCount!=state.Tokens.Count)throw new InvalidOperationException("Custom group row count changed.");
            var rank=state.Tokens.Select((token,index)=>new {token,row=index+1}).ToDictionary(p=>p.token,p=>p.row);
            foreach(var parameter in state.ParameterTokens)
            {
                int row=grid.FindParameterRow(parameter.Key);
                if(row!=rank[parameter.Value]||grid.Value((uint)row,1)!=state.Labels[parameter.Key])
                    throw new InvalidOperationException("Custom group parameter identity changed.");
            }
        }
        internal static Element SelectedOwner(UIApplication app)
        {
            var ui=app.ActiveUIDocument;var ids=ui.Selection.GetElementIds();
            if(ids.Count==0)return ui.ActiveView is ViewPlan ? ui.ActiveView:null;
            var owners=ids.Select(ui.Document.GetElement).ToArray();
            return owners.All(e=>e is Wall)?owners[0]:null;
        }
        private static List<Parameter> Members(Element owner)
        {
            var targets=owner is Wall ? new[]{BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,BuiltInParameter.WALL_ATTR_ROOM_BOUNDING,BuiltInParameter.WALL_BASE_OFFSET,BuiltInParameter.WALL_BASE_CONSTRAINT}
                : new[]{BuiltInParameter.VIEW_SCALE,BuiltInParameter.VIEW_DETAIL_LEVEL,BuiltInParameter.VIEW_PHASE,BuiltInParameter.VIEW_PHASE_FILTER};
            var result=targets.Select(b=>owner.get_Parameter(b)).Where(p=>p!=null&&!p.IsReadOnly).ToList();
            if(owner is Wall)foreach(string name in new[]{"BIMAESTRO_Code_metier","BIMAESTRO_Statut_validation"})
            {var candidates=owner.GetParameters(name);if(candidates.Count>1)throw new InvalidOperationException("Ambiguous project parameter "+name);if(candidates.Count==1&&!candidates[0].IsReadOnly)result.Add(candidates[0]);}
            if(result.Count<2)throw new InvalidOperationException("At least two editable members are required.");
            return result;
        }
        internal static bool OwnsGrid(NativePropertiesGrid grid)
        {
            if(_state==null||!_state.MembersReady||grid.Window!=_state.Grid.Window||grid.Core!=_state.Grid.Core||!HasNativeHeader(grid,_state.Title))return false;
            VerifyCheap(_state,grid);return true;
        }
        internal static PaletteColors RowColors(NativePropertiesGrid grid,uint row)
        {
            if(_state==null||!_state.MembersReady||grid.Core!=_state.Grid.Core||row>_state.Tokens.Count)return null;
            int token=_state.Tokens[(int)row-1];
            if(token!=-1&&!_state.Members.Any(id=>_state.ParameterTokens[id]==token))return null;
            if(grid.Value(row,1)!=(token==-1?_state.Title:_state.Original[token].Label))return null;
            return new PaletteColors{Text="#155C42",Background="#DCEEE3"};
        }
        private static bool PaletteReadyForCapture(Element wall,NativePropertiesGrid grid,string directory)
        {
            RefreshCount(grid);
            int visible=0;string failure=null;var visibleIds=new HashSet<long>();
            foreach(Parameter parameter in wall.Parameters)
            {
                int row=grid.FindParameterRow(parameter.Id.Value);
                if(row<1)continue;
                if(row>grid.RowCount||AppearancePreferences.Normalize(grid.Value((uint)row,1))!=AppearancePreferences.Normalize(parameter.Definition.Name))
                {
                    failure="The API wall selection and visible native parameter labels are not synchronized.";
                    break;
                }
                visible++;visibleIds.Add(parameter.Id.Value);
            }
            if(failure==null&&visible==0)failure="No native parameter identity is visible for the selected API wall yet.";
            if(failure==null)
            {
                var required=Members(wall);
                if(required.Any(parameter=>!visibleIds.Contains(parameter.Id.Value)))failure="The selected wall member parameters are not all present in the native palette yet.";
            }
            if(failure!=null)
            {
                if(_lastPreflightFailure!=failure)Write(directory,"group-auto-deferred",new {time=DateTime.UtcNow,reason=failure,visibleIdentities=visible,wall=wall.UniqueId,mutationAttempted=false});
                _lastPreflightFailure=failure;
                return false;
            }
            _lastPreflightFailure=null;
            return true;
        }
        internal static void Ensure(UIApplication app,string directory)
        {
            if(!AuthorizedFixture(app,directory)){_state=null;_rebuildPending=false;_rebuildIssued=false;return;}
            var wall=SelectedOwner(app);
            if(wall==null){_state=null;_rebuildPending=false;_rebuildIssued=false;_budgetOwner=null;Reapplications.Clear();return;}
            var grid=NativePropertiesGrid.Find(app);
            if(grid==null||!grid.IsCurrent()||grid.IsEditing)return;
            if(_state!=null && OwnerSignature(app)!=_state.OwnerUniqueId)
            {
                // An old native palette can outlive the API selection event.
                if(HasNativeHeader(grid,_state.Title))return;
                _state=null;
            }
            if(_automaticStopped)throw new InvalidOperationException("Automatic custom group stopped after excessive rebuilds; restart the laboratory session explicitly.");
            if(_rebuildPending)
            {
                if(!_rebuildIssued)RequestRebuild(app,directory,_rebuildReason??"deferred recovery");
                ForgetStateForRebuild(app,directory,"verified deferred recovery");
                return;
            }
            if(_state!=null)
            {
                if(HasNativeHeader(grid,_state.Title))
                {
                    if(OwnerSignature(app)!=_state.OwnerUniqueId)return; // Wait for selection/palette synchronization.
                    _state.Grid=grid;
                    try { VerifyCheap(_state,grid); }
                    catch(Exception error) { RequestRebuild(app,directory,error.Message);throw; }
                    return;
                }
                // Only the pre-mutation capture path may wait for asynchronous
                // selection updates. A present custom layout still takes the strict
                // VerifyCheap path above, so real post-mutation failures are not hidden.
                if(!PaletteReadyForCapture(wall,grid,directory))return;
                if(!OriginalMatches(_state,grid))throw new InvalidOperationException("Native grid is neither the verified custom layout nor the verified original layout.");
                if(!ForgetStateForRebuild(app,directory,"original native rows and identities verified before reapplication"))return;
            }
            else
            {
                string file=Path.Combine(directory,"custom-title.txt");
                string title=wall is ViewPlan?"Mes paramètres de plan":File.Exists(file)?File.ReadAllText(file).Trim():"BIMaestro · Groupe personnalisé";
                if(HasNativeHeader(grid,title))return; // Never capture a stale custom header as an original baseline.
                if(!PaletteReadyForCapture(wall,grid,directory))return;
            }
            DateTime now=DateTime.UtcNow;
            // User selection changes and validated model edits are legitimate rebuilds.
            // Only repeated rebuilds of the same unchanged selection consume this fuse.
            if(_budgetOwner!=OwnerSignature(app)||_budgetRevision!=ModelRevision){Reapplications.Clear();_budgetOwner=OwnerSignature(app);_budgetRevision=ModelRevision;}
            while(Reapplications.Count>0&&(now-Reapplications.Peek()).TotalSeconds>=30)Reapplications.Dequeue();
            if(Reapplications.Count>=3)
            {
                _automaticStopped=true;
                Write(directory,"group-auto-stopped",new {time=now,reason="More than three insertion attempts within thirty seconds.",attempts=Reapplications.ToArray()});
                throw new InvalidOperationException("Automatic custom group stopped: three reconstructions within thirty seconds.");
            }
            Reapplications.Enqueue(now);
            if(AutomaticFullAudit)Write(directory,"group-auto-reapply",new {time=now,attempt=Reapplications.Count,windowSeconds=30});
            long timing=PropertiesAppearanceMetrics.Begin();
            try{Run(app,directory,"group-insert",AutomaticFullAudit);Run(app,directory,"group-members",AutomaticFullAudit);}
            finally{PropertiesAppearanceMetrics.End(AutomaticFullAudit?"group.reapply.audit":"group.reapply.interactive",timing);}
        }
        private static string OwnerSignature(UIApplication app) {var ui=app.ActiveUIDocument;var ids=ui.Selection.GetElementIds();return ids.Count==0?"view:"+ui.ActiveView.UniqueId:string.Join("|",ids.Select(id=>ui.Document.GetElement(id)?.UniqueId??"missing").OrderBy(id=>id,StringComparer.Ordinal));}
        private static State Capture(UIApplication app,NativePropertiesGrid grid,string title)
        {
            var selected=app.ActiveUIDocument.Selection.GetElementIds();
            var wall=SelectedOwner(app)??throw new InvalidOperationException("Select fixture walls or a plan view.");
            var rows=Read(grid);var mapping=new Dictionary<long,int>();var labels=new Dictionary<long,string>();
            foreach(Parameter parameter in wall.Parameters)
            {
                long id=parameter.Id.Value;int row=grid.FindParameterRow(id);
                if(row<1)continue;
                if(AppearancePreferences.Normalize(rows[row-1].Label)!=AppearancePreferences.Normalize(parameter.Definition.Name))
                    throw new InvalidOperationException("API selection and native palette are not synchronized.");
                mapping[id]=row-1;labels[id]=rows[row-1].Label;
            }
            if(mapping.Count==0)throw new InvalidOperationException("No verified native parameter identities.");
            var members=Members(wall);
            foreach(var member in members)
                if(member.IsReadOnly||!mapping.ContainsKey(member.Id.Value)||!grid.CanStyle((uint)mapping[member.Id.Value]+1))
                    throw new InvalidOperationException("Fixture member is absent or read-only: "+member.Definition.Name);
            var origins=members.Select(parameter=> {
                int nativeRow=mapping[parameter.Id.Value]+1;
                string sourceHeader=null;
                for(int index=nativeRow-2;index>=0;index--)if(NativePropertiesGrid.IsGroupMarker(rows[index].Marker)){sourceHeader=rows[index].Label;break;}
                return (object)new {id=parameter.Id.Value,name=parameter.Definition.Name,storage=parameter.StorageType.ToString(),parameter.IsShared,
                    parameterElement=wall.Document.GetElement(parameter.Id)?.GetType().Name,definitionGroupId=parameter.Definition.GetGroupTypeId().TypeId,
                    nativeRow,nativeSourceGroup=sourceHeader};
            }).ToArray();
            return new State {Grid=grid,Original=rows,Tokens=Enumerable.Range(0,rows.Count).ToList(),ParameterTokens=mapping,Labels=labels,Members=members.Select(p=>p.Id.Value).ToArray(),MemberOrigins=origins,Title=title,OwnerUniqueId=OwnerSignature(app)};
        }
        internal static void QualifyModules()
        {
            Module("DesktopMFC.dll","76FDA8B73A9E155F722FA70C14201D9F144687E11ED8EE0D629EA1DBA42C236E");
            Module("Utility.dll","E32587919DDC25B9ADF0FA978FC6A03400CE288355E08092858C869CE4D23E0A");
            Module("og1100asu.dll","19F8A8BA3BD983C42897443EEC79203DEC8F47A31B67D63BD9B0C5BC5B7F7371");
        }
        internal static void Run(UIApplication app,string directory,string command,bool audit=true)
        {
            if(app.Application.VersionNumber!="2025"||app.ActiveUIDocument==null)throw new InvalidOperationException("This experiment requires Revit 2025.");
            string fixture=Path.GetFullPath(Path.Combine(directory,"PropertiesCustomGroupFixture.rvt"));
            if(!string.Equals(Path.GetFullPath(app.ActiveUIDocument.Document.PathName),fixture,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refusing a nonfixture document.");
            if(int.Parse(File.ReadAllText(Path.Combine(directory,"target-pid.txt")).Trim())!=Process.GetCurrentProcess().Id)throw new InvalidOperationException("Wrong laboratory process.");
            var desktop=Module("DesktopMFC.dll","76FDA8B73A9E155F722FA70C14201D9F144687E11ED8EE0D629EA1DBA42C236E");
            var utility=Module("Utility.dll","E32587919DDC25B9ADF0FA978FC6A03400CE288355E08092858C869CE4D23E0A");
            var objective=Module("og1100asu.dll","19F8A8BA3BD983C42897443EEC79203DEC8F47A31B67D63BD9B0C5BC5B7F7371");
            if(command=="group-restore")
            {
                RequireIdleGrid(app);
                string before=Fingerprint(app.ActiveUIDocument.Document);
                RequestRebuild(app,directory,"explicit restore");
                if(Fingerprint(app.ActiveUIDocument.Document)!=before)throw new InvalidOperationException("Model data changed during UI restoration.");
                return;
            }
            if(command=="group-inspect")
            {
                if(_state==null)throw new InvalidOperationException("No active custom group experiment.");
                Verify(_state,directory,"inspect");return;
            }
            if(command!="group-insert"&&command!="group-members")throw new InvalidOperationException("Unknown custom group experiment command: "+command);
            var document=app.ActiveUIDocument.Document;
            if(document.IsModifiable)throw new InvalidOperationException("The model is in an active transaction.");
            long revisionBefore=ModelRevision;
            var selected=app.ActiveUIDocument.Selection.GetElementIds();
            Element focused=audit?null:SelectedOwner(app);
            if(!audit&&focused==null)throw new InvalidOperationException("Interactive validation requires one selected fixture element.");
            // Full-document snapshots remain explicit laboratory commands. The
            // interactive path checks the selected element/type plus every native
            // parameter mapping, and rejects ANY DocumentChanged event in the batch.
            string modelBefore=Fingerprint(document,focused);
            bool mutationAttempted=false;
            try
            {
                if(command=="group-insert")
                {
                    if(_state!=null)throw new InvalidOperationException("Restore the previous experiment before inserting another group.");
                    var grid=NativePropertiesGrid.Find(app)??throw new InvalidOperationException("Open the native Properties palette.");
                    if(grid.IsEditing)throw new InvalidOperationException("Finish native editing before inserting a group.");
                    string titleFile=Path.Combine(directory,"custom-title.txt");
                    string title=SelectedOwner(app) is ViewPlan?"Mes paramètres de plan":File.Exists(titleFile)?File.ReadAllText(titleFile).Trim():"BIMaestro · Groupe personnalisé";
                    if(string.IsNullOrWhiteSpace(title)||title.Length>120||title.Contains("\n")||title.Contains("\r"))throw new InvalidOperationException("Invalid laboratory group title.");
                    var state=Capture(app,grid,title);Verify(state,directory,"baseline",audit);
                    if(audit)Write(directory,"group-members-plan",new {title=state.Title,members=state.MemberOrigins});
                    var reset=Function<ResetCell>(objective.BaseAddress,"?ResetCurrentCell@CGXGridCore@@UEAAXH@Z");
                    using(grid.DeferDrawing())
                    {
                        reset(grid.Core,0);
                        if(grid.IsEditing||!grid.IsCurrent())throw new InvalidOperationException("The native editor changed before insertion.");
                        _state=state; // Retain a verified original baseline even if the first insertion fails.
                        mutationAttempted=true;
                        if(Function<InsertRows>(objective.BaseAddress,"?InsertRows@CGXGridCore@@UEAAHKKPEAHPEAVCObArray@@1KIW4GXCmdType@@@Z")
                            (grid.Core,1,2,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,0,0)==0)throw new InvalidOperationException("Native InsertRows refused the request.");
                        state.Tokens.InsertRange(0,new[]{-2,-1});Verify(state,directory,"insert",audit);
                        if(grid.IsEditing||!grid.IsCurrent())throw new InvalidOperationException("The native editor changed after insertion.");
                        IntPtr text=Marshal.StringToHGlobalUni(title),nativeString=Marshal.AllocHGlobal(IntPtr.Size);bool constructed=false;
                        try
                        {
                            // Utility's exact-build constructor writes one pointer-sized AString.
                            Function<StringConstructor>(utility.BaseAddress,"??0AString@@QEAA@PEB_W@Z")(nativeString,text);constructed=true;
                            int cursor=1;
                            Function<AddGroupHeader>(desktop.BaseAddress,"?addGroupHeader@ElemGrid@@QEAAXPEAHAEBVAString@@@Z")(ElementGrid(grid),ref cursor,nativeString);
                            if(cursor!=3)throw new InvalidOperationException("Unexpected native group header row count: "+cursor);
                        }
                        finally
                        {
                            if(constructed)Function<StringDestructor>(utility.BaseAddress,"??1AString@@QEAA@XZ")(nativeString);
                            Marshal.FreeHGlobal(nativeString);Marshal.FreeHGlobal(text);
                        }
                        Verify(state,directory,"header",audit);
                        var header=Read(grid)[1];
                        int nativeHeader=Function<NativeGroupFlag>(desktop.BaseAddress,"?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z")(ElementGrid(grid),2,1);
                        if(header.Label!=title||!NativePropertiesGrid.IsGroupMarker(header.Marker)||nativeHeader==0)
                            throw new InvalidOperationException("The generated row is not a native group header.");
                        if(grid.IsEditing||!grid.IsCurrent())throw new InvalidOperationException("The native editor changed during header creation.");
                        reset(grid.Core,0);state.HeaderReady=true;_state=state;
                    }
                }
                else
                {
                    var state=_state??throw new InvalidOperationException("Insert the native header first.");
                    if(SelectedOwner(app)==null||OwnerSignature(app)!=state.OwnerUniqueId)
                        throw new InvalidOperationException("The selected owner changed before moving group members.");
                    var current=RequireIdleGrid(app);
                    if(current.Window!=state.Grid.Window||current.Core!=state.Grid.Core)throw new InvalidOperationException("Native grid changed before moving members.");
                    state.Grid=current;
                    if(!state.HeaderReady||state.MembersReady)throw new InvalidOperationException("Invalid member movement stage.");
                    Verify(state,directory,"before-members",audit);
                    using(state.Grid.DeferDrawing())
                    {
                        for(int index=0;index<state.Members.Length;index++)
                        {
                            if(!state.Grid.IsCurrent()||state.Grid.IsEditing)throw new InvalidOperationException("Native editing started; membership movement is deferred.");
                            long id=state.Members[index];int source=state.Grid.FindParameterRow(id);int destination=3+index;
                            if(source<destination)throw new InvalidOperationException("Unexpected member position; refusing a downward move.");
                            if(source!=destination)
                            {
                                mutationAttempted=true;
                                long moveTiming=PropertiesAppearanceMetrics.Begin();
                                bool moved=state.Grid.MoveRows((uint)source,(uint)source,(uint)destination);
                                PropertiesAppearanceMetrics.End("group.native-move",moveTiming);
                                if(!moved)throw new InvalidOperationException("Native member movement refused.");
                                int token=state.Tokens[source-1];state.Tokens.RemoveAt(source-1);state.Tokens.Insert(destination-1,token);
                            }
                            Verify(state,directory,"member-"+index,audit);
                        }
                        state.MembersReady=true;LayoutRevision++;
                    }
                }
                string modelAfter=Fingerprint(document,focused);
                bool unchanged=modelBefore==modelAfter&&revisionBefore==ModelRevision;
                if(audit)Write(directory,command,new {time=DateTime.UtcNow,pass=unchanged,modelBefore,modelAfter,revisionBefore,revisionAfter=ModelRevision,headerReady=_state?.HeaderReady,membersReady=_state?.MembersReady,title=_state?.Title});
                if(!unchanged)throw new InvalidOperationException("UI experiment unexpectedly changed model parameters or produced a model transaction.");
            }
            catch(Exception error)
            {
                Write(directory,"group-error",new {time=DateTime.UtcNow,command,mutationAttempted,error=error.ToString()});
                if(mutationAttempted)RequestRebuild(app,directory,error.Message);
                throw;
            }
        }
    }
}
