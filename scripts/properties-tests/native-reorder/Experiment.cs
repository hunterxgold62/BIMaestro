using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
namespace BIMaestro.PropertiesTests {
 public static class Experiment {
  const string MfcHash="FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC";
  const int FromHandleRva=0x2957c0;
  delegate bool EnumProc(IntPtr h,IntPtr arg);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr FromHandle(IntPtr h);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate uint Count(IntPtr p);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr Cell(IntPtr p,uint row,uint col);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Move(IntPtr p,uint from,uint to,uint dest,uint flags,int cmd);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void Action0(IntPtr p);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate void Top(IntPtr p,uint row,uint flags,int hint);
  [StructLayout(LayoutKind.Sequential)]struct Mbi {public IntPtr Base,AllocationBase;public uint AllocationProtect;public ushort PartitionId;public UIntPtr Size;public uint State,Protect,Type;}
  [DllImport("kernel32.dll")]static extern UIntPtr VirtualQuery(IntPtr p,out Mbi info,UIntPtr size);
  [DllImport("kernel32.dll",CharSet=CharSet.Ansi)]static extern IntPtr GetProcAddress(IntPtr h,string s);
  [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,EnumProc proc,IntPtr arg);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int max);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int max);
  [DllImport("user32.dll")]static extern bool RedrawWindow(IntPtr h,IntPtr r,IntPtr region,uint flags);
  [DllImport("user32.dll")]static extern IntPtr GetParent(IntPtr h);
  static StringBuilder log;static string logfile;
  static void L(string s){log.AppendLine(s);File.WriteAllText(logfile,log.ToString());}
  static bool Readable(IntPtr p,int n){Mbi m;if(p==IntPtr.Zero||VirtualQuery(p,out m,new UIntPtr((uint)Marshal.SizeOf(typeof(Mbi))))==UIntPtr.Zero)return false;return m.State==0x1000&&(m.Protect&0x101)==0&&p.ToInt64()>=m.Base.ToInt64()&&p.ToInt64()+n<=m.Base.ToInt64()+(long)m.Size.ToUInt64();}
  static IntPtr Ptr(IntPtr p){if(!Readable(p,8))throw new Exception("Unreadable pointer");return Marshal.ReadIntPtr(p);}
  static int I32(IntPtr p,int offset=0){p=IntPtr.Add(p,offset);if(!Readable(p,4))throw new Exception("Unreadable integer");return Marshal.ReadInt32(p);}
  static string Ansi(IntPtr p){var b=new List<byte>();for(int i=0;i<300;i++){var q=IntPtr.Add(p,i);if(!Readable(q,1))throw new Exception("Unreadable type name");byte c=Marshal.ReadByte(q);if(c==0)return Encoding.ASCII.GetString(b.ToArray());b.Add(c);}throw new Exception("Unterminated type name");}
  static string Wide(IntPtr p){var b=new StringBuilder();for(int i=0;i<1024;i++){var q=IntPtr.Add(p,i*2);if(!Readable(q,2))return "<unreadable>";char c=(char)Marshal.ReadInt16(q);if(c==0)return b.ToString();b.Append(c);}return b+"…";}
  static T Fn<T>(IntPtr module,string name) where T:Delegate {var p=GetProcAddress(module,name);if(p==IntPtr.Zero)throw new Exception("Missing export "+name);return Marshal.GetDelegateForFunctionPointer<T>(p);}
  static IntPtr Core(IntPtr obj) {
   var vt=Ptr(obj);var col=Ptr(IntPtr.Subtract(vt,8));if(I32(col)!=1)throw new Exception("Expected x64 RTTI COL signature 1");
   int self=I32(col,20);var image=IntPtr.Subtract(col,self);var module=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().SingleOrDefault(m=>m.BaseAddress==image);if(module==null)throw new Exception("RTTI image not a loaded module");
   int offset=I32(col,4);if(offset<0||offset>8192)throw new Exception("Unexpected complete object offset");var complete=IntPtr.Subtract(obj,offset);
   string name=Ansi(IntPtr.Add(image,I32(col,12)+16));L("RTTI object="+name+" image="+module.ModuleName+" offset="+offset);
   var hierarchy=IntPtr.Add(image,I32(col,16));int count=I32(hierarchy,8);if(count<1||count>200)throw new Exception("Unexpected base class count");var array=IntPtr.Add(image,I32(hierarchy,12));var candidates=new List<IntPtr>();
   for(int i=0;i<count;i++){var desc=IntPtr.Add(image,I32(array,i*4));string bn=Ansi(IntPtr.Add(image,I32(desc)+16));int md=I32(desc,8),pd=I32(desc,12),vd=I32(desc,16);L("BASE "+bn+" mdisp="+md+" pdisp="+pd+" vdisp="+vd);if(bn==".?AVCGXGridCore@@"&&pd==-1&&md>=0&&md<8192)candidates.Add(IntPtr.Add(complete,md));}
   if(candidates.Distinct().Count()!=1)throw new Exception("No unique nonvirtual CGXGridCore base");var core=candidates[0];var cvt=Ptr(core);if(!Readable(cvt,8))throw new Exception("Invalid core vtable");return core;
  }
  public static void Run(UIApplication app,string dir){
   int expectedPid=int.Parse(File.ReadAllText(Path.Combine(dir,"target-pid.txt")).Trim());if(Process.GetCurrentProcess().Id!=expectedPid){string retry=Path.Combine(dir,"route-retries.txt");int n=File.Exists(retry)?int.Parse(File.ReadAllText(retry)):0;if(n<12){File.WriteAllText(retry,(n+1).ToString());File.WriteAllText(Path.Combine(dir,"control.txt"),"run "+System.Reflection.Assembly.GetExecutingAssembly().Location);}return;}
   log=new StringBuilder();logfile=Path.Combine(dir,"native-reorder-report.txt");string command=File.Exists(Path.Combine(dir,"experiment.txt"))?File.ReadAllText(Path.Combine(dir,"experiment.txt")).Trim():"inspect";
   L("Native reorder lab Revit="+app.Application.VersionNumber+" command="+command+" time="+DateTime.Now.ToString("O")+" PID="+Process.GetCurrentProcess().Id+" MainHWND="+app.MainWindowHandle);
   try{
    if(app.Application.VersionNumber!="2025"||app.ActiveUIDocument==null)throw new Exception("Requires Revit 2025 with disposable fixture");
    var doc=app.ActiveUIDocument.Document;L("Document="+doc.PathName);if(!string.Equals(Path.GetFullPath(doc.PathName),Path.GetFullPath(Path.Combine(dir,"PropertiesFixture.rvt")),StringComparison.OrdinalIgnoreCase))throw new Exception("Refusing nonfixture document");
    L("Fingerprint before="+Fingerprint(app));var mods=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();var mfc=mods.Single(m=>m.ModuleName.Equals("mfc140u.dll",StringComparison.OrdinalIgnoreCase));string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(mfc.FileName))).Replace("-","");L("MFC="+mfc.FileName+" SHA256="+hash);if(hash!=MfcHash)throw new Exception("MFC binary does not match public-symbol RVA");
    var from=Marshal.GetDelegateForFunctionPointer<FromHandle>(IntPtr.Add(mfc.BaseAddress,FromHandleRva));var og=mods.Single(m=>m.ModuleName=="og1100asu.dll");var rows=Fn<Count>(og.BaseAddress,"?GetRowCount@CGXGridCore@@UEAAKXZ");var cols=Fn<Count>(og.BaseAddress,"?GetColCount@CGXGridCore@@UEAAKXZ");var value=Fn<Cell>(og.BaseAddress,"?GetValueRowCol@CGXGridCore@@QEAAAEBV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@KK@Z");
    var grids=new HashSet<IntPtr>();EnumChildWindows(app.MainWindowHandle,(h,a)=>{var s=new StringBuilder(100);GetClassName(h,s,100);if(s.ToString()!="GXWND")return true;for(var p=GetParent(h);p!=IntPtr.Zero;p=GetParent(p)){var t=new StringBuilder(100);GetWindowText(p,t,100);if(t.ToString()=="Propriétés"||t.ToString()=="Properties"){grids.Add(h);break;}}return true;},IntPtr.Zero);
    foreach(var h in grids){L("GRID HWND="+h);var obj=from(h);L("Permanent CWnd="+obj);if(obj==IntPtr.Zero)continue;var core=Core(obj);L("Verified core="+core);uint nr=rows(core),nc=cols(core);L("Dimensions="+nr+"x"+nc);if(nr>500||nc>10)throw new Exception("Dimensions outside fixture bounds");var before=Snapshot(core,value,nr,nc,"ROW");
     if(command=="top"||command=="move"||command=="move-enabled"||command=="restore") {
      int underlay=before.FindIndex(a=>a.Length>3&&a[1].Trim()=="Niveau en fond de plan");int extents=before.FindIndex(a=>a.Length>3&&a[1].Trim()=="Etendues");
      if(underlay<1||extents<1)throw new Exception("Expected view groups not found; no move");int destination=Math.Min(underlay,extents);if(command=="restore"){if(underlay<extents){L("Already restored");continue;}int t=underlay;underlay=extents;extents=t;}else if(command!="top"&&extents<underlay)throw new Exception("Already moved; refusing duplicate move");int end=extents+1;while(end<before.Count&&before[end][3].Trim()!="^")end++;end--;
      L("Identified block="+extents+".."+end+" destination="+underlay);
      if(command!="top") {
       if(command=="move-enabled"){L("Installing native MoveRowsCols component in this disposable session");Fn<Action0>(og.BaseAddress,"?ImplementMoveRowsCols@CGXGridCore@@QEAAXXZ")(core);}
       L("Calling native MoveRows; flags=0 explicit redraw; commandType=0; complete group block");
       int ok=Fn<Move>(og.BaseAddress,"?MoveRows@CGXGridCore@@UEAAHKKKIW4GXCmdType@@@Z")(core,(uint)extents,(uint)end,(uint)underlay,0,0);L("MoveRows returned="+ok);
       var after=Snapshot(core,value,nr,nc,"AFTER");var block=before.GetRange(extents,end-extents+1);var expected=new List<string[]>(before);expected.RemoveRange(extents,end-extents+1);expected.InsertRange(underlay,block);
       L("Expected complete block permutation="+Same(expected,after));L("Rows unchanged="+Same(before,after));
      }
      Fn<Top>(og.BaseAddress,"?SetTopRow@CGXGridCore@@UEAAXKIH@Z")(core,(uint)Math.Max(1,destination-2),0,0);RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x105);
     }
    }
    L("Fingerprint after="+Fingerprint(app));L("Native command complete; document modified="+doc.IsModified);
   }catch(Exception ex){L("ERROR "+ex);}
  }
  static List<string[]> Snapshot(IntPtr core,Cell value,uint nr,uint nc,string prefix){var result=new List<string[]>();for(uint r=0;r<=nr;r++){var texts=new List<string>();for(uint c=0;c<=nc;c++){var str=value(core,r,c);texts.Add(str==IntPtr.Zero?"<null>":Wide(Ptr(str)));}result.Add(texts.ToArray());L(prefix+" "+r+" | "+string.Join(" | ",texts));}return result;}
  static bool Same(List<string[]> a,List<string[]> b){return a.Count==b.Count&&a.Select(x=>string.Join("\t",x.Skip(1))).SequenceEqual(b.Select(x=>string.Join("\t",x.Skip(1))));}
  static string Fingerprint(UIApplication a){var d=a.ActiveUIDocument.Document;var es=new List<Element>{a.ActiveUIDocument.ActiveView};es.AddRange(new FilteredElementCollector(d).OfClass(typeof(Wall)));var lines=new List<string>();foreach(var e in es)foreach(Parameter p in e.Parameters){string v="";try{switch(p.StorageType){case StorageType.String:v=p.AsString();break;case StorageType.Double:v=p.AsDouble().ToString("R",System.Globalization.CultureInfo.InvariantCulture);break;case StorageType.Integer:v=p.AsInteger().ToString();break;case StorageType.ElementId:v=p.AsElementId().ToString();break;}}catch{}lines.Add(e.Id+"|"+p.Id+"|"+p.StorageType+"|"+v);}lines.Sort(StringComparer.Ordinal);using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",lines)))).Replace("-","");}
 }
}
