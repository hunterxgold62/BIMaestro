import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
const directory=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(directory,'../..');
// Only filesystem paths and the mutex name are isolated; all persistence logic is unchanged.
let source=await fs.readFile(path.join(root,'BIMaestro/app et excel/temps revit/ExcelLogger.cs'),'utf8');
source=source.replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "RevitLogs", "Temp")','Path.Combine(LogDirectory, "Temp")')
    .replace('private const string StorageMutexName','private static readonly string StorageMutexName')
    .replace('"BIMaestro_TimeLogger_Storage_v2"','"BIMaestro_TimeLogger_Test_" + Process.GetCurrentProcess().Id');
await fs.mkdir(path.join(directory,'obj'),{recursive:true});await fs.writeFile(path.join(directory,'obj/ExcelLogger.fixture.cs'),source);
const child=spawn('dotnet',['run','--project',path.join(directory,'Logger.Tests.csproj'),'-p:UseSharedCompilation=false'],{cwd:root,shell:false,windowsHide:true,stdio:'inherit'});
child.on('exit',code=>{process.exitCode=code??1;});
