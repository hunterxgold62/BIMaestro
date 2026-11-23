// Défilé automatique des apparences de l'arborescence, filmé en même temps.
// Nécessite la build de test Revit 2026 (mode démo, voir build-demo.mjs)
// chargée dans Revit : elle réapplique le thème dès que le fichier change.
//
//   node demo-cycle.mjs [secondesParVariante=5] [nomDeLaPrise=defile]
//   node demo-cycle.mjs --restore      (remet les réglages d'origine)
//
// Les réglages d'origine sont sauvegardés avant et restaurés à la fin (même sur Ctrl+C).
import { spawn } from "node:child_process";
import { copyFileSync, existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { homedir } from "node:os";

const here = dirname(fileURLToPath(import.meta.url));
const PREFS = join(homedir(), "OneDrive - SAS H.C.M. HOLDING CABINET MERLIN", "Documents",
  "BIMaestro_Revit", "SauvegardePréférence", "Option", "couleursRuban.json");
const BACKUP = PREFS + ".avant-demo";
const ICONS = join(dirname(PREFS), "iconesArborescence.json");
const ICONS_BACKUP = ICONS + ".avant-demo";

const restore = () => {
  if (existsSync(BACKUP)) { copyFileSync(BACKUP, PREFS); console.log("Réglages d'origine restaurés."); }
  if (existsSync(ICONS_BACKUP)) copyFileSync(ICONS_BACKUP, ICONS);
};
if (process.argv[2] === "--restore") { restore(); process.exit(0); }

const secs = Number(process.argv[2] ?? 5);
const take = process.argv[3] ?? "defile";
const set = process.argv[4] ?? "effets";

const raw = readFileSync(PREFS, "utf8");
const bom = raw.startsWith("﻿");
const base = JSON.parse(raw.replace(/^﻿/, ""));
if (!existsSync(BACKUP)) copyFileSync(PREFS, BACKUP);
if (existsSync(ICONS) && !existsSync(ICONS_BACKUP)) copyFileSync(ICONS, ICONS_BACKUP);
const iconsBase = existsSync(ICONS) ? JSON.parse(readFileSync(ICONS, "utf8").replace(/^﻿/, "")) : { CustomAssets: [] };
const original = base.Arborescence ?? {};

// Variantes : fond + couleurs + effets sur les dossiers (section « Arborescence »).
const FOLDERS = ["Plans d'étage (Floor Plan)", "Plans d'étage (Working)", "Plans de plafond (Ceiling Plan)", "Vues 3D (3D View)",
  "Elévations (Building Elevation)", "Coupes (Building Section)", "Coupes (Wall Section)", "Vues de détail (Detail)",
  "Rendus (Rendering)", "Visites virtuelles (Walkthrough)", "Plans de surface (Gross Building)"];
const RAINBOW = ["#FFEF4444", "#FFF97316", "#FFEAB308", "#FF22C55E", "#FF14B8A6", "#FF3B82F6", "#FF6366F1", "#FFA855F7", "#FFEC4899", "#FF0F5132", "#FF64748B"];
const PASTEL = ["#FFFCA5A5", "#FFFDBA74", "#FFFDE047", "#FF86EFAC", "#FF5EEAD4", "#FF93C5FD", "#FFA5B4FC", "#FFD8B4FE", "#FFF9A8D4", "#FF6EE7B7", "#FFCBD5E1"];
const rules = (effect, colors = RAINBOW, scope = "Branche") =>
  FOLDERS.map((name, i) => ({ Nom: name, Couleur: colors[i % colors.length], Chemin: name, Effet: effect, Portee: scope }));
const base0 = { Activer: true, ColorerTypesVues: false, ColorerCategories: true, CibleCouleursVues: "Fond", ColorerParentsVues: true };
const V = (label, o) => ({ label, o: { ...base0, ...o } });
const variants = [
  V("Revit de base", { Activer: false, ColorerCategories: false, ReglesCategories: [] }),
  V("Pastilles", { ModeFond: "Uni", Fond: "#FFFFFFFF", Texte: "#FF111827", Accent: "#FF0F5132", ReglesCategories: rules("Pastille") }),
  V("Badges", { ModeFond: "Grille d'architecte", Fond: "#FFF8FAFC", Texte: "#FF1E293B", Accent: "#FF2563EB", ReglesCategories: rules("Badge") }),
  V("Icônes", { ModeFond: "Plan d'architecte", Fond: "#FF1E3A8A", Texte: "#FFFFFFFF", Accent: "#FF93C5FD", ReglesCategories: rules("Icône", PASTEL) }),
  V("Texte en couleur", { ModeFond: "Courbes topographiques", Fond: "#FFF7F3E9", Texte: "#FF3F3A2E", Accent: "#FFB45309", ReglesCategories: rules("Texte") }),
  V("Dégradés", { ModeFond: "Vagues pastel", Fond: "#FFFFF7ED", Texte: "#FF7C2D12", Accent: "#FFEA580C", ReglesCategories: rules("Dégradé") }),
  V("Fonds par dossier", { ModeFond: "Bulles pastel", Fond: "#FFFFF9FC", Texte: "#FF3A3D48", Accent: "#FFDB578E", ReglesCategories: rules("Fond", PASTEL) }),
  V("Bordures", { ModeFond: "Rubans fluides", Fond: "#FFF5F3FF", Texte: "#FF3B0764", Accent: "#FF7C3AED", ReglesCategories: rules("Bordure") }),
  V("Animation", { ModeFond: "Constellation douce", Fond: "#FF0F172A", Texte: "#FFE2E8F0", Accent: "#FF38BDF8", ReglesCategories: rules("Animation", PASTEL) }),
  V("Gras coloré", { ModeFond: "Lucioles pastel", Fond: "#FFF6F8FC", Texte: "#FF243142", Accent: "#FFEF4444", ReglesCategories: rules("Gras") }),
  V("Souligné", { ModeFond: "Encre dans l'eau", Fond: "#FFF0FDFA", Texte: "#FF134E4A", Accent: "#FF0D9488", ReglesCategories: rules("Souligné") }),
  V("Pastilles + verre", { ModeFond: "Verre dépoli", Fond: "#FFEFF6FF", Texte: "#FFBE123C", Accent: "#FF22C55E", ReglesCategories: rules("Pastille") }),
  V("Aurore + badges", { ModeFond: "Aurore boréale", Fond: "#FF052E16", Texte: "#FFECFDF5", Accent: "#FF4ADE80", ReglesCategories: rules("Badge", PASTEL) }),
  V("Dégradé animé", { ModeFond: "Dégradé pastel animé", AnimerFond: true, Fond: "#FFFDF4FF", Texte: "#FF1F2937", Accent: "#FFD946EF", ReglesCategories: rules("Dégradé") }),
  V("Couleurs par type de vue", { ModeFond: "Uni", Fond: "#FFFFFFFF", Texte: "#FF111827", Accent: "#FF0F5132", ColorerCategories: false, ColorerTypesVues: true, ReglesCategories: [] }),
  V("Types de vue en texte", { ModeFond: "Uni", Fond: "#FFFFFFFF", Texte: "#FF111827", Accent: "#FF0F5132", ColorerCategories: false, ColorerTypesVues: true, CibleCouleursVues: "Texte", ReglesCategories: [] }),
];

// Icônes : le nom de la règle doit être le libellé exact du dossier ou de la vue.
const ICON_MAP = [
  ["Plans d'étage (Enlarged Plan)", "room"], ["Plans d'étage (Floor Plan)", "levels"],
  ["Plans d'étage (Life Safety Plan)", "fire"], ["Plans d'étage (Schematic Plan)", "study"],
  ["Plans d'étage (Site Plan)", "garden"], ["Plans d'étage (Working)", "new-project"],
  ["Plans d'étage (Working Dimensions)", "details"], ["Plans de plafond (Ceiling Plan)", "lighting"],
  ["Plans de plafond (Working)", "power"], ["Vues 3D (3D Coordination)", "coordination"],
  ["Vues 3D (3D View)", "presentation"], ["Elévations (Building Elevation)", "architecture"],
  ["Elévations (Interior Elevation)", "reception"], ["Coupes (Building Section)", "structure"],
  ["Coupes (Wall Section)", "existing"], ["Coupes (Working Section)", "demolition"],
  ["Vues de détail (Detail)", "details"], ["Rendus (Rendering)", "presentation"],
  ["Vues de dessin (Detail)", "study"], ["Visites virtuelles (Walkthrough)", "restaurant"],
  ["Plans de surface (Gross Building)", "zone"], ["Plans de surface (Rentable)", "zone"],
  ["Légendes", "validation"], ["Feuilles (all)", "publication"], ["Typical Schedule", "data"],
  ["Working Schedule", "data"], ["A100 - Parking Deck Floor Plan", "levels"], ["A106 - Green Roof", "garden"],
  ["A107 - Roof Plan", "roof"], ["A101 - First Floor Plan", "levels"], ["A102 - Second Floor Plan", "levels"],
  ["A108 - First Floor Ceiling Plan", "lighting"], ["A109 - Second Floor Ceiling Plan", "lighting"],
];
const iconRules = (n = ICON_MAP.length) => ICON_MAP.slice(0, n).map(([Name, IconId]) => ({ Name, IconId }));
if (set === "icones") {
  variants.length = 0;
  const off = { Activer: false, ColorerCategories: false, ReglesCategories: [] };
  variants.push(
    { label: "Revit de base", o: { ...off }, icons: { Enabled: false, Rules: [] } },
    { label: "Icônes métiers", o: { ...off }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "Icônes + fond uni", o: { ...base0, ModeFond: "Uni", Fond: "#FFFFFFFF", Texte: "#FF111827", Accent: "#FF0F5132", ColorerCategories: false, ReglesCategories: [] }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "Icônes + texte coloré", o: { ...base0, ModeFond: "Lucioles pastel", Fond: "#FFF6F8FC", Texte: "#FF243142", Accent: "#FFEF4444", ReglesCategories: rules("Texte") }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "Icônes + dégradés", o: { ...base0, ModeFond: "Verre dépoli", Fond: "#FFEFF6FF", Texte: "#FF1E293B", Accent: "#FF22C55E", ReglesCategories: rules("Dégradé") }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "Icônes de nuit", o: { ...base0, ModeFond: "Aurore boréale", Fond: "#FF052E16", Texte: "#FFECFDF5", Accent: "#FF4ADE80", ReglesCategories: rules("Badge", PASTEL) }, icons: { Enabled: true, Rules: iconRules() } },
  );
}

// Tous les libellés de dossiers/vues connus du projet (pour les icônes par métier).
const LABELS = ICON_MAP.map(([n]) => n);
const iconsAll = (ids) => ({ Enabled: true, Rules: LABELS.map((Name, i) => ({ Name, IconId: ids[i % ids.length] })) });
const NO_ICONS = { Enabled: false, Rules: [] };
const off = { Activer: false, ColorerCategories: false, ReglesCategories: [] };

if (set === "config") {
  // Les réglages s'empilent : fond, puis texte, puis icônes, puis badges, puis mode nuit.
  variants.length = 0;
  const lu = { ...base0, ModeFond: "Lucioles pastel", Fond: "#FFF6F8FC", Texte: "#FF243142", Accent: "#FFEF4444" };
  variants.push(
    { label: "Revit de base", o: { ...off }, icons: NO_ICONS },
    { label: "+ Fond", o: { ...lu, ColorerCategories: false, ReglesCategories: [] }, icons: NO_ICONS },
    { label: "+ Couleurs", o: { ...lu, ReglesCategories: rules("Texte") }, icons: NO_ICONS },
    { label: "+ Icônes", o: { ...lu, ReglesCategories: rules("Texte") }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "+ Badges", o: { ...lu, ReglesCategories: [...rules("Texte"), ...rules("Badge")] }, icons: { Enabled: true, Rules: iconRules() } },
    { label: "+ Mode nuit", o: { ...lu, ModeFond: "Aurore boréale", Fond: "#FF052E16", Texte: "#FFECFDF5", Accent: "#FF4ADE80", ReglesCategories: [...rules("Texte", PASTEL), ...rules("Badge", PASTEL)] }, icons: { Enabled: true, Rules: iconRules() } },
  );
}

if (set === "metiers") {
  // Un thème complet par métier : fond, couleurs, effet et icônes du métier.
  variants.length = 0;
  const pal = (...c) => c;
  variants.push(
    { label: "Revit de base", o: { ...off }, icons: NO_ICONS },
    { label: "Architecte", icons: iconsAll(["architecture", "room", "garden", "roof", "presentation", "reception"]),
      o: { ...base0, ModeFond: "Plan d'architecte", Fond: "#FF1E3A8A", Texte: "#FFFFFFFF", Accent: "#FF93C5FD", ReglesCategories: rules("Pastille", pal("#FF93C5FD", "#FFBFDBFE", "#FFE0F2FE", "#FFFDE68A")) } },
    { label: "Structure", icons: iconsAll(["structure", "levels", "details", "existing", "demolition"]),
      o: { ...base0, ModeFond: "Grille d'architecte", Fond: "#FFF8FAFC", Texte: "#FF1F2937", Accent: "#FFF97316", ReglesCategories: rules("Gras", pal("#FFEA580C", "#FF475569", "#FFC2410C", "#FF334155")) } },
    { label: "Plomberie & CVC", icons: iconsAll(["plumbing", "ventilation", "heating", "plumbing", "ventilation"]),
      o: { ...base0, ModeFond: "Encre dans l'eau", Fond: "#FFF0FDFA", Texte: "#FF134E4A", Accent: "#FF0D9488", ReglesCategories: rules("Dégradé", pal("#FF0EA5E9", "#FF14B8A6", "#FF06B6D4", "#FFEF4444")) } },
    { label: "Électricité", icons: iconsAll(["lighting", "power", "data", "fire"]),
      o: { ...base0, ModeFond: "Constellation douce", Fond: "#FF0F172A", Texte: "#FFE2E8F0", Accent: "#FFFACC15", ReglesCategories: rules("Texte", pal("#FFFACC15", "#FFFDE047", "#FFFBBF24", "#FF38BDF8")) } },
    { label: "BIM manager", icons: iconsAll(["coordination", "validation", "publication", "study", "archive", "data"]),
      o: { ...base0, ModeFond: "Verre dépoli", Fond: "#FFEFF6FF", Texte: "#FF1E293B", Accent: "#FF22C55E", ReglesCategories: rules("Badge") } },
  );
}

if (set === "ouencore") {
  // Les 6 looks de fin du mélange configurateur + battle, à filmer dossiers dépliés.
  const fx = variants.slice();
  variants.length = 0;
  variants.push(
    { label: "Revit de base", o: { ...off }, icons: NO_ICONS },
    { ...fx[8], icons: NO_ICONS },                          // Constellation de nuit
    { ...fx[7], icons: NO_ICONS },                          // Rubans fluides
    { ...fx[9], icons: NO_ICONS },                          // Lucioles + gras coloré
    { ...fx[3], icons: NO_ICONS },                          // Plan d'architecte
    { ...fx[13], icons: NO_ICONS },                         // Dégradé animé
    { label: "Verre dépoli + icônes", o: { ...base0, ModeFond: "Verre dépoli", Fond: "#FFEFF6FF", Texte: "#FF1E293B", Accent: "#FF22C55E", ReglesCategories: rules("Dégradé") }, icons: { Enabled: true, Rules: iconRules() } },
  );
}

const writeIcons = (icons) => {
  const data = { ...iconsBase, Enabled: icons.Enabled, Rules: icons.Rules };
  writeFileSync(ICONS, JSON.stringify(data, null, 2), "utf8");
};

const write = (o) => {
  base.Arborescence = { ...original, ...o };
  const json = JSON.stringify(base, null, 2);
  writeFileSync(PREFS, (bom ? "﻿" : "") + json, "utf8");
};

// Enregistrement en parallèle (zone de zone.json), un peu plus long que le défilé.
const total = variants.length * secs + 2;
const rec = spawn("node", [join(here, "capture.mjs"), "record", take, String(total)],
  { shell: false, windowsHide: true, stdio: "inherit" });

const log = [];
let i = 0;
const t0 = Date.now();
const next = () => {
  if (i >= variants.length) return;
  const v = variants[i++];
  if (v.icons) writeIcons(v.icons);
  write(v.o);
  const at = ((Date.now() - t0) / 1000).toFixed(2);
  log.push({ t: Number(at), label: v.label });
  console.log(`${at}s  ${v.label}`);
};
setTimeout(() => { next(); const id = setInterval(() => { if (i >= variants.length) clearInterval(id); else next(); }, secs * 1000); }, 1000);

const finish = () => {
  restore();
  writeFileSync(join(here, "rushes", `${take}.txt`), log.map((l) => `${l.t}\t${l.label}`).join("\n"));
};
rec.on("exit", () => { finish(); process.exit(0); });
process.on("SIGINT", () => { rec.kill(); finish(); process.exit(0); });
