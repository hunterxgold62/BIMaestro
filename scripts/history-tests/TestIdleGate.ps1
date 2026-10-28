$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs'))
$start = $source.IndexOf('    internal static class HistoryBackgroundWork')
$end = $source.IndexOf('    internal sealed class HistoryNativeArtifact', $start)
$gate = $source.Substring($start, $end - $start)
# Exercise the production decisions with deterministic native focus snapshots.
$gate = [regex]::Replace($gate, '\[DllImport\("user32.dll"[^\]]*\)\]\s*(\[return: MarshalAs\(UnmanagedType.Bool\)\]\s*)?private static extern bool GetGUIThreadInfo\(uint threadId, ref GuiThreadInfo info\);', 'private static bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info) { info.Caret = TestCaret ? new IntPtr(1) : IntPtr.Zero; return TestGuiAvailable; }')
$gate = [regex]::Replace($gate, '\[DllImport\("user32.dll"[^\]]*\)\]\s*private static extern int GetClassName\(IntPtr window, System.Text.StringBuilder name, int capacity\);', 'private static int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity) { name.Append(TestClass); return TestClass.Length; }')
$gate = [regex]::Replace($gate, '\[DllImport\("user32.dll"\)\]\s*private static extern IntPtr GetForegroundWindow\(\);', 'internal static bool TestCaret, TestGuiAvailable = true, TestForeground = true; internal static string TestClass = "Grid"; private static IntPtr GetForegroundWindow() => new IntPtr(1);')
$gate = [regex]::Replace($gate, '\[DllImport\("user32.dll"\)\]\s*private static extern uint GetWindowThreadProcessId\(IntPtr window, out uint processId\);', 'private static uint GetWindowThreadProcessId(IntPtr window, out uint processId) { processId = TestForeground ? ProcessId : 0; return 1; }')
$wpfStubs = @'
namespace System.Windows.Input { internal static class Keyboard { internal static object FocusedElement; } }
namespace System.Windows.Controls.Primitives { internal class TextBoxBase {} }
namespace System.Windows.Controls { internal class PasswordBox {} internal class ComboBox { internal bool IsEditable; } }
'@
$checks = @'
public static class IdleGateChecks {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() {
        Check(!HistoryBackgroundWork.IsTextInputActive(), "Grid focus permits background work");
        HistoryBackgroundWork.SelectionRequested = true;
        HistoryBackgroundWork.TestCaret = true;
        Check(HistoryBackgroundWork.IsTextInputActive(), "Native caret protects ribbon editing even between keystrokes");
        Check(!HistoryBackgroundWork.SelectionCanRun(), "Selection priority cannot interrupt a native edit");
        Check(!HistoryBackgroundWork.InventoryCanRun(), "Inventory cannot interrupt a native edit");
        Check(!HistoryBackgroundWork.UserIsIdle(), "Long typing pauses must still protect the edit");
        Check(!HistoryBackgroundWork.PreparationCanRun(), "Preparation cannot interrupt a native edit");
        HistoryBackgroundWork.TestCaret = false;
        HistoryBackgroundWork.TestClass = "WindowsForms10.EDIT.app";
        Check(HistoryBackgroundWork.IsTextInputActive(), "Native edit remains protected when caret is unavailable");
        HistoryBackgroundWork.TestClass = "HwndWrapper";
        System.Windows.Input.Keyboard.FocusedElement = new System.Windows.Controls.Primitives.TextBoxBase();
        Check(HistoryBackgroundWork.IsTextInputActive(), "WPF ribbon textbox is protected");
        System.Windows.Input.Keyboard.FocusedElement = new System.Windows.Controls.ComboBox { IsEditable = true };
        Check(HistoryBackgroundWork.IsTextInputActive(), "Editable ribbon combo is protected");
        System.Windows.Input.Keyboard.FocusedElement = new System.Windows.Controls.ComboBox();
        Check(!HistoryBackgroundWork.IsTextInputActive(), "Noneditable combo permits background work");
        System.Windows.Input.Keyboard.FocusedElement = null;
        HistoryBackgroundWork.TestGuiAvailable = false;
        Check(HistoryBackgroundWork.IsTextInputActive(), "Unknown foreground input state defers work");
        HistoryBackgroundWork.TestGuiAvailable = true;
        HistoryBackgroundWork.TestCaret = true;
        HistoryBackgroundWork.TestForeground = false;
        Check(!HistoryBackgroundWork.IsTextInputActive(), "Other applications do not block Revit inventory");
        HistoryBackgroundWork.TestForeground = true;
        HistoryBackgroundWork.TestCaret = false;
        Check(HistoryBackgroundWork.SelectionCanRun(), "Pending selection resumes after leaving the editor");
        HistoryBackgroundWork.SelectionRequested = true;
        Check(HistoryBackgroundWork.CanStartSelection(true, true, false), "Explicit selection starts without idle delay");
        Check(!HistoryBackgroundWork.CanStartSelection(true, true, true), "Stop blocks explicit selection");
        Check(!HistoryBackgroundWork.CanStartSelection(false, true, false), "Explicit selection pauses outside Revit");
        Check(!HistoryBackgroundWork.CanStartSelection(true, false, false), "Background work cannot borrow the selection bypass");
        HistoryBackgroundWork.Paused = true;
        Check(!HistoryBackgroundWork.UserIsIdle(), "Stop blocks ordinary automatic preparation too");
        HistoryBackgroundWork.Paused = false;
        HistoryBackgroundWork.SelectionRequested = false;
        Check(!HistoryBackgroundWork.CanRun(1000, 0, false, 1000), "Outside Revit remains paused at one-second cadence");
        Check(!HistoryBackgroundWork.CanRun(2000, 0, true, 1000), "Return still settles before capturing");
        Check(!HistoryBackgroundWork.CanRun(2999, 0, true, 1000), "One-second threshold is not reached early");
        Check(HistoryBackgroundWork.CanRun(3000, 0, true, 1000), "Automatic capture resumes after one second");
        Check(!HistoryBackgroundWork.CanRun(3001, 3000, true, 1000), "Input interrupts the one-second capture cadence");
        Check(!HistoryBackgroundWork.CanRun(10000, 0, false, 2000), "AFK outside Revit must pause");
        Check(!HistoryBackgroundWork.CanRun(11000, 0, true, 2000), "Returning to Revit must not start immediately");
        Check(!HistoryBackgroundWork.CanRun(12999, 0, true, 2000), "Foreground settling delay");
        Check(HistoryBackgroundWork.CanRun(13000, 0, true, 2000), "Idle foreground can resume");
        Check(!HistoryBackgroundWork.CanRun(13001, 13000, true, 2000), "New input stops capture");
        Check(!HistoryBackgroundWork.CanRun(15000, 13000, false, 2000), "Switching away stops capture");
        Check(!HistoryBackgroundWork.CanRun(20000, 13000, true, 2000), "Return resets settling delay");
        Check(HistoryBackgroundWork.CanRun(22000, 13000, true, 2000), "Return resumes after settling");
        HistoryBackgroundWork.CanRun(uint.MaxValue-1000, 0, false, 2000);
        Check(!HistoryBackgroundWork.CanRun(uint.MaxValue-1000, uint.MaxValue-3000, true, 2000), "Rollover initial delay");
        Check(HistoryBackgroundWork.CanRun(1000, uint.MaxValue-3000, true, 2000), "Tick counter rollover");
        Check(!HistoryBackgroundWork.CanRunInventory(1000,1000,true,false), "Inventory yields to current foreground input");
        Check(!HistoryBackgroundWork.CanRunInventory(1149,1000,true,false), "Inventory does not start before its 150ms input pause");
        Check(HistoryBackgroundWork.CanRunInventory(1150,1000,true,false), "Short inventory resumes at the 150ms boundary");
        Check(HistoryBackgroundWork.CanRunInventory(1000,1000,false,false), "Opening protection progresses while another app receives input");
        Check(!HistoryBackgroundWork.CanRunInventory(1000,0,false,true), "Stop also blocks inventory outside Revit");
        Check(!HistoryBackgroundWork.CanRunInventory(1000,0,true,true), "Stop blocks foreground inventory");
        Check(!HistoryBackgroundWork.CanRunInventory(49,uint.MaxValue-99,true,false), "Inventory threshold remains accurate across tick rollover");
        Check(HistoryBackgroundWork.CanRunInventory(50,uint.MaxValue-99,true,false), "Inventory resumes after 150ms across tick rollover");
        HistoryBackgroundWork.Paused=true;
        Check(!HistoryBackgroundWork.InventoryCanRun(), "Paused inventory exits before touching native input APIs");
        HistoryBackgroundWork.Paused=false;
    }
}
'@
Add-Type -TypeDefinition ("using System; using System.Diagnostics; using System.Runtime.InteropServices;`n" + $gate + $checks + $wpfStubs)
[IdleGateChecks]::Run()
Write-Output 'PASS: selection, Stop, heavy foreground suspension, return delay, input interruption, light inventory in background, 150ms threshold and tick rollover.'
