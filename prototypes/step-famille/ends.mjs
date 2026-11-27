// Cherche les extrémités de tuyauterie libres (piquages candidats) dans analyse.json.
import { readFileSync, writeFileSync } from "node:fs";
const d = JSON.parse(readFileSync(new URL("./analyse.json", import.meta.url)));
const inst = d.instances.map((i, k) => ({ ...i, k, short: i.path.replace("D195-19340-CL-Ensemble UT-L60_B / ", "") }));
const ignore = (i) => /calo|cordon de soudure|vis |joint|ecrou|écrou|rondelle|support|poteau/i.test(i.path);
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const inside = (p, i, tol) => p.every((v, a) => v >= i.min[a] - tol && v <= i.max[a] + tol);
const ends = [];
for (const i of inst) {
  if (ignore(i) || !i.cyl.length) continue;
  // groupe les cylindres coaxiaux, garde le rayon le plus grand de chaque axe
  const axes = [];
  for (const c of i.cyl) {
    const z = c.z.map(Math.abs).indexOf(Math.max(...c.z.map(Math.abs)));
    if (Math.abs(c.z[z]) < 0.999) continue; // axes alignés seulement (pour le proto)
    const key = [z, ...[0, 1, 2].filter((a) => a !== z).map((a) => Math.round(c.o[a] / 5))].join(":");
    const ax = axes.find((x) => x.key === key);
    if (ax) { ax.r = Math.max(ax.r, c.r); ax.rs.add(c.r); } else axes.push({ key, axis: z, o: c.o, r: c.r, rs: new Set([c.r]) });
  }
  for (const ax of axes) {
    if (ax.r < 10) continue;
    for (const s of [-1, 1]) {
      const p = [...ax.o]; p[ax.axis] = s < 0 ? i.min[ax.axis] : i.max[ax.axis];
      // l'extrémité doit être dans l'emprise radiale de la pièce
      const out = [...p]; out[ax.axis] += s * 15;
      const blocked = inst.some((o) => o !== i && !/calo/i.test(o.path) && inside(out, o, 0));
      if (!blocked) ends.push({ part: i.short, axis: "XYZ"[ax.axis], sign: s, p: p.map(Math.round), rMax: ax.r, rs: [...ax.rs].sort((a, b) => a - b) });
    }
  }
}
// Filtre : extrémités sur des pièces de tuyauterie / brides / vannes
const keep = ends.filter((e) => /bride|pipe|dn\d|carneau|manchette|vanne|robinet|soupape|lyre|tube|réduction|reduction|coude|té |piquage|event|évent|RS 251|GF 60|KSN|recirculation|olive/i.test(e.part));
writeFileSync(new URL("./ends.json", import.meta.url), JSON.stringify(keep, null, 1));
for (const e of keep.sort((a, b) => b.rMax - a.rMax)) console.log(("r" + e.rMax).padEnd(8), (e.sign > 0 ? "+" : "-") + e.axis, e.p.join(",").padEnd(18), e.part.slice(0, 100));
