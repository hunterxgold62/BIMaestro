// Défilé des nouveaux styles du menu Propriétés (couches, icônes, zébrage, contour), filmé en même temps.
// Nécessite la build de test Styles4 (ou plus récente) chargée dans Revit 2026, un MUR sélectionné.
// BIMaestro relit PropertiesAppearance.json toutes les 2 s.
//
//   node props-styles.mjs <set: styles|couches> [secondesParLook=5] [nomDeLaPrise=<set>]
//   node props-styles.mjs --restore
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
const set = process.argv[2] ?? "styles";
const secs = Number(process.argv[3] ?? 5);
const take = process.argv[4] ?? set;
if (existsSync(FILE) && !existsSync(BACKUP)) copyFileSync(FILE, BACKUP);

const G = {
  contraintes: "autodesk.parameter.group:constraints-1.0.0", structure: "autodesk.parameter.group:structural-1.0.0",
  cotes: "autodesk.parameter.group:dimensions-1.0.0", identite: "autodesk.parameter.group:identityData-1.0.0",
  phases: "autodesk.parameter.group:phasing-1.0.0", texte: "autodesk.parameter.group:text-1.0.0",
  ifc: "autodesk.parameter.group:ifc-1.0.0", section: "autodesk.revit.group:wallCrossSectionDefinition-1.0.0",
  construction: "autodesk.parameter.group:construction-1.0.0", graphismes: "autodesk.parameter.group:graphics-1.0.0",
};
const P = {
  hauteur: `${G.contraintes}/builtin:-1001105`, decalageBase: `${G.contraintes}/builtin:-1001108`,
  contrainteBase: `${G.contraintes}/builtin:-1001107`, contrainteHaut: `${G.contraintes}/builtin:-1001103`,
  commentaires: `${G.identite}/builtin:-1010106`, marque: `${G.identite}/builtin:-1001203`, creation: `${G.phases}/builtin:-1012100`,
};
// Une couleur par groupe, harmonieuse (teinte auto : fond pâle et mode sombre calculés).
const C = { sauge: "#155C42", bleu: "#234F78", ambre: "#77521A", lavande: "#604483", framboise: "#8A2C4F", sarcelle: "#0F766E", ardoise: "#475569" };
const COLOR = { contraintes: C.sauge, structure: C.bleu, cotes: C.ambre, identite: C.lavande, phases: C.framboise, texte: C.sarcelle, ifc: C.ardoise, section: C.sarcelle, construction: C.bleu, graphismes: C.sauge };
const ICON = { contraintes: "levels", structure: "structure", cotes: "zone", identite: "data", phases: "new-project", texte: "study", ifc: "coordination", section: "details", construction: "architecture", graphismes: "presentation" };
const hex = (h) => "#FF" + h.slice(1).toUpperCase();
const pale = (h) => { const n = parseInt(h.slice(1), 16); const m = (c) => Math.round(255 + (c - 255) * 0.12); return "#FF" + [n >> 16, (n >> 8) & 255, n & 255].map((c) => m(c).toString(16).padStart(2, "0")).join("").toUpperCase(); };
const rule = (Kind, TargetKey, color, Style, Icon = null) => ({ Kind, TargetKey, Label: TargetKey, Light: { Text: hex(color), Background: pale(color) }, Dark: { Text: null, Background: null }, Style, AutoTint: true, Icon });
const groups = (style, { icons = false, only } = {}) => Object.keys(COLOR).filter((g) => !only || only.includes(g)).map((g) => rule("group", G[g], COLOR[g], style, icons ? ICON[g] : null));
const params = (keys, style, color) => keys.map((k) => rule("parameter", P[k], color ?? (k === "commentaires" || k === "marque" ? COLOR.identite : k === "creation" ? COLOR.phases : COLOR.contraintes), style));

const CUSTOM = "custom:b1a3e57000d04f6c9a1e5d2b7c4f8a02";
const customGroup = (name, members) => {
  const json = JSON.stringify([{ Key: CUSTOM, Name: name, Members: members.map((m) => P[m]) }]);
  const hash = createHash("sha256").update(json, "utf8").digest("hex").toUpperCase();
  mkdirSync(GROUPS, { recursive: true });
  const target = join(GROUPS, hash + ".json");
  if (!existsSync(target)) writeFileSync(target, json, "utf8");
  return hash;
};
const L = (label, rules, custom = null) => {
  if (!rules) return { label, prefs: { Version: 1, Enabled: true, Profiles: [] } };
  const Parameters = {};
  if (custom) Parameters["bimaestro:custom-groups:v1"] = [customGroup(custom.name, custom.members)];
  return { label, prefs: { Version: 1, Enabled: true, Profiles: [{ Key: "cat:-2000011", Name: "Catégorie · Murs", Rules: rules, Layout: { Groups: custom ? [CUSTOM] : [], Parameters } }] } };
};
const KEYS = ["hauteur", "contrainteHaut", "commentaires"];
const MES = { name: "Mes favoris", members: ["hauteur", "decalageBase", "commentaires", "marque"] };

const COUCHE6 = () => [...groups("FondDegrade+TexteCouleur+Lisere+Zebre", { icons: true }), ...params(KEYS, "FondDegrade+TexteCouleur+Contour")];
const FAV = { name: "Mes paramètres", members: ["hauteur", "contrainteHaut", "commentaires"] };

const sets = {
  // Huit looks très différents, pour le défilé.
  styles: [
    L("Revit de base"),
    L("Liserés", groups("Lisere")),
    L("Texte coloré", groups("TexteCouleur")),
    L("Dégradé + pastille", [...groups("FondDegrade+TexteCouleur"), ...params(KEYS, "FondDegrade+TexteCouleur+Pastille")]),
    L("Titres + icônes", groups("FondPlein+TexteCouleur+EnTete", { icons: true })),
    L("Zébrage", groups("FondPlein+TexteCouleur+EnTete+Zebre", { icons: true })),
    L("Contours clés", [...groups("FondPlein+TexteCouleur+EnTete"), ...params(KEYS, "Contour")]),
    L("Mes favoris", [rule("group", CUSTOM, C.sauge, "FondDegrade+TexteCouleur", "validation"), ...groups("TexteCouleur"), ...params(["hauteur", "decalageBase", "commentaires", "marque"], "Lisere", C.sauge)], MES),
    L("Tout en un", [...groups("FondPlein+TexteCouleur+EnTete+Zebre", { icons: true }), ...params(KEYS, "TexteCouleur+Pastille")]),
    L("Revit de base (fin)"),
  ],
  // La palette se construit couche par couche, pour le configurateur.
  couches: [
    L("Revit de base"),
    L("+ Texte", groups("TexteCouleur")),
    L("+ Fond", groups("FondDegrade+TexteCouleur")),
    L("+ Liseré", groups("FondDegrade+TexteCouleur+Lisere")),
    L("+ Icônes", groups("FondDegrade+TexteCouleur+Lisere", { icons: true })),
    L("+ Zébrage", groups("FondDegrade+TexteCouleur+Lisere+Zebre", { icons: true })),
    L("+ Contour", [...groups("FondDegrade+TexteCouleur+Lisere+Zebre", { icons: true }), ...params(KEYS, "FondDegrade+TexteCouleur+Contour")]),
    L("Revit de base (fin)"),
  ],
  // Palette complète, puis trois paramètres qui montent dans une section personnalisée.
  favoris: [
    L("+ Contour", COUCHE6()),
    L("+ Ton paramètre", [rule("group", CUSTOM, "#0F5132", "FondDegrade+TexteCouleur+Lisere+Zebre", "validation"), ...COUCHE6()], FAV),
    L("+ Contour (fin)", COUCHE6()),
  ],
};
const looks = sets[set];
if (!looks) { console.log("set inconnu :", set); process.exit(1); }

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
  log.push(`${at}\t${v.label}`); console.log(`${at}s  ${v.label}`);
};
setTimeout(() => { next(); const id = setInterval(() => { if (i >= looks.length) clearInterval(id); else next(); }, secs * 1000); }, 1000);
const finish = () => { restore(); writeFileSync(join(here, "rushes", `${take}.txt`), log.join("\n")); };
rec.on("exit", () => { finish(); process.exit(0); });
process.on("SIGINT", () => { rec.kill(); finish(); process.exit(0); });
