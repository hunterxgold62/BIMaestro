// Défilé automatique des looks du menu Propriétés (BIMaestro), filmé en même temps.
// Pas besoin de build de démo : BIMaestro relit PropertiesAppearance.json toutes les 2 s.
// Il faut un MUR sélectionné dans Revit (l'ordre ne s'applique qu'aux murs et aux vues).
//
//   node props-cycle.mjs [secondesParLook=5] [nomDeLaPrise=proprietes]
//   node props-cycle.mjs --restore
import { spawn } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const FILE = join(process.env.APPDATA, "BIMaestro", "PropertiesAppearance.json");
const BACKUP = FILE + ".avant-demo";
const GROUPS = FILE + ".groups";

const restore = () => { if (existsSync(BACKUP)) { copyFileSync(BACKUP, FILE); console.log("Réglages d'origine restaurés."); } };
if (process.argv[2] === "--restore") { restore(); process.exit(0); }
const secs = Number(process.argv[2] ?? 5);
const take = process.argv[3] ?? "proprietes";
if (existsSync(FILE) && !existsSync(BACKUP)) copyFileSync(FILE, BACKUP);

// Groupes Revit d'un mur (ForgeTypeId) et paramètres (BuiltInParameter).
const G = {
  contraintes: "autodesk.parameter.group:constraints-1.0.0",
  structure: "autodesk.parameter.group:structural-1.0.0",
  cotes: "autodesk.parameter.group:dimensions-1.0.0",
  identite: "autodesk.parameter.group:identityData-1.0.0",
  phases: "autodesk.parameter.group:phasing-1.0.0",
  construction: "autodesk.parameter.group:construction-1.0.0",
  graphismes: "autodesk.parameter.group:graphics-1.0.0",
  ifc: "autodesk.parameter.group:ifc-1.0.0",
  analytique: "autodesk.parameter.group:analyticalModel-1.0.0",
  section: "autodesk.revit.group:wallCrossSectionDefinition-1.0.0",
  geometrie: "autodesk.revit.group:geometryPositioning-1.0.0",
};
const P = {
  hauteur: `${G.contraintes}/builtin:-1001105`, decalageBase: `${G.contraintes}/builtin:-1001108`,
  contrainteBase: `${G.contraintes}/builtin:-1001107`, contrainteHaut: `${G.contraintes}/builtin:-1001103`,
  longueur: `${G.cotes}/builtin:-1004005`, surface: `${G.cotes}/builtin:-1012805`, volume: `${G.cotes}/builtin:-1012806`,
  commentaires: `${G.identite}/builtin:-1010106`, marque: `${G.identite}/builtin:-1001203`,
  creation: `${G.phases}/builtin:-1012100`,
};
const PRESET = {
  sauge: ["#FF155C42", "#FFE4F1E9"], bleu: ["#FF234F78", "#FFE7F1FB"],
  ambre: ["#FF77521A", "#FFFBF1D9"], lavande: ["#FF604483", "#FFF1EBFA"],
  vert: ["#FFFFFFFF", "#FF0F5132"], menthe: ["#FF0F5132", "#FFD7F5E5"],
  rose: ["#FF8A2C4F", "#FFFCE7EF"], ardoise: ["#FFFFFFFF", "#FF334155"],
};
const rule = (Kind, TargetKey, p) => ({ Kind, TargetKey, Label: TargetKey,
  Light: { Text: PRESET[p][0], Background: PRESET[p][1], IsEmpty: false },
  Dark: { Text: PRESET[p][0], Background: PRESET[p][1], IsEmpty: false } });
const groupsIn = (map) => Object.entries(map).map(([g, p]) => rule("group", G[g], p));
const paramsIn = (map) => Object.entries(map).map(([k, p]) => rule("parameter", P[k], p));

const CUSTOM = "custom:b1a3e57000d04f6c9a1e5d2b7c4f8a01";
const writeCustom = (name, members) => {
  const json = JSON.stringify([{ Key: CUSTOM, Name: name, Members: members.map((m) => P[m]) }]);
  const hash = createHash("sha256").update(json, "utf8").digest("hex").toUpperCase();
  mkdirSync(GROUPS, { recursive: true });
  const target = join(GROUPS, hash + ".json");
  if (!existsSync(target)) writeFileSync(target, json, "utf8");
  return hash;
};
const profile = ({ rules = [], order = [], custom = null }) => {
  const Parameters = {};
  if (custom) Parameters["bimaestro:custom-groups:v1"] = [writeCustom(custom.name, custom.members)];
  return { Key: "cat:-2000011", Name: "Catégorie · Murs", Rules: rules,
    Layout: { Groups: [...(custom ? [CUSTOM] : []), ...order.map((g) => G[g])], Parameters } };
};
const L = (label, p) => ({ label, prefs: { Version: 1, Enabled: true, Profiles: p ? [profile(p)] : [] } });
const MES = { name: "Mes paramètres", members: ["hauteur", "decalageBase", "commentaires", "marque"] };

const looks = [
  L("Revit de base", null),
  L("Un groupe, une couleur", { rules: groupsIn({ contraintes: "sauge", structure: "bleu", cotes: "ambre", identite: "lavande", phases: "rose", construction: "bleu", graphismes: "sauge", ifc: "ambre", analytique: "lavande", section: "bleu", geometrie: "rose" }) }),
  L("Vert BIMaestro", { rules: groupsIn({ contraintes: "vert", structure: "vert", cotes: "vert", identite: "vert", phases: "vert", construction: "vert", graphismes: "vert", ifc: "vert", analytique: "vert", section: "vert", geometrie: "vert" }) }),
  L("Les paramètres clés", { rules: paramsIn({ hauteur: "ambre", decalageBase: "ambre", contrainteBase: "bleu", contrainteHaut: "bleu", longueur: "sauge", surface: "sauge", volume: "sauge", commentaires: "lavande", marque: "lavande", creation: "rose" }) }),
  L("Identité en premier", { order: ["identite", "cotes", "contraintes", "phases"], rules: groupsIn({ identite: "lavande", cotes: "ambre" }) }),
  L("Mes paramètres", { custom: MES, rules: [rule("group", CUSTOM, "vert"), ...paramsIn({ hauteur: "menthe", decalageBase: "menthe", commentaires: "menthe", marque: "menthe" })] }),
  L("Ardoise et pastels", { rules: [...groupsIn({ contraintes: "ardoise", structure: "ardoise", cotes: "ardoise", identite: "ardoise", phases: "ardoise", construction: "ardoise", graphismes: "ardoise", ifc: "ardoise", analytique: "ardoise", section: "ardoise", geometrie: "ardoise" }), ...paramsIn({ hauteur: "bleu", longueur: "sauge", surface: "sauge", commentaires: "rose", creation: "lavande" })] }),
  L("Tout en un", { custom: MES, order: ["cotes", "contraintes", "identite"], rules: [rule("group", CUSTOM, "vert"), ...groupsIn({ cotes: "ambre", contraintes: "sauge", identite: "lavande", structure: "bleu", phases: "rose" }), ...paramsIn({ hauteur: "menthe", decalageBase: "menthe", commentaires: "menthe", marque: "menthe" })] }),
  L("Revit de base (fin)", null),
];

const total = looks.length * secs + 2;
const rec = spawn("node", [join(here, "capture.mjs"), "record", take, String(total)],
  { shell: false, windowsHide: true, stdio: "inherit", env: { ...process.env, ZONE: "zone-proprietes.json" } });
const log = [];
let i = 0;
const t0 = Date.now();
const next = () => {
  const v = looks[i++];
  writeFileSync(FILE, JSON.stringify(v.prefs, null, 2), "utf8");
  const at = ((Date.now() - t0) / 1000).toFixed(2);
  log.push(`${at}\t${v.label}`);
  console.log(`${at}s  ${v.label}`);
};
setTimeout(() => { next(); const id = setInterval(() => { if (i >= looks.length) clearInterval(id); else next(); }, secs * 1000); }, 1000);
const finish = () => { restore(); writeFileSync(join(here, "rushes", `${take}.txt`), log.join("\n")); };
rec.on("exit", () => { finish(); process.exit(0); });
process.on("SIGINT", () => { rec.kill(); finish(); process.exit(0); });
