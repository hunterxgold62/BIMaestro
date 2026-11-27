import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
const directory=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(directory,'../..');
let source=await fs.readFile(path.join(root,'BIMaestro/app et excel/temps revit/TimeProjectTracking.cs'),'utf8');
source=source.replace(/        \[System\.Runtime\.InteropServices\.DllImport\("user32.dll"\)\] private static extern IntPtr GetForegroundWindow\(\);/,'        private static IntPtr GetForegroundWindow() => new IntPtr(1);')
.replace(/        \[System\.Runtime\.InteropServices\.DllImport\("user32.dll"\)\] private static extern uint GetWindowThreadProcessId\(IntPtr window, out uint processId\);/,'        private static uint GetWindowThreadProcessId(IntPtr window, out uint processId) { processId = TestGate.Foreground ? (uint)System.Diagnostics.Process.GetCurrentProcess().Id : 0; return 1; }')
.replace(/        \[System\.Runtime\.InteropServices\.DllImport\("user32.dll"\)\] private static extern bool IsWindowEnabled\(IntPtr window\);/,'        private static bool IsWindowEnabled(IntPtr window) => TestGate.MainEnabled;');
if(source.includes('DllImport'))throw Error('Native gates were not isolated');
await fs.mkdir(path.join(directory,'obj'),{recursive:true});await fs.writeFile(path.join(directory,'obj/TimeProjectTracking.fixture.cs'),source);
const child=spawn('dotnet',['run','--project',path.join(directory,'Service.Tests.csproj'),'-p:UseSharedCompilation=false'],{cwd:root,shell:false,windowsHide:true,stdio:'inherit'});
child.on('exit',code=>{process.exitCode=code??1;});
