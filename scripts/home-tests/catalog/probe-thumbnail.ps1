param([Parameter(Mandatory=$true)][string]$ModelPath,[Parameter(Mandatory=$true)][string]$OutputPath)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
public static class HomeThumbnailProbe {
 [StructLayout(LayoutKind.Sequential)] struct Size { public int x,y; }
 [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface Factory { void GetImage(Size size,uint flags,out IntPtr bitmap); }
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)] static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out Factory factory);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
 public static string Probe(string path,string output) {
  Factory factory=null; IntPtr bitmap=IntPtr.Zero;
  try { var iid=new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"); SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out factory); factory.GetImage(new Size{x=128,y=128},0x09,out bitmap); using(var image=Image.FromHbitmap(bitmap)) { image.Save(output,ImageFormat.Png); return image.Width+"x"+image.Height; } }
  finally { if(bitmap!=IntPtr.Zero)DeleteObject(bitmap); if(factory!=null)Marshal.ReleaseComObject(factory); }
 }
}
'@
$timer=[Diagnostics.Stopwatch]::StartNew()
try { $dimensions=[HomeThumbnailProbe]::Probe($ModelPath,$OutputPath); @{ok=$true;dimensions=$dimensions;elapsedMs=$timer.Elapsed.TotalMilliseconds}|ConvertTo-Json -Compress }
catch { @{ok=$false;error=$_.Exception.Message;elapsedMs=$timer.Elapsed.TotalMilliseconds}|ConvertTo-Json -Compress }
