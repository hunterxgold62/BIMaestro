using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Globalization;
using Autodesk.Revit.DB;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Autodesk.Revit.UI;
namespace BIMaestro.PropertiesTests {
 public static class Experiment {
  sealed class Saved {public DependencyObject O; public DependencyProperty P; public object Local; public BindingBase Binding;}
  static readonly List<Saved> saved=new List<Saved>();
  static void Fingerprint(UIApplication app,string dir,StringBuilder report){if(app.ActiveUIDocument==null)return;var doc=app.ActiveUIDocument.Document;var elements=new List<Element>{app.ActiveUIDocument.ActiveView};elements.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Element>());var lines=new List<string>();foreach(var e in elements)foreach(Parameter p in e.Parameters){string value;try{switch(p.StorageType){case StorageType.String:value=p.AsString()??"<null>";break;case StorageType.Integer:value=p.AsInteger().ToString(CultureInfo.InvariantCulture);break;case StorageType.Double:value=p.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;case StorageType.ElementId:value=p.AsElementId().ToString();break;default:value="<none>";break;}}catch(Exception ex){value="<error:"+ex.GetType().Name+">";}lines.Add(e.Id+"|"+p.Id+"|"+p.Definition.Name+"|"+p.StorageType+"|"+value);}lines.Sort(StringComparer.Ordinal);string data=string.Join("\n",lines);string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(data))).Replace("-","");report.AppendLine("Parameter fingerprint SHA256="+hash+" Entries="+lines.Count);File.WriteAllText(Path.Combine(dir,"parameter-fingerprint.txt"),hash+Environment.NewLine+data);}
  static readonly HashSet<IntPtr> hooks=new HashSet<IntPtr>();
  static readonly SubclassProc callback=Paint;
  const uint HookId=29143;
  delegate IntPtr SubclassProc(IntPtr h,uint msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data);
  delegate bool EnumProc(IntPtr h,IntPtr p);
  [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
  static IntPtr Paint(IntPtr h,uint msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data){var result=DefSubclassProc(h,msg,wp,lp);try{if(msg==0xF){IntPtr dc=GetDC(h);if(dc==IntPtr.Zero)return result;try{Rect r;if(GetClientRect(h,out r)){IntPtr brush=CreateSolidBrush(0x0039520F);try{FrameRect(dc,ref r,brush);}finally{DeleteObject(brush);}}}finally{ReleaseDC(h,dc);}}else if(msg==0x82){RemoveWindowSubclass(h,callback,new UIntPtr(HookId));hooks.Remove(h);}}catch{}return result;}
  static void Native(UIApplication app,string command,StringBuilder b){if(command=="style"){EnumChildWindows(app.MainWindowHandle,(h,p)=>{var cls=new StringBuilder(256);GetClassName(h,cls,256);if(cls.ToString()!="GXWND")return true;bool inPalette=false;for(IntPtr parent=GetParent(h);parent!=IntPtr.Zero;parent=GetParent(parent)){var title=new StringBuilder(128);GetWindowText(parent,title,128);if(title.ToString()=="Propriétés"||title.ToString()=="Properties"){inPalette=true;break;}}if(inPalette&&!hooks.Contains(h)&&SetWindowSubclass(h,callback,new UIntPtr(HookId),UIntPtr.Zero)){hooks.Add(h);RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x101);b.AppendLine("Native grid border="+h);}return true;},IntPtr.Zero);}else if(command=="restore"){foreach(var h in hooks.ToArray()){RemoveWindowSubclass(h,callback,new UIntPtr(HookId));RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x105);}hooks.Clear();}b.AppendLine("Native border hooks="+hooks.Count);}
  [DllImport("comctl32.dll")]static extern bool SetWindowSubclass(IntPtr h,SubclassProc p,UIntPtr id,UIntPtr data);
  [DllImport("comctl32.dll")]static extern bool RemoveWindowSubclass(IntPtr h,SubclassProc p,UIntPtr id);
  [DllImport("comctl32.dll")]static extern IntPtr DefSubclassProc(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
  [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr lp);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")]static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr h,IntPtr dc);
  [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")]static extern int FrameRect(IntPtr dc,ref Rect r,IntPtr brush);
  [DllImport("user32.dll")]static extern bool RedrawWindow(IntPtr h,IntPtr r,IntPtr region,uint flags);
  [DllImport("gdi32.dll")]static extern IntPtr CreateSolidBrush(uint color);
  [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr o);
  static bool Relevant(DependencyObject o){string s=o.GetType().FullName;return s=="UIFramework.TypeSelectorToolBarControl";}
  public static void Run(UIApplication app,string dir){string command=File.Exists(Path.Combine(dir,"experiment.txt"))?File.ReadAllText(Path.Combine(dir,"experiment.txt")).Trim():"inspect";var b=new StringBuilder();b.AppendLine(DateTime.Now+" Command="+command);var seen=new HashSet<DependencyObject>();var roots=new List<DependencyObject>();if(System.Windows.Application.Current!=null)foreach(System.Windows.Window w in System.Windows.Application.Current.Windows)roots.Add(w);foreach(PresentationSource source in PresentationSource.CurrentSources)if(source.RootVisual!=null)roots.Add(source.RootVisual);foreach(var root in roots)Visit(root,0,false,seen,b,command);foreach(var a in AppDomain.CurrentDomain.GetAssemblies()){Type t=a.GetType("ManagePropertiesPalette",false);if(t!=null){b.AppendLine("MANAGER "+t.AssemblyQualifiedName);foreach(var m in t.GetMembers(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))b.AppendLine(m.MemberType+" "+m);}}if(command=="restore"){for(int i=saved.Count-1;i>=0;i--){var x=saved[i];try{if(x.Binding!=null)BindingOperations.SetBinding(x.O,x.P,x.Binding);else if(x.Local==DependencyProperty.UnsetValue)x.O.ClearValue(x.P);else x.O.SetValue(x.P,x.Local);}catch(Exception ex){b.AppendLine("RESTORE ERROR "+ex.Message);}}saved.Clear();}Native(app,command,b);b.AppendLine("Saved property count="+saved.Count);Fingerprint(app,dir,b);File.WriteAllText(Path.Combine(dir,"experiment-report.txt"),b.ToString());}
  static void Set(DependencyObject o,DependencyProperty p,object value){if(saved.Any(x=>ReferenceEquals(x.O,o)&&x.P==p))return;saved.Add(new Saved{O=o,P=p,Local=o.ReadLocalValue(p),Binding=BindingOperations.GetBindingBase(o,p)});o.SetCurrentValue(p,value);}
  static readonly SolidColorBrush Green=new SolidColorBrush(System.Windows.Media.Color.FromRgb(15,82,57));
  static readonly SolidColorBrush Mint=new SolidColorBrush(System.Windows.Media.Color.FromRgb(225,240,231));
  static void Visit(DependencyObject o,int depth,bool scope,HashSet<DependencyObject> seen,StringBuilder b,string command){if(o==null||depth>65||!seen.Add(o))return;scope=scope||Relevant(o);var f=o as FrameworkElement;string text=o is TextBlock?((TextBlock)o).Text:"";bool title=(text=="Propriétés"||text=="Properties")&&f!=null&&(f.Name=="mAnchorableTitle"||f.Name=="mAnchorablerHeader");if(scope||title){b.Append(' ',Math.Min(depth,30)).Append(o.GetType().FullName).Append(" Name=").Append(f==null?"":f.Name).Append(" Text=").Append(text).AppendLine();if(command=="style"){if(o is System.Windows.Controls.Control){Set(o,System.Windows.Controls.Control.ForegroundProperty,Green);Set(o,System.Windows.Controls.Control.BackgroundProperty,Mint);}if(o is TextBlock)Set(o,TextBlock.ForegroundProperty,Green);if(o is Border){Set(o,Border.BorderBrushProperty,Green);Set(o,Border.BorderThicknessProperty,new Thickness(1));}if(o is System.Windows.Controls.Panel)Set(o,System.Windows.Controls.Panel.BackgroundProperty,Mint);}}int n=0;try{n=VisualTreeHelper.GetChildrenCount(o);}catch{}for(int i=0;i<n;i++)Visit(VisualTreeHelper.GetChild(o,i),depth+1,scope,seen,b,command);}
 }
}
