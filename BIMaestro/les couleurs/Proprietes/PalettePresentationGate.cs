using System;
using System.Runtime.InteropServices;
using BIMaestro.PropertiesAppearance;
namespace BIMaestro.PropertiesAppearance
{
 // Bounded presentation gate shared by elements and views. No DB API, editor changes or selection writes.
 internal sealed class PalettePresentationGate:IDisposable
 {
  delegate IntPtr Callback(IntPtr h,uint m,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
  readonly Callback callback;IntPtr window;bool held;DateTime deadline;
  [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h,Callback p,UIntPtr id,UIntPtr data);
  [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr h,Callback p,UIntPtr id);
  [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] static extern bool ValidateRect(IntPtr h,IntPtr rect);
  [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h,IntPtr rect,IntPtr region,uint flags);
  static readonly UIntPtr Id=new UIntPtr(0xB1A979);
  internal PalettePresentationGate(){callback=Message;}
  internal void Hold(NativePropertiesGrid grid)
  {
   if(grid==null||!grid.IsCurrent()||grid.IsEditing)return;
   if(window!=grid.Window){Dispose();window=grid.Window;if(!SetWindowSubclass(window,callback,Id,UIntPtr.Zero)){window=IntPtr.Zero;return;}}
   held=true;deadline=DateTime.UtcNow.AddMilliseconds(750);
   var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
   System.Threading.Tasks.Task.Delay(750).ContinueWith(_=>dispatcher.BeginInvoke(new Action(Expire)));
  }
  internal void Expire(){if(held&&DateTime.UtcNow>=deadline)Release();}
  internal void Release(){if(!held)return;held=false;if(window!=IntPtr.Zero)RedrawWindow(window,IntPtr.Zero,IntPtr.Zero,0x105);}
  IntPtr Message(IntPtr h,uint m,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data)
  {
   if(m==0x82){held=false;window=IntPtr.Zero;return DefSubclassProc(h,m,w,l);}
   // Never leave an old image in front of an interactive native editor.
   if((m>=0x100&&m<=0x109)||(m>=0x201&&m<=0x20E)||m==7)Release();
   Expire();
   if(held&&(m==0xF||m==0x14)){if(m==0xF)ValidateRect(h,IntPtr.Zero);return m==0x14?new IntPtr(1):IntPtr.Zero;}
   return DefSubclassProc(h,m,w,l);
  }
  public void Dispose(){Release();if(window!=IntPtr.Zero)RemoveWindowSubclass(window,callback,Id);window=IntPtr.Zero;}
 }
}
