// Prototype STEP -> famille Revit : construit la description simplifiée (spec) à partir de l'analyse.
//   node spec.mjs <analyse.json> <spec.json> <chemin du STEP>
// Coordonnées Revit en mm : X = x, Y = -z, Z = y + offsetSol (le STEP SolidWorks est en Y vertical).
import { readFileSync, writeFileSync } from "node:fs";

const [, , analysePath, outPath, stepPath] = process.argv;
const d = JSON.parse(readFileSync(analysePath, "utf8"));
const inst = d.instances.map((i) => ({ ...i, short: i.path.replace(/^[^/]*\/ /, "") }));
const floor = Math.min(...inst.map((i) => i.min[1]));          // sol = point le plus bas (Y STEP)
const P = ([x, y, z]) => [x, -z, y - floor];                    // point STEP -> Revit (mm)
const D = ([x, y, z]) => [x, -z, y];                            // direction STEP -> Revit
const box = (min, max) => { const a = P(min), b = P(max); return { min: a.map((v, k) => Math.min(v, b[k])), max: a.map((v, k) => Math.max(v, b[k])) }; };

const skip = /calo|cordon de soudure|vis |\bvis\b|[ée]crou|rondelle|joint|goujon|boulon|manchon|bouchon|olive|doigt de gant/i;
const group = (p) =>
  /bruleur|br[uû]leur/i.test(p) ? "Brûleur" :
  /rampe gaz|GN14|gaz/i.test(p) ? "Gaz" :
  /recirculation/i.test(p) ? "Recirculation" :
  /carneau|fum[ée]es/i.test(p) ? "Fumées" :
  /RZ.?350|lyre|eau/i.test(p) ? "Eau" : "Corps";

const prims = [];
for (const i of inst) {
  if (skip.test(i.short)) continue;
  const dims = i.max.map((v, k) => v - i.min[k]);
  if (Math.max(...dims) < 120) continue;                        // petits accessoires écartés
  const g = group(i.path);
  // cylindre dominant aligné sur un axe ?
  let best = null;
  for (const c of i.cyl) {
    const ax = c.z.map(Math.abs).indexOf(Math.max(...c.z.map(Math.abs)));
    if (Math.abs(c.z[ax]) < 0.999) continue;
    const cross = [0, 1, 2].filter((k) => k !== ax).map((k) => dims[k]);
    // le cylindre doit remplir les deux dimensions transversales (sinon : boîte)
    if (2 * c.r < 0.7 * Math.max(...cross) || 2 * c.r > 1.05 * Math.max(...cross) + 2 || Math.min(...cross) < 0.7 * 2 * c.r) continue;
    if (!best || c.r > best.r) best = { ax, r: c.r, o: c.o };
  }
  if (best) {
    const a = [...best.o], b = [...best.o]; a[best.ax] = i.min[best.ax]; b[best.ax] = i.max[best.ax];
    prims.push({ kind: "cyl", group: g, name: i.short, a: P(a), b: P(b), r: best.r });
    // corps cylindrique posé au sol : ajoute un berceau jusqu'au sol
    if (g === "Corps" && best.r > 1000 && best.o[1] - best.r - i.min[1] > 50) {
      const span = [i.min[0], i.max[0]];
      prims.push({ kind: "box", group: g, name: "Berceau (déduit)", ...box([span[0] + 200, i.min[1], best.o[2] - 0.6 * best.r], [span[1] - 200, best.o[1], best.o[2] + 0.6 * best.r]) });
    }
  } else prims.push({ kind: "box", group: g, name: i.short, ...box(i.min, i.max) });
}

// Raccordements retenus après analyse des extrémités libres (voir ends.mjs) :
// position = face libre de la bride (STEP), direction sortante, diamètre nominal.
const C = (name, sys, dn, p, dir, note) => ({ name, system: sys, diameter: dn, p: P(p), dir: D(dir), note });
const connectors = [
  C("Départ eau chaude DN350", "SupplyHydronic", 350, [5940, 2452, 0], [0, 1, 0], "Bride DN350 PN16 libre ; la lyre « départ eau chaude » est sur ce piquage"),
  C("Retour eau chaude DN350", "ReturnHydronic", 350, [3940, 2302, 0], [0, 1, 0], "Bride DN350 PN16 libre (déduit : l'autre piquage est le départ)"),
  C("Sortie fumées DN900", "Flue", 900, [8218, 280, 0], [1, 0, 0], "Bride fumées DIN 24154 DN900 du carneau"),
  C("Arrivée gaz DN125", "Gas", 125, [4800, -1138, -1650], [1, 0, 0], "Extrémité amont de la ligne GN145 (robinet KSN DN125)"),
  C("Évent gaz DN25", "GasVent", 25, [822, -682, -1650], [1, 0, 0], "Bride DN25 de la panoplie d'évent (à confirmer)"),
];

const all = inst.flatMap((i) => [P(i.min), P(i.max)]);
const bbox = { min: [0, 1, 2].map((k) => Math.min(...all.map((p) => p[k]))), max: [0, 1, 2].map((k) => Math.max(...all.map((p) => p[k]))) };
const spec = {
  name: "Chaudière UT-L 60",
  step: stepPath,
  stepUpAxis: "Y",
  stepFloorY: floor,
  expectedBBoxMm: bbox,                // encombrement attendu une fois redressé, sol à Z = 0
  parameters: { "Modèle": "UT-L 60", "Source CAO": stepPath.split(/[\\/]/).pop() },
  connectors,
  primitives: prims,
};
writeFileSync(outPath, JSON.stringify(spec, null, 1));
const count = prims.reduce((m, p) => ((m[p.group] = (m[p.group] ?? 0) + 1), m), {});
console.log("primitives:", prims.length, count, "connecteurs:", connectors.length);
console.log("encombrement mm:", bbox.min.map(Math.round), bbox.max.map(Math.round));
