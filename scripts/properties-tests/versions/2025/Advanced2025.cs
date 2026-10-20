using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using WpfControl=System.Windows.Controls.Control;
using MediaColor=System.Windows.Media.Color;

namespace BIMaestro.PropertiesTests {
 public static class Experiment {
  sealed class Saved { public DependencyObject Node; public DependencyProperty Property; public object Value; public BindingBase Binding; }
  static readonly List<Saved> saved=new List<Saved>();
  static readonly HashSet<IntPtr> hooks=new HashSet<IntPtr>();
  static readonly SubclassProc painter=Paint;
  static PropertyInfo themeProperty; static object originalTheme;
  static string mode="inspect";
  static IntPtr sourceDc,sourceBitmap,previousBitmap;
  static uint accent=0x0039520f;
  static long paints,bands; static double maxPaintMs,lastPaintMs,totalPaintMs;
  sealed class Samples {public IntPtr Dc,Bitmap,Old,Bits; public int Height;public byte[] Pixels;}
  static readonly Dictionary<IntPtr,Samples> samples=new Dictionary<IntPtr,Samples>();
  sealed class Canvas {public IntPtr Dc,Bitmap,Old,Bits;public int Width,Height;public byte[] Pixels;}
  static readonly Dictionary<IntPtr,Canvas> canvases=new Dictionary<IntPtr,Canvas>();
  static long inkPixels;
  const uint HookId=39251;
  delegate bool EnumProc(IntPtr hwnd,IntPtr argument);
  delegate IntPtr SubclassProc(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data);
  [StructLayout(LayoutKind.Sequential)]struct NativeRect {public int Left,Top,Right,Bottom;}
  [StructLayout(LayoutKind.Sequential,Pack=1)]struct Blend {public byte Operation,Flags,Alpha,Format;}
  [StructLayout(LayoutKind.Sequential)]struct BitmapHeader {public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Used,Important;}
  [StructLayout(LayoutKind.Sequential)]struct BitmapInfo {public BitmapHeader Header;public uint Color;}

  public static void Run(UIApplication app,string dir) {
   var command=File.Exists(Path.Combine(dir,"experiment.txt"))?File.ReadAllText(Path.Combine(dir,"experiment.txt")).Trim():"inspect";
   var report=new StringBuilder(); report.AppendLine("Revit "+app.Application.VersionNumber+" Command="+command);
   if(command=="restore")Restore(report);
   else if(command=="green"||command=="blue"||command=="night"||command=="ink") {
    Restore(report); mode=command;
    if(command=="night") {
     themeProperty=typeof(UIApplication).Assembly.GetType("Autodesk.Revit.UI.UIThemeManager")?.GetProperty("CurrentTheme",BindingFlags.Public|BindingFlags.Static);
     if(themeProperty!=null&&themeProperty.CanWrite) {originalTheme=themeProperty.GetValue(null,null); themeProperty.SetValue(null,Enum.Parse(themeProperty.PropertyType,"Dark"),null);report.AppendLine("Native dark theme requested; original="+originalTheme);}
    }
    bool dark=command=="night"; bool blue=command=="blue";
    var fg=Brush(dark?"#D8F2E2":blue?"#174765":"#0F5239");
    var bg=Brush(dark?"#243B30":blue?"#E6F0F8":"#E1F0E7");
    accent=blue?0x00975d20u:0x0039520fu;
    var seen=new HashSet<DependencyObject>();
    foreach(var root in Roots())Visit(root,false,seen,fg,bg);
    sourceDc=CreateCompatibleDC(IntPtr.Zero);var screen=GetDC(IntPtr.Zero);try{sourceBitmap=CreateCompatibleBitmap(screen,1,1);}finally{ReleaseDC(IntPtr.Zero,screen);}
    if(sourceDc!=IntPtr.Zero&&sourceBitmap!=IntPtr.Zero){previousBitmap=SelectObject(sourceDc,sourceBitmap);SetPixelV(sourceDc,0,0,accent);}
    foreach(var grid in Grids(app))if(SetWindowSubclass(grid,painter,new UIntPtr(HookId),UIntPtr.Zero)){hooks.Add(grid);RedrawWindow(grid,IntPtr.Zero,IntPtr.Zero,0x105);}
   }
   else if(command=="repaint")for(int i=0;i<5;i++)foreach(var h in hooks.ToArray())RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x105);
   report.AppendLine("Mode="+mode+" WPF saved="+saved.Count+" Native hooks="+hooks.Count);
   report.AppendLine("Paints="+paints+" Tinted bands="+bands+" Max overlay ms="+maxPaintMs.ToString("F3",CultureInfo.InvariantCulture)+" Last ms="+lastPaintMs.ToString("F3",CultureInfo.InvariantCulture)+" Mean ms="+(paints==0?0:totalPaintMs/paints).ToString("F3",CultureInfo.InvariantCulture));
   report.AppendLine("Tint detection: visible grayscale bands only; experimental paint overlay, not semantic parameter styles.");
   report.AppendLine("Recolored grayscale foreground pixels="+inkPixels+"; this is raster styling, not native cell font or parameter content.");
   foreach(var h in Grids(app)) {NativeRect r;GetClientRect(h,out r);report.AppendLine("Grid="+h+" "+(r.Right-r.Left)+"x"+(r.Bottom-r.Top));}
   report.AppendLine("Fingerprint="+Fingerprint(app));
   File.WriteAllText(Path.Combine(dir,"advanced2025-report.txt"),report.ToString());
  }
  static SolidColorBrush Brush(string color){var b=new SolidColorBrush((MediaColor)ColorConverter.ConvertFromString(color));b.Freeze();return b;}
  static IEnumerable<DependencyObject> Roots(){var roots=new HashSet<DependencyObject>();if(System.Windows.Application.Current!=null)foreach(System.Windows.Window w in System.Windows.Application.Current.Windows)roots.Add(w);foreach(PresentationSource s in PresentationSource.CurrentSources)if(s.RootVisual!=null)roots.Add(s.RootVisual);return roots;}
  static void Set(DependencyObject node,DependencyProperty property,object value){if(!saved.Any(s=>ReferenceEquals(s.Node,node)&&s.Property==property))saved.Add(new Saved{Node=node,Property=property,Value=node.ReadLocalValue(property),Binding=BindingOperations.GetBindingBase(node,property)});node.SetCurrentValue(property,value);}
  static void Visit(DependencyObject node,bool scope,HashSet<DependencyObject> seen,Brush fg,Brush bg){if(node==null||seen.Count>40000||!seen.Add(node))return;scope=scope||node.GetType().FullName=="UIFramework.TypeSelectorToolBarControl";var f=node as FrameworkElement;var text=node as TextBlock;bool title=text!=null&&(text.Text=="Propriétés"||text.Text=="Properties")&&f!=null&&(f.Name=="mAnchorableTitle"||f.Name=="mAnchorablerHeader");if(scope||title){if(node is WpfControl){Set(node,WpfControl.BackgroundProperty,bg);Set(node,WpfControl.ForegroundProperty,fg);}if(text!=null){Set(node,TextBlock.ForegroundProperty,fg);if(scope)Set(node,TextBlock.FontWeightProperty,FontWeights.SemiBold);}if(node is Border)Set(node,Border.BorderBrushProperty,fg);if(node is System.Windows.Controls.Panel)Set(node,System.Windows.Controls.Panel.BackgroundProperty,bg);}int n=0;try{n=VisualTreeHelper.GetChildrenCount(node);}catch{}for(int i=0;i<n;i++)Visit(VisualTreeHelper.GetChild(node,i),scope,seen,fg,bg);}
  static HashSet<IntPtr> Grids(UIApplication app){var result=new HashSet<IntPtr>();EnumProc scan=(h,p)=>{var name=new StringBuilder(256);GetClassName(h,name,256);if(name.ToString()!="GXWND")return true;for(var ancestor=GetParent(h);ancestor!=IntPtr.Zero;ancestor=GetParent(ancestor)){var title=new StringBuilder(256);GetWindowText(ancestor,title,256);if(title.ToString()=="Propriétés"||title.ToString()=="Properties"){result.Add(h);break;}}return true;};EnumChildWindows(app.MainWindowHandle,scan,IntPtr.Zero);uint current=(uint)Process.GetCurrentProcess().Id;EnumWindows((h,p)=>{uint pid;GetWindowThreadProcessId(h,out pid);if(pid==current)EnumChildWindows(h,scan,IntPtr.Zero);return true;},IntPtr.Zero);return result;}
  static bool Gray(uint pixel){if(pixel==0xffffffff)return false;int r=(int)(pixel&255),g=(int)((pixel>>8)&255),b=(int)((pixel>>16)&255);return Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<9&&((g>=195&&g<=242)||(mode=="night"&&g>=36&&g<=90));}
  static void DisposeSamples(IntPtr h){Samples s;if(!samples.TryGetValue(h,out s))return;SelectObject(s.Dc,s.Old);DeleteObject(s.Bitmap);DeleteDC(s.Dc);samples.Remove(h);}
  static void DisposeCanvas(IntPtr h){Canvas s;if(!canvases.TryGetValue(h,out s))return;SelectObject(s.Dc,s.Old);DeleteObject(s.Bitmap);DeleteDC(s.Dc);canvases.Remove(h);}
  static void RecolorForeground(IntPtr h,IntPtr dc,int width,int height,NativeRect dirty){Canvas s;if(!canvases.TryGetValue(h,out s)||s.Width!=width||s.Height!=height){DisposeCanvas(h);var info=new BitmapInfo{Header=new BitmapHeader{Size=40,Width=width,Height=-height,Planes=1,Bits=32}};s=new Canvas{Dc=CreateCompatibleDC(dc),Width=width,Height=height,Pixels=new byte[width*height*4]};s.Bitmap=CreateDIBSection(dc,ref info,0,out s.Bits,IntPtr.Zero,0);if(s.Bitmap==IntPtr.Zero){DeleteDC(s.Dc);return;}s.Old=SelectObject(s.Dc,s.Bitmap);canvases[h]=s;}BitBlt(s.Dc,0,0,width,height,dc,0,0,0x00CC0020);GdiFlush();Marshal.Copy(s.Bits,s.Pixels,0,s.Pixels.Length);int left=Math.Max(0,dirty.Left),right=Math.Min(width,dirty.Right),top=Math.Max(0,dirty.Top),bottom=Math.Min(height,dirty.Bottom);for(int y=top;y<bottom;y++)for(int x=left;x<right;x++){int i=(y*width+x)*4,b=s.Pixels[i],g=s.Pixels[i+1],r=s.Pixels[i+2];if(Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>7||g>145)continue;s.Pixels[i]=(byte)(62+(255-62)*g/255);s.Pixels[i+1]=(byte)(93+(255-93)*g/255);s.Pixels[i+2]=(byte)(22+(255-22)*g/255);inkPixels++;}Marshal.Copy(s.Pixels,0,s.Bits,s.Pixels.Length);BitBlt(dc,left,top,right-left,bottom-top,s.Dc,left,top,0x00CC0020);}
  static Samples Capture(IntPtr h,IntPtr dc,int width,int height){Samples s;if(!samples.TryGetValue(h,out s)||s.Height!=height){DisposeSamples(h);var info=new BitmapInfo{Header=new BitmapHeader{Size=40,Width=2,Height=-height,Planes=1,Bits=32}};s=new Samples{Dc=CreateCompatibleDC(dc),Height=height,Pixels=new byte[height*8]};s.Bitmap=CreateDIBSection(dc,ref info,0,out s.Bits,IntPtr.Zero,0);if(s.Bitmap==IntPtr.Zero){DeleteDC(s.Dc);throw new InvalidOperationException("DIB allocation failed");}s.Old=SelectObject(s.Dc,s.Bitmap);samples[h]=s;}BitBlt(s.Dc,0,0,1,height,dc,width-30,0,0x00CC0020);BitBlt(s.Dc,1,0,1,height,dc,width*3/4,0,0x00CC0020);GdiFlush();Marshal.Copy(s.Bits,s.Pixels,0,s.Pixels.Length);return s;}
  static uint Sample(Samples s,int x,int y){int i=y*8+x*4;return (uint)(s.Pixels[i]|s.Pixels[i+1]<<8|s.Pixels[i+2]<<16);}
  static IntPtr Paint(IntPtr h,uint msg,IntPtr wp,IntPtr lp,UIntPtr id,UIntPtr data){NativeRect dirty=new NativeRect();bool invalid=msg==0xf&&GetUpdateRect(h,out dirty,false);var result=DefSubclassProc(h,msg,wp,lp);try{if(msg==0x82){RemoveWindowSubclass(h,painter,id);hooks.Remove(h);DisposeSamples(h);DisposeCanvas(h);}else if(msg==0xf&&invalid&&sourceDc!=IntPtr.Zero){var timer=Stopwatch.StartNew();var dc=GetDC(h);if(dc==IntPtr.Zero)return result;try{NativeRect bounds;if(!GetClientRect(h,out bounds))return result;int w=bounds.Right,height=bounds.Bottom;if(w<80||height<10)return result;var sample=Capture(h,dc,w,height);if(mode=="ink")RecolorForeground(h,dc,w,height,dirty);int start=-1;for(int y=Math.Max(0,dirty.Top);y<=Math.Min(height,dirty.Bottom);y+=2){bool band=y<height&&Gray(Sample(sample,0,y))&&Gray(Sample(sample,1,y));if(band&&start<0)start=y;if((!band||y+2>=dirty.Bottom)&&start>=0){int end=band?Math.Min(y+2,dirty.Bottom):y;if(end-start>=10&&end-start<=64){AlphaBlend(dc,Math.Max(0,dirty.Left),start,Math.Min(w,dirty.Right)-Math.Max(0,dirty.Left),end-start,sourceDc,0,0,1,1,new Blend{Alpha=48});bands++;}start=-1;}}paints++;}finally{ReleaseDC(h,dc);lastPaintMs=timer.Elapsed.TotalMilliseconds;totalPaintMs+=lastPaintMs;maxPaintMs=Math.Max(maxPaintMs,lastPaintMs);}}}catch{}return result;}
  static void Restore(StringBuilder report){foreach(var h in hooks.ToArray())RemoveWindowSubclass(h,painter,new UIntPtr(HookId));var oldHooks=hooks.ToArray();hooks.Clear();foreach(var h in samples.Keys.ToArray())DisposeSamples(h);foreach(var h in canvases.Keys.ToArray())DisposeCanvas(h);for(int i=saved.Count-1;i>=0;i--){var s=saved[i];if(s.Binding!=null)BindingOperations.SetBinding(s.Node,s.Property,s.Binding);else if(s.Value==DependencyProperty.UnsetValue)s.Node.ClearValue(s.Property);else s.Node.SetValue(s.Property,s.Value);}saved.Clear();if(themeProperty!=null&&originalTheme!=null)themeProperty.SetValue(null,originalTheme,null);themeProperty=null;originalTheme=null;if(sourceDc!=IntPtr.Zero){if(previousBitmap!=IntPtr.Zero)SelectObject(sourceDc,previousBitmap);DeleteDC(sourceDc);}if(sourceBitmap!=IntPtr.Zero)DeleteObject(sourceBitmap);sourceDc=sourceBitmap=previousBitmap=IntPtr.Zero;foreach(var h in oldHooks)RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x105);mode="inspect";report.AppendLine("Restored local styles, hooks and original native theme.");}
  static string Fingerprint(UIApplication app){if(app.ActiveUIDocument==null)return "no-document";var d=app.ActiveUIDocument.Document;var elements=new List<Element>{app.ActiveUIDocument.ActiveView};elements.AddRange(new FilteredElementCollector(d).OfClass(typeof(Wall)));var lines=new List<string>();foreach(var e in elements)foreach(Parameter p in e.Parameters){string v="";try{switch(p.StorageType){case StorageType.String:v=p.AsString();break;case StorageType.Double:v=p.AsDouble().ToString("R",CultureInfo.InvariantCulture);break;case StorageType.Integer:v=p.AsInteger().ToString();break;case StorageType.ElementId:v=p.AsElementId().ToString();break;}}catch{}lines.Add(e.Id+"|"+p.Id+"|"+p.StorageType+"|"+v);}lines.Sort(StringComparer.Ordinal);using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",lines)))).Replace("-","");}
  [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr a);
  [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc p,IntPtr a);
  [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder b,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
  [DllImport("user32.dll")]static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr h,IntPtr dc);
  [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr h,out NativeRect r);
  [DllImport("user32.dll")]static extern bool GetUpdateRect(IntPtr h,out NativeRect r,bool erase);
  [DllImport("user32.dll")]static extern bool RedrawWindow(IntPtr h,IntPtr r,IntPtr region,uint flags);
  [DllImport("comctl32.dll")]static extern bool SetWindowSubclass(IntPtr h,SubclassProc p,UIntPtr id,UIntPtr data);
  [DllImport("comctl32.dll")]static extern bool RemoveWindowSubclass(IntPtr h,SubclassProc p,UIntPtr id);
  [DllImport("comctl32.dll")]static extern IntPtr DefSubclassProc(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
  [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int w,int h);
  [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
  [DllImport("gdi32.dll")]static extern bool SetPixelV(IntPtr dc,int x,int y,uint color);
  [DllImport("gdi32.dll")]static extern uint GetPixel(IntPtr dc,int x,int y);
  [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll")]static extern bool GdiFlush();
  [DllImport("gdi32.dll")]static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
  [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr dst,int x,int y,int w,int h,IntPtr src,int sx,int sy,uint rop);
  [DllImport("msimg32.dll")]static extern bool AlphaBlend(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int sw,int sh,Blend blend);
 }
}
