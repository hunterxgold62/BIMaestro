var __defProp = Object.defineProperty;
var __name = (target, value) => __defProp(target, "name", { value, configurable: true });

// quota.js
var METRICS = ["r2_bytes", "r2_a", "r2_b", "worker_requests", "d1_reads", "d1_writes", "d1_bytes"];
var FREE_LIMITS = { r2_bytes: 1e10, r2_a: 1e6, r2_b: 1e7, worker_requests: 1e5, d1_reads: 5e6, d1_writes: 1e5, d1_bytes: 5e8 };
var RESERVE_SQL = `UPDATE quota_state SET ${METRICS.map((k) => `${k}=${k}+?`).join(",")}
 WHERE id=1 AND EXISTS(SELECT 1 FROM quota_policy p WHERE p.id=1 AND p.enabled=1
 AND p.scope='account' AND p.threshold_percent BETWEEN 1 AND 80 AND p.verified_until>?
 AND ${METRICS.map((k) => `typeof(quota_state.${k})='integer' AND quota_state.${k} BETWEEN 0 AND ${FREE_LIMITS[k]} AND typeof(p.${k}_limit)='integer' AND p.${k}_limit BETWEEN 1 AND ${FREE_LIMITS[k]} AND quota_state.${k}+?<=CAST(p.${k}_limit*p.threshold_percent/100 AS INTEGER)`).join(" AND ")}) RETURNING id`;
async function reserve(db, costs, now = (/* @__PURE__ */ new Date()).toISOString()) {
  const values = METRICS.map((k) => costs[k] ?? 0);
  if (values.some((v) => !Number.isSafeInteger(v) || v < 0)) throw new Error("Invalid quota reservation");
  return !!await db.prepare(RESERVE_SQL).bind(...values, now, ...values).first();
}
__name(reserve, "reserve");
function requestCost(kind, bytes = 0) {
  const writes = { list: 1, versions: 1, previewDownload: 1, download: 2, previewUpload: 2, withdraw: 2, edit: 2, upload: 16 };
  if (!Object.hasOwn(writes, kind)) throw new Error("Unknown library operation");
  return {
    worker_requests: 1,
    d1_reads: 256,
    d1_writes: writes[kind],
    r2_a: kind === "upload" || kind === "previewUpload" ? 1 : 0,
    r2_b: kind === "download" || kind === "previewDownload" ? 1 : 0,
    r2_bytes: kind === "upload" || kind === "previewUpload" ? bytes : 0,
    d1_bytes: kind === "upload" ? 16384 : 0
  };
}
__name(requestCost, "requestCost");

// worker.js
var MAX_UPLOAD_BYTES = 2e7;
var MAX_PREVIEW_BYTES = 1e6;
var MAGIC = [208, 207, 17, 224, 161, 177, 26, 225];
var PNG_MAGIC = [137, 80, 78, 71, 13, 10, 26, 10];
function json(value, status = 200) {
  return Response.json(value, { status, headers: { "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } });
}
__name(json, "json");
function fail(code, message, status) {
  return json({ error: code, message }, status);
}
__name(fail, "fail");
function metadata(request, key, max, required = true) {
  let value = decodeURIComponent(request.headers.get(key) || "").normalize("NFC").trim();
  if (required && !value || value.length > max || /[\u0000-\u001f\u007f]/u.test(value)) throw new Error("metadata");
  return value;
}
__name(metadata, "metadata");
async function readBounded(request, size, timeoutMs = 9e4, magic = MAGIC) {
  if (!request.body) throw new Error("body");
  const output = new Uint8Array(size);
  let offset = 0;
  const reader = request.body.getReader();
  let timer;
  const deadline = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("upload_timeout")), timeoutMs);
  });
  try {
    while (true) {
      const { done, value } = await Promise.race([reader.read(), deadline]);
      if (done) break;
      if (offset + value.length > size) throw new Error("size");
      output.set(value, offset);
      offset += value.length;
    }
    if (offset !== size || magic.some((v, i) => output[i] !== v)) throw new Error("format");
    return output;
  } catch (e) {
    await reader.cancel().catch(() => {
    });
    throw e;
  } finally {
    clearTimeout(timer);
    reader.releaseLock();
  }
}
__name(readBounded, "readBounded");
async function sha256(bytes) {
  return Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)), (b) => b.toString(16).padStart(2, "0")).join("");
}
__name(sha256, "sha256");
function item(row, ownerHash = "") {
  return {
    id: row.id,
    name: row.name,
    category: row.category,
    revitVersion: row.revit_version,
    description: row.description,
    sizeBytes: row.size_bytes,
    createdAt: row.created_at,
    downloadCount: row.download_count,
    origin: row.origin,
    creatorName: row.creator_name || null,
    revisionNumber: row.revision_number || 1,
    changeNote: row.change_note || "",
    hasPreview: row.has_preview === 1,
    isOwner: !!ownerHash && !!row.owner_hash && ownerHash === row.owner_hash
  };
}
__name(item, "item");
var worker_default = { async fetch(request, env) {
  if (env.LIBRARY_ENABLED !== "true") return fail("library_paused", "Biblioth\xE8que temporairement ferm\xE9e.", 503);
  const url = new URL(request.url);
  let kind, id, size = 0, meta;
  if (url.pathname === "/v1/families" && request.method === "GET") kind = "list";
  else if (url.pathname === "/v1/families" && request.method === "POST") kind = "upload";
  else if (request.method === "GET" && /^\/v1\/families\/[a-f0-9]{64}\/file$/.test(url.pathname)) {
    kind = "download";
    id = url.pathname.split("/")[3];
  } else if (request.method === "GET" && /^\/v1\/families\/[a-f0-9]{64}\/preview$/.test(url.pathname)) {
    kind = "previewDownload";
    id = url.pathname.split("/")[3];
  } else if (request.method === "PUT" && /^\/v1\/families\/[a-f0-9]{64}\/preview$/.test(url.pathname)) {
    kind = "previewUpload";
    id = url.pathname.split("/")[3];
  } else if (request.method === "GET" && /^\/v1\/families\/[a-f0-9]{64}\/versions$/.test(url.pathname)) {
    kind = "versions";
    id = url.pathname.split("/")[3];
  } else if (request.method === "PATCH" && /^\/v1\/families\/[a-f0-9]{64}$/.test(url.pathname)) {
    kind = "edit";
    id = url.pathname.split("/")[3];
  } else if (request.method === "DELETE" && /^\/v1\/families\/[a-f0-9]{64}$/.test(url.pathname)) {
    kind = "withdraw";
    id = url.pathname.split("/")[3];
  } else return fail("not_found", "Adresse inconnue.", 404);
  const ownerToken = request.headers.get("X-Owner-Token") || "";
  if (ownerToken && !/^[a-f0-9]{64}$/.test(ownerToken) || !ownerToken && (kind === "upload" || kind === "previewUpload" || kind === "withdraw" || kind === "edit" || url.searchParams.get("mine") === "1"))
    return fail("invalid_owner_token", "La cl\xE9 personnelle de publication est absente ou invalide. Mettez BIMaestro \xE0 jour.", 400);
  if (kind === "upload") {
    const length = request.headers.get("Content-Length");
    size = Number(length);
    if (!length || !/^\d+$/.test(length) || !Number.isSafeInteger(size) || size < 512 || size > MAX_UPLOAD_BYTES)
      return fail("invalid_size", "Fichier attendu entre 512 octets et 20 Mo.", 413);
    try {
      meta = {
        name: metadata(request, "X-Family-Name", 120),
        category: metadata(request, "X-Family-Category", 80),
        description: metadata(request, "X-Family-Description", 1e3, false),
        creatorName: metadata(request, "X-Creator-Name", 120, false),
        changeNote: metadata(request, "X-Change-Note", 500, false),
        replaces: request.headers.get("X-Replaces-Family-ID") || "",
        version: Number(request.headers.get("X-Revit-Version")),
        origin: request.headers.get("X-Family-Origin")
      };
      if (!["personal", "ai"].includes(meta.origin)) throw new Error("origin");
      if (meta.replaces && !/^[a-f0-9]{64}$/.test(meta.replaces)) throw new Error("replaces");
      if (!Number.isInteger(meta.version) || meta.version < 2020 || meta.version > 2100 || !/[\p{L}\p{N}]/u.test(meta.category)) throw new Error("version");
    } catch {
      return fail("invalid_metadata", "Informations de famille invalides.", 400);
    }
  }
  if (kind === "edit") try {
    meta = { name: metadata(request, "X-Family-Name", 120), category: metadata(request, "X-Family-Category", 80), description: metadata(request, "X-Family-Description", 1e3, false), origin: request.headers.get("X-Family-Origin") };
    if (!["personal", "ai"].includes(meta.origin) || !/[\p{L}\p{N}]/u.test(meta.category)) throw new Error("metadata");
  } catch {
    return fail("invalid_metadata", "Informations de famille invalides.", 400);
  }
  if (kind === "previewUpload") {
    const length = request.headers.get("Content-Length");
    size = Number(length);
    if (!length || !/^\d+$/.test(length) || !Number.isSafeInteger(size) || size < PNG_MAGIC.length || size > MAX_PREVIEW_BYTES || request.headers.get("Content-Type") !== "image/png")
      return fail("invalid_preview", "Aper\xE7u PNG attendu (1 Mo maximum).", 413);
  }
  try {
    const ownerHash = ownerToken ? await sha256(new TextEncoder().encode(ownerToken)) : "";
    if (!await reserve(env.CATALOG, requestCost(kind, size))) return fail("quota_exceeded", "Biblioth\xE8que ferm\xE9e : seuil de s\xE9curit\xE9 atteint ou contr\xF4le expir\xE9.", 429);
    if (kind === "withdraw") {
      const removed = await env.CATALOG.prepare("UPDATE shared_families SET withdrawn=1 WHERE id=? AND owner_hash=? AND owner_hash<>'' RETURNING id").bind(id, ownerHash).first();
      return removed ? new Response(null, { status: 204, headers: { "Cache-Control": "no-store" } }) : fail("not_owner", "Seul l\u2019auteur peut retirer cette famille.", 403);
    }
    if (kind === "edit") {
      const updated = await env.CATALOG.prepare("UPDATE shared_families SET name=?,category=?,description=?,origin=? WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0 RETURNING *").bind(meta.name, meta.category, meta.description, meta.origin, id, ownerHash).first();
      return updated ? json({ item: item(updated, ownerHash) }) : fail("not_owner", "Seul l\u2019auteur peut modifier cette famille.", 403);
    }
    if (kind === "versions") {
      const current = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0").bind(id).first();
      if (!current) return fail("not_found", "Famille introuvable.", 404);
      const group = current.family_group_id || current.id;
      const versions = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE (id=? OR family_group_id=?) AND status='ready' AND withdrawn=0 ORDER BY revision_number DESC LIMIT 50").bind(group, group).all();
      return json({ items: versions.results.map((r) => item(r, ownerHash)) });
    }
    if (kind === "list") {
      const cursor = url.searchParams.get("cursor") || "";
      if (cursor && !/^[a-f0-9]{64}$/.test(cursor)) return fail("invalid_cursor", "Curseur invalide.", 400);
      const q = (url.searchParams.get("q") || "").toLocaleLowerCase().slice(0, 120);
      const category = (url.searchParams.get("category") || "").slice(0, 80);
      const version = url.searchParams.get("revitVersion");
      const origin = url.searchParams.get("origin") || null;
      const mine = url.searchParams.get("mine") === "1";
      if (origin !== null && !["personal", "ai"].includes(origin)) return fail("invalid_origin", "Origine invalide.", 400);
      if (version !== null && (!/^\d{4}$/.test(version) || Number(version) < 2023 || Number(version) > 2100))
        return fail("invalid_version", "Version Revit invalide.", 400);
      const page = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id>? AND superseded=0 ORDER BY id LIMIT 101").bind(cursor).all();
      const rows = page.results.slice(0, 100);
      return json({
        items: rows.filter((r) => r.status === "ready" && !r.withdrawn && (!origin || r.origin === origin) && (!mine || ownerHash && r.owner_hash === ownerHash) && (!q || `${r.name} ${r.description}`.toLocaleLowerCase().includes(q)) && (!category || r.category === category) && (!version || r.revit_version >= 2023 && r.revit_version <= Number(version))).map((r) => item(r, ownerHash)),
        nextCursor: page.results.length > 100 ? rows.at(-1).id : null
      });
    }
    if (kind === "download") {
      const row = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0").bind(id).first();
      if (!row) return fail("not_found", "Famille introuvable.", 404);
      const object = await env.FAMILIES.get(`families/${id}.rfa`);
      if (!object) return fail("unavailable", "Fichier indisponible.", 503);
      const deliver = await env.CATALOG.prepare("UPDATE shared_families SET download_count=download_count+1 WHERE id=? AND status='ready' AND withdrawn=0 RETURNING id").bind(id).first();
      if (!deliver) {
        await object.body?.cancel?.();
        return fail("not_found", "Famille retir\xE9e.", 404);
      }
      return new Response(object.body, { headers: {
        "Content-Type": "application/octet-stream",
        "Content-Length": String(object.size),
        "Content-Disposition": `attachment; filename="${id}.rfa"`,
        "Cache-Control": "no-store",
        "X-Content-Type-Options": "nosniff"
      } });
    }
    if (kind === "previewDownload") {
      const row = await env.CATALOG.prepare("SELECT id FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0 AND has_preview=1").bind(id).first();
      if (!row) return fail("not_found", "Aper\xE7u introuvable.", 404);
      const object = await env.FAMILIES.get(`previews/${id}.png`);
      if (!object) return fail("unavailable", "Aper\xE7u indisponible.", 503);
      return new Response(object.body, { headers: { "Content-Type": "image/png", "Content-Length": String(object.size), "Cache-Control": "public, max-age=300", "X-Content-Type-Options": "nosniff" } });
    }
    if (kind === "previewUpload") {
      const row = await env.CATALOG.prepare("SELECT id FROM shared_families WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0").bind(id, ownerHash).first();
      if (!row) return fail("not_owner", "Seul l\u2019auteur peut ajouter cet aper\xE7u.", 403);
      let bytes2;
      try {
        bytes2 = await readBounded(request, size, 9e4, PNG_MAGIC);
      } catch {
        return fail("invalid_preview", "Aper\xE7u PNG invalide ou taille incorrecte.", 400);
      }
      if (PNG_MAGIC.some((v, i) => bytes2[i] !== v)) return fail("invalid_preview", "Aper\xE7u PNG invalide.", 400);
      await env.FAMILIES.put(`previews/${id}.png`, bytes2, { httpMetadata: { contentType: "image/png", cacheControl: "public, max-age=300" } });
      await env.CATALOG.prepare("UPDATE shared_families SET has_preview=1 WHERE id=?").bind(id).run();
      return new Response(null, { status: 204, headers: { "Cache-Control": "no-store" } });
    }
    let bytes;
    try {
      bytes = await readBounded(request, size);
    } catch {
      return fail("invalid_file", "Fichier Revit invalide ou taille incorrecte.", 400);
    }
    id = await sha256(bytes);
    const existing = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=?").bind(id).first();
    if (existing) {
      if (existing.withdrawn) {
        if (!ownerHash || !existing.owner_hash || existing.owner_hash !== ownerHash) return fail("family_withdrawn", "Cette famille a \xE9t\xE9 retir\xE9e par son auteur et ne peut \xEAtre r\xE9activ\xE9e que par lui.", 409);
        const restored = await env.CATALOG.prepare("UPDATE shared_families SET name=?,category=?,revit_version=?,description=?,origin=?,creator_name=?,withdrawn=0 WHERE id=? AND owner_hash=? AND status='ready' RETURNING *").bind(meta.name, meta.category, meta.version, meta.description, meta.origin, meta.creatorName, id, ownerHash).first();
        return restored ? json({ item: item(restored, ownerHash), duplicate: true, restored: true }) : fail("service_unavailable", "Impossible de r\xE9activer cette famille.", 503);
      }
      if (existing.status === "ready" && ownerHash && existing.owner_hash === ownerHash && meta.creatorName && existing.creator_name !== meta.creatorName) {
        const refreshed = await env.CATALOG.prepare("UPDATE shared_families SET creator_name=? WHERE id=? AND owner_hash=? RETURNING *").bind(meta.creatorName, id, ownerHash).first();
        return json({ item: item(refreshed, ownerHash), duplicate: true, creatorUpdated: true });
      }
      return existing.status === "ready" ? json({ item: item(existing, ownerHash), duplicate: true }) : fail("upload_pending", "Ce fichier est d\xE9j\xE0 en cours de traitement.", 409);
    }
    let replacesRow = null, groupId = "", revision = 1;
    if (meta.replaces) {
      replacesRow = await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0 AND superseded=0").bind(meta.replaces, ownerHash).first();
      if (!replacesRow) return fail("not_owner", "La publication remplac\xE9e est introuvable ou ne vous appartient pas.", 403);
      groupId = replacesRow.family_group_id || replacesRow.id;
      const latest = await env.CATALOG.prepare("SELECT MAX(revision_number) AS revision FROM shared_families WHERE id=? OR family_group_id=?").bind(groupId, groupId).first();
      revision = (latest?.revision || 1) + 1;
    }
    const createdAt = (/* @__PURE__ */ new Date()).toISOString();
    const inserted = await env.CATALOG.prepare("INSERT OR IGNORE INTO shared_families(id,name,category,revit_version,description,size_bytes,created_at,status,origin,owner_hash,creator_name,family_group_id,revision_number,change_note) VALUES(?,?,?,?,?,?,?,'pending',?,?,?,?,?,?) RETURNING id").bind(id, meta.name, meta.category, meta.version, meta.description, size, createdAt, meta.origin, ownerHash, meta.creatorName, groupId, revision, meta.changeNote).first();
    if (!inserted) return fail("upload_pending", "Ce fichier est d\xE9j\xE0 en cours de traitement.", 409);
    await env.FAMILIES.put(`families/${id}.rfa`, bytes, { httpMetadata: { contentType: "application/octet-stream" } });
    await env.CATALOG.prepare("UPDATE shared_families SET status='ready' WHERE id=?").bind(id).run();
    if (replacesRow) await env.CATALOG.prepare("UPDATE shared_families SET superseded=1 WHERE id=? AND owner_hash=?").bind(replacesRow.id, ownerHash).run();
    return json({ item: { id, name: meta.name, category: meta.category, revitVersion: meta.version, description: meta.description, sizeBytes: size, createdAt, downloadCount: 0, origin: meta.origin, creatorName: meta.creatorName, revisionNumber: revision, changeNote: meta.changeNote, hasPreview: false, isOwner: true }, duplicate: false, newVersion: !!replacesRow }, 201);
  } catch {
    return fail("service_unavailable", "Service indisponible ; aucun nouvel acc\xE8s autoris\xE9.", 503);
  }
} };
export {
  MAX_PREVIEW_BYTES,
  MAX_UPLOAD_BYTES,
  worker_default as default,
  readBounded
};
//# sourceMappingURL=worker.js.map
