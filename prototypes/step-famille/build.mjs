// Compile le prototype et installe son manifeste Revit 2026 (Compléments > Outils externes).
// Processus direct, sans shell (voir AGENTS.md).
//   node build.mjs [dossier=v2]   |   node build.mjs --retirer
// Sortie versionnée (bin\<dossier>) : Revit garde la DLL chargée verrouillée jusqu'à sa fermeture.
import { spawn } from "node:child_process";
import { existsSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const addin = join(process.env.APPDATA, "Autodesk", "Revit", "Addins", "2026", "BIMaestro.ProtoStep.addin");
if (process.argv[2] === "--retirer") { if (existsSync(addin)) rmSync(addin); console.log("Manifeste du prototype retiré."); process.exit(0); }

const proj = join(here, "RevitProto");
const folder = process.argv[2] ?? "v2";
const outDir = join(proj, "bin", folder);
const p = spawn("dotnet", ["build", "ProtoStep.csproj", "-c", "Release", "-v", "minimal", "-nologo", `-p:OutputPath=${outDir}\\`],
  { cwd: proj, shell: false, windowsHide: true });
let log = ""; p.stdout.on("data", (d) => (log += d)); p.stderr.on("data", (d) => (log += d));
p.on("exit", (code) => {
  console.log(log.split(/\r?\n/).filter((l) => !/MSB3277/.test(l) && /error|warn|->|réussi|succeeded|échec|failed/i.test(l)).slice(-30).join("\n"));
  if (code !== 0) { console.log("exit", code); process.exit(code ?? 1); }
  const dll = join(outDir, "BIMaestro.ProtoStep.dll");
  const entry = (name, text, desc, cls, id) => `  <AddIn Type="Command">
    <Name>${name}</Name>
    <Text>${text}</Text>
    <Description>${desc}</Description>
    <Assembly>${dll}</Assembly>
    <FullClassName>${cls}</FullClassName>
    <ClientId>${id}</ClientId>
    <VendorId>BIMaestro</VendorId>
    <VendorDescription>BIMaestro (prototype)</VendorDescription>
  </AddIn>`;
  writeFileSync(addin, `<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
${entry("Proto STEP vers famille", "Proto STEP → famille", "Prototype v1 : chaudière UT-L 60 en volumes simples.", "BIMaestro.ProtoStep.ProtoStepFamilyCommand", "6f1d3c52-8a0e-4b7e-9c41-2f5b7d9e0a17")}
${entry("Proto CAO vers famille LOD", "Proto CAO → famille LOD", "Prototype : STEP ou SAT en une famille à 3 niveaux de détail (boîte / volumes / modèle complet).", "BIMaestro.ProtoStep.ProtoLodCommand", "a4e2b7c1-3d58-4f9a-b6e0-91c2d7f84e35")}
${entry("Proto STEP vers famille v2", "Proto STEP → famille v2", "Prototype v2 : géométrie du STEP simplifiée, raccordements détectés et validés.", "BIMaestro.ProtoStep.ProtoStepV2Command", "0c7a9e14-5b2f-4d63-a8e1-7f3d2b6c9a41")}
</RevitAddIns>
`);
  console.log("Manifeste installé :", addin, "->", dll);
});
