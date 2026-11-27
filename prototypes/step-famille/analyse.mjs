// Analyse locale d'un STEP AP203/AP214 : arbre d'assemblage, placements, encombrements, cylindres.
//   node analyse.mjs <fichier.step> <sortie.json>
import { readFileSync, writeFileSync } from "node:fs";

const [, , file, outFile] = process.argv;
const text = readFileSync(file, "latin1");
const data = text.slice(text.indexOf("DATA;") + 5, text.lastIndexOf("ENDSEC;"));

// --- Parse des entités -------------------------------------------------
const ents = new Map(); // id -> { type, args } ou { complex: [{type,args}] }
const re = /#(\d+)\s*=\s*([\s\S]*?);\s*(?=#\d+\s*=|$)/g;
// Tokeniseur d'arguments
function parseArgs(s, i = 0) {
  // s[i] === '('
  const out = []; i++;
  let cur = null;
  while (i < s.length) {
    const c = s[i];
    if (c === " " || c === "\n" || c === "\r" || c === "\t") { i++; continue; }
    if (c === ")") { return [out, i + 1]; }
    if (c === ",") { i++; continue; }
    if (c === "(") { const [arr, j] = parseArgs(s, i); out.push(arr); i = j; continue; }
    if (c === "'") { let j = i + 1, str = ""; while (j < s.length) { if (s[j] === "'") { if (s[j + 1] === "'") { str += "'"; j += 2; continue; } break; } str += s[j++]; } out.push({ s: str }); i = j + 1; continue; }
    if (c === "#") { let j = i + 1; while (/\d/.test(s[j])) j++; out.push({ r: +s.slice(i + 1, j) }); i = j; continue; }
    if (c === ".") { let j = s.indexOf(".", i + 1); out.push({ e: s.slice(i + 1, j) }); i = j + 1; continue; }
    if (/[A-Z_]/i.test(c)) { // typed param e.g. LENGTH_MEASURE(1.)
      let j = i; while (/[A-Z0-9_]/i.test(s[j])) j++;
      const name = s.slice(i, j); while (s[j] === " ") j++;
      if (s[j] === "(") { const [arr, k] = parseArgs(s, j); out.push({ t: name, v: arr }); i = k; } else { out.push({ k: name }); i = j; }
      continue;
    }
    let j = i; while (j < s.length && !",)".includes(s[j])) j++;
    const tok = s.slice(i, j).trim(); out.push(tok === "$" || tok === "*" ? null : Number(tok)); i = j;
  }
  return [out, i];
}
function parseBody(body) {
  body = body.trim();
  if (body.startsWith("(")) { // complexe
    const parts = []; let i = 1;
    while (i < body.length) {
      while (" \n\r\t".includes(body[i])) i++;
      if (body[i] === ")") break;
      let j = i; while (/[A-Z0-9_]/i.test(body[j])) j++;
      const type = body.slice(i, j); while (body[j] === " ") j++;
      const [args, k] = parseArgs(body, j); parts.push({ type, args }); i = k;
    }
    return { complex: parts };
  }
  const p = body.indexOf("(");
  const type = body.slice(0, p).trim();
  const [args] = parseArgs(body, p);
  return { type, args };
}
let m;
const rawById = new Map();
for (const line of data.split(/;\s*\r?\n/)) {
  const mm = /^\s*#(\d+)\s*=\s*([\s\S]*)$/.exec(line);
  if (mm) rawById.set(+mm[1], mm[2]);
}
const get = (id) => { let e = ents.get(id); if (!e) { const raw = rawById.get(id); if (raw == null) return null; e = parseBody(raw); ents.set(id, e); } return e; };
const byType = new Map();
for (const [id, raw] of rawById) {
  const t = raw.trimStart().startsWith("(") ? "__COMPLEX" : raw.slice(0, raw.indexOf("(")).trim();
  if (!byType.has(t)) byType.set(t, []); byType.get(t).push(id);
}
const ids = (t) => byType.get(t) ?? [];
const ref = (a) => a?.r;

// --- Géométrie de base -------------------------------------------------
const pt = (id) => get(id).args[1];
const dir = (id) => { const v = get(id).args[1]; const n = Math.hypot(...v); return v.map((x) => x / n); };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
function axisFrame(id) { // AXIS2_PLACEMENT_3D -> {o, x, y, z}
  const a = get(id).args; const o = pt(a[1].r);
  const z = a[2] ? dir(a[2].r) : [0, 0, 1];
  let x = a[3] ? dir(a[3].r) : (Math.abs(z[0]) < 0.9 ? [1, 0, 0] : [0, 1, 0]);
  const d = x[0] * z[0] + x[1] * z[1] + x[2] * z[2]; x = x.map((v, i) => v - d * z[i]); const n = Math.hypot(...x); x = x.map((v) => v / n);
  return { o, x, y: cross(z, x), z };
}
// Matrices 4x4 en lignes : M = [R | t]
const fromFrame = (f) => [[f.x[0], f.y[0], f.z[0], f.o[0]], [f.x[1], f.y[1], f.z[1], f.o[1]], [f.x[2], f.y[2], f.z[2], f.o[2]], [0, 0, 0, 1]];
const mul = (A, B) => A.map((r, i) => [0, 1, 2, 3].map((j) => r[0] * B[0][j] + r[1] * B[1][j] + r[2] * B[2][j] + r[3] * B[3][j]));
const inv = (M) => { const R = [[M[0][0], M[1][0], M[2][0]], [M[0][1], M[1][1], M[2][1]], [M[0][2], M[1][2], M[2][2]]]; const t = [M[0][3], M[1][3], M[2][3]]; const nt = R.map((r) => -(r[0] * t[0] + r[1] * t[1] + r[2] * t[2])); return [[...R[0], nt[0]], [...R[1], nt[1]], [...R[2], nt[2]], [0, 0, 0, 1]]; };
const I = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]];
const apply = (M, p) => [0, 1, 2].map((i) => M[i][0] * p[0] + M[i][1] * p[1] + M[i][2] * p[2] + M[i][3]);
const applyDir = (M, p) => [0, 1, 2].map((i) => M[i][0] * p[0] + M[i][1] * p[1] + M[i][2] * p[2]);

// --- Produits -----------------------------------------------------------
const pdToProduct = new Map(); // PRODUCT_DEFINITION -> nom
for (const id of ids("PRODUCT_DEFINITION")) {
  const pdf = get(get(id).args[2].r); const prod = get(pdf.args[2].r);
  pdToProduct.set(id, prod.args[1].s || prod.args[0].s);
}
const pdsOf = new Map(); // definition (PD ou NAUO) -> PDS
for (const id of ids("PRODUCT_DEFINITION_SHAPE")) pdsOf.set(get(id).args[2].r, id);
const shapeRepOfPds = new Map();
for (const id of ids("SHAPE_DEFINITION_REPRESENTATION")) { const a = get(id).args; shapeRepOfPds.set(a[0].r, a[1].r); }
// Relations shape_rep <-> brep reps (SHAPE_REPRESENTATION_RELATIONSHIP simple)
const repLinks = new Map();
for (const id of ids("SHAPE_REPRESENTATION_RELATIONSHIP")) { const a = get(id).args; const k = a[2].r; if (!repLinks.has(k)) repLinks.set(k, []); repLinks.get(k).push(a[3].r); if (!repLinks.has(a[3].r)) repLinks.set(a[3].r, []); repLinks.get(a[3].r).push(k); }
// Transformations d'occurrence
const nauoTransform = new Map();
for (const id of ids("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION")) {
  const a = get(id).args; const rel = get(a[0].r); const pds = get(a[1].r);
  const nauo = pds.args[2].r;
  const parts = rel.complex; const rr = parts.find((p) => p.type === "REPRESENTATION_RELATIONSHIP").args;
  const tr = parts.find((p) => p.type === "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION").args[0].r;
  const idt = get(tr).args;
  nauoTransform.set(nauo, { rep1: rr[2].r, rep2: rr[3].r, a1: idt[2].r, a2: idt[3].r });
}
const children = new Map(); const isChild = new Set();
for (const id of ids("NEXT_ASSEMBLY_USAGE_OCCURRENCE")) {
  const a = get(id).args; const parent = a[3].r, child = a[4].r;
  if (!children.has(parent)) children.set(parent, []); children.get(parent).push({ nauo: id, child }); isChild.add(child);
}
const roots = [...pdToProduct.keys()].filter((p) => !isChild.has(p));

// --- Géométrie des solides : points, cylindres -------------------------
const breps = new Set([...ids("MANIFOLD_SOLID_BREP"), ...ids("BREP_WITH_VOIDS"), ...ids("SHELL_BASED_SURFACE_MODEL")]);
const geomCache = new Map();
function brepGeom(id) {
  if (geomCache.has(id)) return geomCache.get(id);
  const pts = [], cyl = [], circ = []; const seen = new Set(), circKeys = new Set();
  const stack = [id];
  while (stack.length) {
    const cur = stack.pop(); if (seen.has(cur)) continue; seen.add(cur);
    const e = get(cur); if (!e || e.complex) continue;
    if (e.type === "VERTEX_POINT") { pts.push(pt(e.args[1].r)); continue; }
    if (e.type === "CYLINDRICAL_SURFACE") { const f = axisFrame(e.args[1].r); cyl.push({ o: f.o, z: f.z, r: e.args[2] }); continue; }
    if (e.type === "EDGE_CURVE") {
      // Un tube n'a qu'un sommet par cercle : pour une arête circulaire fermée, on prend l'encombrement du cercle.
      const [, v1, v2, curve] = e.args;
      stack.push(v1.r, v2.r);
      const c = get(curve.r);
      if (c?.type === "CIRCLE" && get(c.args[1].r).type === "AXIS2_PLACEMENT_3D") {
        const f = axisFrame(c.args[1].r), r = c.args[2];
        if (v1.r === v2.r) for (const a of [0, 1, 2]) { const ext = r * Math.sqrt(Math.max(0, 1 - f.z[a] * f.z[a])); for (const s of [-1, 1]) { const p = [...f.o]; p[a] += s * ext; pts.push(p); } }
        // cercles (complets ou en arcs) : servent à repérer les extrémités libres
        const key = f.o.map((v) => Math.round(v * 2)).join(",") + ":" + Math.round(r * 2);
        if (r >= 8 && !circKeys.has(key)) { circKeys.add(key); circ.push({ c: f.o, n: f.z, r }); }
      }
      continue;
    }
    if (["CARTESIAN_POINT", "DIRECTION", "B_SPLINE_SURFACE_WITH_KNOTS", "B_SPLINE_CURVE_WITH_KNOTS", "LINE", "CIRCLE", "PLANE", "AXIS2_PLACEMENT_3D", "VECTOR", "PCURVE"].includes(e.type)) continue;
    const walk = (arr) => { for (const v of arr) { if (v?.r) stack.push(v.r); else if (Array.isArray(v)) walk(v); } };
    walk(e.args);
  }
  const g = { pts, cyl, circ }; geomCache.set(id, g); return g;
}
function repItems(rep, seen = new Set()) { // tous les breps d'une représentation (+ liées)
  if (seen.has(rep)) return []; seen.add(rep);
  const e = get(rep); const out = [];
  const items = e.complex ? [] : e.args[1];
  for (const it of items ?? []) if (breps.has(it.r)) out.push(it.r);
  for (const l of repLinks.get(rep) ?? []) out.push(...repItems(l, seen));
  return out;
}
function repPlacementOf(rep, axId) { return fromFrame(axisFrame(axId)); }

// --- Parcours de l'arbre ------------------------------------------------
const instances = [];
function visit(pd, M, path) {
  const name = pdToProduct.get(pd);
  const kids = children.get(pd) ?? [];
  const rep = shapeRepOfPds.get(pdsOf.get(pd));
  const solids = rep ? repItems(rep) : [];
  if (solids.length) {
    let pts = [], cyl = [], circ = [];
    for (const b of solids) {
      const g = brepGeom(b); pts.push(...g.pts.map((p) => apply(M, p)));
      cyl.push(...g.cyl.map((c) => ({ o: apply(M, c.o), z: applyDir(M, c.z), r: c.r })));
      circ.push(...g.circ.map((c) => ({ c: apply(M, c.c), n: applyDir(M, c.n), r: c.r })));
    }
    if (pts.length) {
      const min = [0, 1, 2].map((i) => Math.min(...pts.map((p) => p[i]))), max = [0, 1, 2].map((i) => Math.max(...pts.map((p) => p[i])));
      const r1 = (v) => v.map((x) => +x.toFixed(1)), r3 = (v) => v.map((x) => +x.toFixed(4));
      instances.push({
        path: [...path, name].join(" / "), name, min, max, solids: solids.length,
        cyl: cyl.filter((c) => c.r >= 8).map((c) => ({ o: r1(c.o), z: r3(c.z), r: +c.r.toFixed(1) })),
        circ: circ.map((c) => ({ c: r1(c.c), n: r3(c.n), r: +c.r.toFixed(1) })),
      });
    }
  }
  for (const k of kids) {
    const t = nauoTransform.get(k.nauo);
    let Mk = M;
    if (t) {
      const childRep = shapeRepOfPds.get(pdsOf.get(k.child));
      const [inParent, inChild] = t.rep2 === childRep ? [t.a1, t.a2] : [t.a2, t.a1];
      Mk = mul(M, mul(fromFrame(axisFrame(inParent)), inv(fromFrame(axisFrame(inChild)))));
    }
    visit(k.child, Mk, [...path, name]);
  }
}
for (const r of roots) visit(r, I, []);
const all = instances.flatMap((i) => [i.min, i.max]);
const gmin = [0, 1, 2].map((k) => Math.min(...all.map((p) => p[k]))), gmax = [0, 1, 2].map((k) => Math.max(...all.map((p) => p[k])));
// Logiciel d'origine (sert à deviner l'axe vertical : Y chez SolidWorks / Inventor / Creo, Z ailleurs)
const header = text.slice(0, text.indexOf("ENDSEC;"));
const origin = (/FILE_NAME\s*\(([\s\S]*?)\)\s*;/.exec(header)?.[1] ?? "").match(/'[^']*'/g)?.slice(-3).join(" ") ?? "";
const upAxis = /solidworks|inventor|creo|pro\/?e|sw ?step/i.test(origin) ? 1 : 2;
writeFileSync(outFile, JSON.stringify({ origin, upAxis, roots: roots.map((r) => pdToProduct.get(r)), bbox: { min: gmin, max: gmax }, instances }));
console.log("logiciel d'origine:", origin, "-> axe vertical", "XYZ"[upAxis]);
console.log("racines:", roots.map((r) => pdToProduct.get(r)));
console.log("instances avec géométrie:", instances.length);
console.log("encombrement global mm:", gmin.map((v) => v.toFixed(0)), gmax.map((v) => v.toFixed(0)), "=> dims", gmax.map((v, i) => (v - gmin[i]).toFixed(0)));
