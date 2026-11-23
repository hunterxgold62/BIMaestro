// Pointe le manifeste Revit 2026 de BIMaestro vers une DLL donnée.
//   node set-addin.mjs <chemin\BIMaestro.dll>
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const addin = join(process.env.APPDATA, "Autodesk", "Revit", "Addins", "2026", "BIMaestro.addin");
const dll = process.argv[2];
if (!dll) { console.log(readFileSync(addin, "utf8").match(/<Assembly>[^<]*<\/Assembly>/g).join("\n")); process.exit(0); }
const s = readFileSync(addin, "utf8").replace(/<Assembly>[^<]*<\/Assembly>/g, () => `<Assembly>${dll}</Assembly>`);
writeFileSync(addin, s);
console.log(s.match(/<Assembly>[^<]*<\/Assembly>/g).join("\n"));
