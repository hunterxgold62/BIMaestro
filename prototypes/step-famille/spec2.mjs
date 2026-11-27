// Version 2 : description générique d'un STEP pour Revit, sans règle propre à un fichier.
//   node spec2.mjs <analyse.json> <spec2.json> <chemin du STEP>
// - pièces : encombrement en coordonnées Revit (mm, sol à Z = 0), sous-ensemble, rôle (organe / fixation…) ;
// - raccordements candidats : extrémités circulaires libres (aucune pièce coaxiale en vis-à-vis),
//   diamètre nominal déduit, système proposé d'après les noms (à valider par l'utilisateur, puis par l'IA).
import { readFileSync, writeFileSync } from "node:fs";

const [, , analysePath, outPath, stepPath] = process.argv;
const d = JSON.parse(readFileSync(analysePath, "utf8"));
const up = d.upAxis ?? 2;
const floor = Math.min(...d.instances.map((i) => i.min[up]));
const P = up === 1 ? ([x, y, z]) => [x, -z, y - floor] : ([x, y, z]) => [x, y, z - floor];
const D = up === 1 ? ([x, y, z]) => [x, -z, y] : (v) => v;
const r1 = (v) => v.map((x) => Math.round(x * 10) / 10);
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const sub = (a, b) => a.map((v, i) => v - b[i]);
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const len = (v) => Math.hypot(...v);

// --- Rôles des pièces (multilingue FR / EN / DE) --------------------------
const RX = {
  fixation: /\bvis\b|screw|schraube|[ée]crou|\bnut\b|mutter|rondelle|washer|scheibe|goujon|\bstud|boulon|bolt|cheville|rivet/i,
  soudure: /soudure|weld|schwei(ss|ß)naht|cordon/i,
  joint: /\bjoint\b|gasket|dichtung|o-?ring/i,
  isolant: /\bcalo|isolant|insulation|d[äa]mmung|isolation/i,
  organe: /vanne|valve|ventil|robinet|soupape|clapet|sonde|sensor|f[üu]hler|transmetteur|manom|pressostat|thermom|d[ée]bitm|filtre|filter|pompe|pump|servo|actuat|moteur|motor|d[ée]tendeur|regler|r[ée]gulat/i,
};
RX.support = /support|poteau|console|bracket|halter|tr[äa]ger|passerelle|garde-?corps|railing|gel[äa]nder|platform|caillebotis|[ée]chelle|ladder/i;
RX.obturateur = /bride pleine|blind|blindflansch|bouchon|\bcap\b|kappe|obturat/i;
RX.instrument = /sonde|sensor|f[üu]hler|transmetteur|transmitter|manom|pressostat|thermom|d[ée]bitm|doigt de gant|thermowell|tauchh[üu]lse/i;
const roleOf = (n) => RX.support.test(n) ? "support" : RX.obturateur.test(n) ? "obturateur" : RX.instrument.test(n) ? "instrument" : null;
const role = (p) => roleOf(p.split(" / ").pop()) ?? (RX.fixation.test(p) ? "fixation" : RX.soudure.test(p) ? "soudure" : RX.joint.test(p) ? "joint" : RX.isolant.test(p) ? "isolant" : RX.organe.test(p.split(" / ").pop()) ? "organe" : "pièce");

const segs = (i) => i.path.split(" / ");
const inst = d.instances.map((i, k) => {
  const s = segs(i);
  return { ...i, k, group: s.length > 2 ? s[1] : s[0], role: role(i.path) };
});
const parts = inst.map((i) => {
  const a = P(i.min), b = P(i.max);
  return { name: i.name, path: i.path, group: i.group, role: i.role, min: r1(a.map((v, k) => Math.min(v, b[k]))), max: r1(a.map((v, k) => Math.max(v, b[k]))) };
});

// --- Raccordements libres ---------------------------------------------------
const OD = [[15, 21.3], [20, 26.9], [25, 33.7], [32, 42.4], [40, 48.3], [50, 60.3], [65, 76.1], [80, 88.9], [100, 114.3], [125, 139.7], [150, 168.3], [200, 219.1], [250, 273], [300, 323.9], [350, 355.6], [400, 406.4], [450, 457], [500, 508], [600, 610], [700, 711], [800, 813], [900, 914], [1000, 1016], [1200, 1219]];
const dnFromName = (s) => { const m = /(?:^|[^A-Za-z])DN\s?0*(\d{2,4})(?!\d)/i.exec(s); return m ? +m[1] : null; };
// Tout ce qui est rattaché en bout (soudure, joint, bouchon…) ferme l'extrémité ; seul l'isolant n'en ferme pas.
const blockers = inst.filter((i) => !["isolant", "fixation"].includes(i.role));
const ends = [];
for (const i of inst) {
  if (i.role !== "pièce" && i.role !== "organe") continue;
  // cercles regroupés par axe (direction + droite support)
  const axes = [];
  for (const c of i.circ ?? []) {
    let n = c.n; const big = n.map(Math.abs).indexOf(Math.max(...n.map(Math.abs))); if (n[big] < 0) n = n.map((v) => -v);
    const t = dot(c.c, n), foot = sub(c.c, n.map((v) => v * t));
    let ax = axes.find((a) => len(cross(a.n, n)) < 0.01 && len(sub(a.foot, foot)) < 2);
    if (!ax) axes.push(ax = { n, foot, cs: [] });
    ax.cs.push({ ...c, t });
  }
  for (const ax of axes) {
    // axe principal seulement : on écarte les trous de boulons et petits perçages (cercles bien plus petits
    // que le plus grand cercle de même orientation dans la pièce)
    const rMaxSameDir = Math.max(...axes.filter((a) => len(cross(a.n, ax.n)) < 0.01).flatMap((a) => a.cs.map((c) => c.r)));
    if (Math.max(...ax.cs.map((c) => c.r)) < 0.35 * rMaxSameDir) continue;
    const ts = ax.cs.map((c) => c.t), tmin = Math.min(...ts), tmax = Math.max(...ts);
    for (const [tEnd, s] of [[tmin, -1], [tmax, 1]]) {
      const atEnd = ax.cs.filter((c) => Math.abs(c.t - tEnd) < 3);
      const rOut = Math.max(...atEnd.map((c) => c.r)), rIn = Math.min(...atEnd.map((c) => c.r));
      if (rOut < 10) continue;
      const n = ax.n.map((v) => v * s);
      const center = [0, 1, 2].map((k) => ax.foot[k] + ax.n[k] * tEnd);
      // l'extrémité doit être au bord de la pièce dans cette direction (sinon c'est un alésage intérieur)
      const reach = [0, 1, 2].reduce((m, k) => m + Math.abs(n[k]) * (n[k] > 0 ? i.max[k] : i.min[k]) * Math.sign(n[k]), 0);
      // (une poignée ou un volant peut dépasser : on garde l'extrémité mais on la signale « en retrait »)
      const recessed = dot(center, n) < reach - 5;
      // libre si aucune autre pièce n'a de cercle coaxial dans ce plan (bride, tube ou tubulure en vis-à-vis)
      // la pièce en vis-à-vis doit avoir de la matière au-delà du plan (sinon c'est un tube emboîté en retrait
      // dans une bride, pas un raccord)
      const beyond = (o) => [0, 1, 2].reduce((m, k) => m + Math.max((o.min[k] - center[k]) * n[k], (o.max[k] - center[k]) * n[k]), 0) > 3;
      const mated = blockers.some((o) => o !== i &&
        o.min.every((v, k) => v - rOut - 15 <= center[k]) && o.max.every((v, k) => v + rOut + 15 >= center[k]) && beyond(o) &&
        (o.circ ?? []).some((c) => len(cross(c.n, n)) < 0.02 && Math.abs(dot(sub(c.c, center), n)) < 12 &&
          len(cross(sub(c.c, center), n)) < 3 && c.r > 0.5 * rIn && c.r < 1.6 * rOut));
      if (mated) continue;
      // diamètre nominal : nom de la pièce, sinon cercle le plus proche d'un diamètre extérieur normalisé
      let dn = dnFromName(i.name), how = "nom";
      if (!dn) {
        let best = null;
        for (const c of ax.cs) for (const [n2, od] of OD) { const e = Math.abs(2 * c.r - od) / od; if (e < 0.03 && (!best || e < best.e)) best = { dn: n2, e }; }
        dn = best?.dn ?? null; how = "géométrie";
      }
      if (!dn) { const od = 2 * rIn; dn = OD.reduce((b, x) => Math.abs(x[1] - od) < Math.abs(b[1] - od) ? x : b)[0]; how = "alésage (approx.)"; }
      ends.push({ part: i, center, n, rOut, rIn, dn, how, recessed });
    }
  }
}

// --- Système proposé (mots-clés du chemin et des pièces voisines du même sous-ensemble) ---
const SYS = [
  ["Fumées", "Flue", /fum[ée]e|carneau|chemin[ée]e|abgas|rauchgas|flue|exhaust|chimney/i],
  ["Gaz", "Gas", /\bgaz\b|\bgas\b|GN\d|rampe gaz|gasstrecke/i],
  ["Soupape (échappement)", "Other", /soupape|safety|sicherheits|relief/i],
  ["Évent", "Other", /[ée]vent|\bvent\b|entl[üu]ft/i],
  ["Vidange", "Other", /vidange|drain|entleer/i],
  ["Départ", "SupplyHydronic", /d[ée]part|vorlauf|supply|aller\b|\bflow\b/i],
  ["Retour", "ReturnHydronic", /retour|r[üu]cklauf|return/i],
  ["Eau", "Other", /\beau\b|water|wasser|hydraul/i],
];
const groupText = new Map();
for (const i of inst) groupText.set(i.group, (groupText.get(i.group) ?? "") + " " + i.name);
function propose(e) {
  for (const [label, sys, rx] of SYS) if (rx.test(e.part.path)) return { label, sys, why: "nom de la pièce" };
  for (const [label, sys, rx] of SYS) if (rx.test(groupText.get(e.part.group))) return { label, sys, why: "sous-ensemble « " + e.part.group + " »" };
  return { label: "À qualifier", sys: "Other", why: "aucun indice dans les noms" };
}
// doublons (même point, même direction) : on garde le plus grand
const dedup = [];
for (const e of ends.sort((a, b) => b.rOut - a.rOut))
  if (!dedup.some((u) => len(sub(u.center, e.center)) < 25 && dot(u.n, e.n) > 0.99)) dedup.push(e);
// deux extrémités proches et opposées sur le même axe = un perçage traversant (paroi), pas un raccordement
const uniq = dedup.filter((e) => !dedup.some((o) => o !== e && o.part === e.part && dot(o.n, e.n) < -0.99 &&
  len(sub(o.center, e.center)) < Math.max(60, 0.5 * e.rOut) && len(cross(sub(o.center, e.center), e.n)) < 3));
// Dégagement : un vrai raccordement donne sur l'extérieur. On lance un rayon depuis l'extrémité et on regarde
// s'il traverse l'encombrement d'une autre pièce de taille raisonnable.
const totalVol = (() => { const a = inst.flatMap((i) => [i.min, i.max]); return [0, 1, 2].reduce((m, k) => m * (Math.max(...a.map((p) => p[k])) - Math.min(...a.map((p) => p[k]))), 1); })();
const vol = (o) => [0, 1, 2].reduce((m, k) => m * (o.max[k] - o.min[k]), 1);
const obstacles = inst.filter((o) => o.role !== "isolant" && o.role !== "fixation" && vol(o) < 0.2 * totalVol);
function clear(e) {
  const L = Math.min(600, Math.max(150, 3 * e.rOut));
  for (let s = 20; s <= L; s += 20) {
    const q = e.center.map((v, k) => v + e.n[k] * s);
    if (obstacles.some((o) => o !== e.part && o.min.every((v, k) => v - 1 <= q[k]) && o.max.every((v, k) => v + 1 >= q[k]))) return false;
  }
  return true;
}
const scored = uniq.map((e) => {
  const p = propose(e);
  const flange = /bride|flange|flansch|brida/i.test(e.part.name) || e.rOut > 1.4 * e.rIn;
  const free = clear(e);
  const score = (flange ? 3 : 0) + (free ? 3 : 0) + (e.recessed ? -2 : 1) + (p.label !== "À qualifier" ? 1 : 0) + (e.part.role === "pièce" ? 1 : 0) + Math.log10(e.dn);
  // présélection : bride dégagée d'au moins DN25
  const keep = flange && free && e.part.role === "pièce" && (e.recessed ? e.dn >= 100 : e.dn >= 25);
  return {
    keep, score: Math.round(score * 10) / 10, name: `${p.label} DN${e.dn}`, label: p.label, system: p.sys, diameter: e.dn,
    p: r1(P(e.center)), dir: D(e.n).map((v) => Math.round(v * 1e4) / 1e4),
    part: e.part.name, why: `${p.why} ; DN par ${e.how}${flange ? " ; bride" : ""}${free ? " ; dégagé" : " ; encombré"}${e.recessed ? " ; en retrait" : ""}`,
  };
});
// on ne propose que les 40 meilleurs (le reste est presque toujours de la géométrie interne d'organes)
const candidates = scored.sort((a, b) => b.score - a.score).slice(0, 40);
console.log(`extrémités libres trouvées: ${scored.length} (40 proposées)`);

const all = parts.flatMap((p) => [p.min, p.max]);
const bbox = { min: [0, 1, 2].map((k) => Math.min(...all.map((p) => p[k]))), max: [0, 1, 2].map((k) => Math.max(...all.map((p) => p[k]))) };
const spec = {
  version: 2, name: stepPath.split(/[\\/]/).pop().replace(/\.(step|stp)$/i, ""), step: stepPath,
  origin: d.origin, upAxis: "XYZ"[up], expectedBBoxMm: bbox,
  parameters: { "Source CAO": stepPath.split(/[\\/]/).pop(), "Logiciel CAO": d.origin.replace(/'/g, "").trim() },
  parts, connectors: candidates,
};
writeFileSync(outPath, JSON.stringify(spec));
console.log(`pièces: ${parts.length}  rôles:`, parts.reduce((m, p) => ((m[p.role] = (m[p.role] ?? 0) + 1), m), {}));
console.log("encombrement mm:", bbox.min.map(Math.round), bbox.max.map(Math.round), "axe vertical STEP:", "XYZ"[up]);
console.log(`raccordements libres: ${candidates.length}, présélectionnés: ${candidates.filter((c) => c.keep).length}`);
for (const c of candidates.slice(0, 40)) console.log((c.keep ? "[x] " : "[ ] ") + c.name.padEnd(28), c.p.map(Math.round).join(",").padEnd(20), c.dir.map(Math.round).join(","), "|", c.part.slice(0, 50), "|", c.why);
