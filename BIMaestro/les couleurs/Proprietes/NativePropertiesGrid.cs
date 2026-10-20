using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.UI;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed class NativePropertiesGrid
    {
        internal IntPtr Window, Core;
        private IntPtr _object, _coreVtable;
        internal uint RowCount;
        private static IntPtr _og, _mfc;
        private static bool _checked;
        private static string _failure;
        private static FromHandle _from;
        private static Count _rows, _columns;
        private static Cell _value, _style;
        private static StyleFlag _readOnly, _enabled;
        private static StyleColor _text;
        private static CellRect _rect;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FromHandle(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Count(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Cell(IntPtr p, uint r, uint c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StyleFlag(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StyleColor(IntPtr p);
        // MSVC x64 member return: this in RCX, CRect return storage in RDX.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CellRect(IntPtr p, ref Rect rect, uint row, uint col);
        internal delegate bool EnumProc(IntPtr h, IntPtr p);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Mbi { public IntPtr Base, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public UIntPtr Size; public uint State, Protect, Type; }
        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr p, out Mbi m, UIntPtr size);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr a);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr a);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetSysColor(int index);

        private static T Function<T>(string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(_og, name);
            if (p == IntPtr.Zero) throw new NotSupportedException("Export natif absent : " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        private static void Initialize()
        {
            if (_checked) { if (_failure != null) throw new NotSupportedException(_failure); return; }
            _checked = true;
            try
            {
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();
                var mfc = modules.Single(m => m.ModuleName.Equals("mfc140u.dll", StringComparison.OrdinalIgnoreCase));
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(mfc.FileName))).Replace("-", "");
                if (hash != "FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC")
                    throw new NotSupportedException("Cette bibliothèque Revit n’a pas encore été validée. Les préférences sont conservées, sans modifier la palette.");
                _mfc = mfc.BaseAddress;
                _og = modules.Single(m => m.ModuleName.Equals("og1100asu.dll", StringComparison.OrdinalIgnoreCase)).BaseAddress;
                _from = Marshal.GetDelegateForFunctionPointer<FromHandle>(IntPtr.Add(_mfc, 0x2957c0));
                _rows = Function<Count>("?GetRowCount@CGXGridCore@@UEAAKXZ");
                _columns = Function<Count>("?GetColCount@CGXGridCore@@UEAAKXZ");
                _value = Function<Cell>("?GetValueRowCol@CGXGridCore@@QEAAAEBV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@KK@Z");
                _style = Function<Cell>("?LookupStyleRowCol@CGXGridCore@@UEAAAEBVCGXStyle@@KK@Z");
                _readOnly = Function<StyleFlag>("?GetReadOnly@CGXStyle@@QEBAHXZ");
                _enabled = Function<StyleFlag>("?GetEnabled@CGXStyle@@QEBAHXZ");
                _text = Function<StyleColor>("?GetTextColor@CGXStyle@@QEBAKXZ");
                _rect = Function<CellRect>("?CalcRectFromRowColEx@CGXGridCore@@QEAA?AVCRect@@KK@Z");
            }
            catch (Exception ex) { _failure = ex.Message; throw; }
        }

        internal static NativePropertiesGrid Find(UIApplication app)
        {
            Initialize();
            IntPtr found = IntPtr.Zero;
            EnumProc visit = (h, a) =>
            {
                var c = new StringBuilder(100); GetClassName(h, c, 100);
                if (c.ToString() != "GXWND" || !IsWindowVisible(h)) return true;
                for (var p = GetParent(h); p != IntPtr.Zero; p = GetParent(p))
                {
                    var t = new StringBuilder(100); GetWindowText(p, t, 100);
                    if (t.ToString() == "Propriétés" || t.ToString() == "Properties") { found = h; return false; }
                }
                return true;
            };
            EnumChildWindows(app.MainWindowHandle, visit, IntPtr.Zero);
            if (found == IntPtr.Zero)
                EnumWindows((h, a) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == (uint)Process.GetCurrentProcess().Id) EnumChildWindows(h, visit, IntPtr.Zero); return found == IntPtr.Zero; }, IntPtr.Zero);
            if (found == IntPtr.Zero) return null;
            IntPtr obj = _from(found); if (obj == IntPtr.Zero) return null;
            var result = new NativePropertiesGrid { Window = found, Core = GetCore(obj) };
            result._object = obj; result._coreVtable = Ptr(result.Core);
            result.RowCount = _rows(result.Core);
            uint cols = _columns(result.Core);
            if (result.RowCount > 2000 || cols < 3 || cols > 10) throw new NotSupportedException("Grille de propriétés non reconnue.");
            return result;
        }

        internal bool IsCurrent() => _from(Window) == _object && Ptr(Core) == _coreVtable;
        internal string Value(uint row, uint col) => Wide(Ptr(_value(Core, row, col)));
        internal Rect Bounds(uint row, uint col)
        {
            var rect = new Rect(); _rect(Core, ref rect, row, col);
            if (Math.Abs((long)rect.Left) > 100000 || Math.Abs((long)rect.Top) > 100000 || rect.Right < rect.Left || rect.Bottom < rect.Top)
                throw new NotSupportedException("Coordonnées de cellule non reconnues.");
            return rect;
        }
        internal bool CanStyle(uint row)
        {
            IntPtr s = _style(Core, row, 2);
            return s != IntPtr.Zero && _enabled(s) != 0 && _readOnly(s) == 0;
        }
        internal uint TextColor(uint row, uint col)
        {
            uint color = _text(_style(Core, row, col));
            return (color & 0x80000000) != 0 ? GetSysColor((int)(color & 255)) : color;
        }

        // Type offsets come from the live RTTI, never from an assumed C++ layout.
        private static IntPtr GetCore(IntPtr obj)
        {
            var vtable = Ptr(obj); var col = Ptr(IntPtr.Subtract(vtable, 8));
            if (Int(col) != 1) throw new NotSupportedException("RTTI natif non reconnu.");
            var image = IntPtr.Subtract(col, Int(col, 20));
            if (!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.BaseAddress == image)) throw new NotSupportedException("Module RTTI inconnu.");
            int offset = Int(col, 4); if (offset < 0 || offset > 8192) throw new NotSupportedException("Objet natif non reconnu.");
            var complete = IntPtr.Subtract(obj, offset); var hierarchy = IntPtr.Add(image, Int(col, 16));
            int count = Int(hierarchy, 8); if (count < 1 || count > 200) throw new NotSupportedException("Hiérarchie native non reconnue.");
            var array = IntPtr.Add(image, Int(hierarchy, 12)); var matches = new HashSet<IntPtr>();
            for (int i = 0; i < count; i++)
            {
                var desc = IntPtr.Add(image, Int(array, i * 4));
                string name = Ansi(IntPtr.Add(image, Int(desc) + 16));
                int md = Int(desc, 8);
                if (name == ".?AVCGXGridCore@@" && Int(desc, 12) == -1 && md >= 0 && md < 8192) matches.Add(IntPtr.Add(complete, md));
            }
            if (matches.Count != 1) throw new NotSupportedException("Instance de grille ambiguë.");
            var core = matches.Single(); Ptr(core); return core;
        }
        private static int Available(IntPtr p)
        {
            Mbi m; if (p == IntPtr.Zero || VirtualQuery(p, out m, new UIntPtr((uint)Marshal.SizeOf(typeof(Mbi)))) == UIntPtr.Zero || m.State != 0x1000 || (m.Protect & 0x101) != 0) return 0;
            long n = m.Base.ToInt64() + (long)m.Size.ToUInt64() - p.ToInt64(); return (int)Math.Min(int.MaxValue, Math.Max(0, n));
        }
        private static IntPtr Ptr(IntPtr p) { if (Available(p) < 8) throw new NotSupportedException("Objet natif indisponible."); return Marshal.ReadIntPtr(p); }
        private static int Int(IntPtr p, int offset = 0) { p = IntPtr.Add(p, offset); if (Available(p) < 4) throw new NotSupportedException("Métadonnées natives indisponibles."); return Marshal.ReadInt32(p); }
        private static string Ansi(IntPtr p) { int n = Math.Min(300, Available(p)); if (n == 0) throw new NotSupportedException("Type natif indisponible."); string s = Marshal.PtrToStringAnsi(p, n); int end = s.IndexOf('\0'); return end < 0 ? s : s.Substring(0, end); }
        private static string Wide(IntPtr p) { int n = Math.Min(1024, Available(p) / 2); if (n == 0) return ""; string s = Marshal.PtrToStringUni(p, n); int end = s.IndexOf('\0'); return end < 0 ? s : s.Substring(0, end); }
    }
}
