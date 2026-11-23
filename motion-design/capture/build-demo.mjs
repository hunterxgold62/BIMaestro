// Compile la version de test Revit 2026 avec le mode démo (BIMAESTRO_DEMO).
// Sortie séparée : BIMaestro/bin/Revit2026/Demo/ — ne touche pas la release.
// Processus direct, sans shell (voir AGENTS.md).
import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "BIMaestro");
const args = [
  "build", "BIMaestro.Revit2025.csproj", "-c", "Release",
  "-p:RevitVersion=2026", "-p:BimaestroDemo=true",
  `-p:OutputPath=bin/Revit2026/${process.argv[2] ?? "Demo"}/`, "-p:IntermediateOutputPath=obj/Demo2026/",
  "-v", "minimal", "-nologo",
];
const p = spawn("dotnet", args, { cwd: repo, shell: false, windowsHide: true });
let out = "";
p.stdout.on("data", (d) => (out += d));
p.stderr.on("data", (d) => (out += d));
p.on("exit", (code) => {
  const lines = out.split(/\r?\n/).filter((l) => / error |->|réussi|succeeded|échec|failed/i.test(l));
  console.log(lines.slice(-30).join("\n"));
  console.log("exit", code);
  process.exit(code ?? 1);
});
