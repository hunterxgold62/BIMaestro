var __defProp = Object.defineProperty;
var __name = (target, value) => __defProp(target, "name", { value, configurable: true });

// ../family-library/quota.js
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
function modelRequestCost(kind, { bytes = 0, markerBytes = 0, count = 1 } = {}) {
  if (!["upload", "download", "verify", "delete"].includes(kind) || ![bytes, markerBytes, count].every(Number.isSafeInteger) || bytes < 0 || markerBytes < 0 || count < 1 || count > 1e3)
    throw new Error("Invalid model reservation");
  return {
    worker_requests: 1,
    d1_reads: 16,
    d1_writes: 1,
    r2_bytes: kind === "upload" ? bytes + markerBytes : 0,
    r2_a: kind === "upload" ? markerBytes > 0 ? 2 : 1 : kind === "delete" ? 1 : 0,
    r2_b: kind === "download" ? 1 : kind === "verify" ? count : 0
  };
}
__name(modelRequestCost, "modelRequestCost");

// worker.js
var shareApi = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share";
var supabaseKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo";
var allowedOrigins = /* @__PURE__ */ new Set(["https://viewer.bimaestro.fr", "http://localhost:3000", "http://localhost:3001"]);
function headers(origin) {
  return {
    "Access-Control-Allow-Origin": allowedOrigins.has(origin) ? origin : "https://viewer.bimaestro.fr",
    "Access-Control-Allow-Headers": "content-type, authorization",
    "Access-Control-Allow-Methods": "POST, PUT, OPTIONS",
    "Access-Control-Max-Age": "600",
    "Cache-Control": "no-store",
    "Vary": "Origin",
    "X-Content-Type-Options": "nosniff"
  };
}
__name(headers, "headers");
function failure(origin, status, message) {
  return new Response(message, { status, headers: headers(origin) });
}
__name(failure, "failure");
async function authorize(body, bearer) {
  const response = await fetch(shareApi, {
    method: "POST",
    headers: { "content-type": "application/json", apikey: supabaseKey, ...bearer ? { authorization: bearer } : {} },
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(`Supabase ${response.status}`);
  return response.json();
}
__name(authorize, "authorize");
async function sha256(bytes) {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}
__name(sha256, "sha256");
function validTarget(id, revision, name) {
  return /^[0-9a-f-]{36}$/.test(id || "") && Number.isSafeInteger(revision) && revision > 0 && /^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name || "");
}
__name(validTarget, "validTarget");
function nameMarker(publicationName) {
  const readable = String(publicationName || "").normalize("NFKD").replace(/[\u0300-\u036f]/g, "").replace(/[^A-Za-z0-9._ -]/g, " ").replace(/\s+/g, " ").trim().slice(0, 80);
  return `00 - ${readable || "Maquette"}.txt`;
}
__name(nameMarker, "nameMarker");
async function readModelBody(request, expectedBytes) {
  if (!request.body) throw new Error("Missing upload");
  const reader = request.body.getReader();
  const bytes = new Uint8Array(expectedBytes);
  let offset = 0, timer;
  const deadline = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("Upload timeout")), 9e4);
  });
  try {
    while (true) {
      const { done, value } = await Promise.race([reader.read(), deadline]);
      if (done) break;
      if (offset + value.length > expectedBytes) throw new Error("Upload too large");
      bytes.set(value, offset);
      offset += value.length;
    }
    if (offset !== expectedBytes) throw new Error("Incomplete upload");
    return bytes;
  } catch (error) {
    void reader.cancel().catch(() => {
    });
    throw error;
  } finally {
    clearTimeout(timer);
    reader.releaseLock();
  }
}
__name(readModelBody, "readModelBody");
var worker_default = {
  async fetch(request, env) {
    const origin = request.headers.get("Origin") || "";
    const path = new URL(request.url).pathname;
    if (request.method === "OPTIONS" && ["/asset", "/upload", "/verify", "/delete"].includes(path)) return new Response(null, { status: 204, headers: headers(origin) });
    if (origin && !allowedOrigins.has(origin)) return failure(origin, 403, "Origine refus\xE9e");
    if (env.QUOTA_ENABLED !== "true") return failure(origin, 503, "Service temporairement ferm\xE9");
    try {
      if (request.method === "PUT" && path === "/upload") {
        const url = new URL(request.url);
        const publicationId = url.searchParams.get("publicationId");
        const revision2 = Number(url.searchParams.get("revision"));
        const name2 = url.searchParams.get("name");
        if (!validTarget(publicationId, revision2, name2)) return failure(origin, 400, "Fichier invalide");
        const bearer = request.headers.get("authorization");
        if (!bearer?.startsWith("Bearer ")) return failure(origin, 401, "Licence requise");
        let access2;
        try {
          access2 = await authorize({ action: "r2-upload-authorize", publicationId, revision: revision2, name: name2 }, bearer);
        } catch {
          return failure(origin, 403, "Publication non autoris\xE9e");
        }
        const asset2 = access2.asset;
        if (access2.publicationId !== publicationId || access2.revision !== revision2 || asset2?.name !== name2 || !Number.isSafeInteger(asset2.bytes) || asset2.bytes < 1 || asset2.bytes > 48 * 1024 * 1024 || !/^[0-9a-f]{64}$/.test(asset2.sha256 || "")) return failure(origin, 403, "Manifeste invalide");
        if (Number(request.headers.get("content-length")) > asset2.bytes) return failure(origin, 413, "Fichier trop volumineux");
        let markerKey = "", markerBody = "", markerBytes = 0;
        if (name2 === "index.zip" && typeof access2.publicationName === "string") {
          if (access2.publicationName.length > 1e3) return failure(origin, 403, "Nom de publication invalide");
          markerKey = `${publicationId}/${revision2}/${nameMarker(access2.publicationName)}`;
          markerBody = `Maquette : ${access2.publicationName}
Publication : ${publicationId}
R\xE9vision : ${revision2}
`;
          markerBytes = new TextEncoder().encode(markerBody).byteLength;
        }
        if (!await reserve(env.CATALOG, modelRequestCost("upload", { bytes: asset2.bytes, markerBytes })))
          return failure(origin, 429, "Quota commun atteint ou contr\xF4le expir\xE9");
        let bytes;
        try {
          bytes = await readModelBody(request, asset2.bytes);
        } catch {
          return failure(origin, 413, "Envoi incomplet, trop volumineux ou expir\xE9");
        }
        if (bytes.byteLength !== asset2.bytes || await sha256(bytes) !== asset2.sha256) return failure(origin, 409, "Int\xE9grit\xE9 incorrecte");
        const key = `${publicationId}/${revision2}/${name2}`;
        const stored = await env.MODELS.put(key, bytes, { customMetadata: { sha256: asset2.sha256 } });
        if (!stored || stored.size !== asset2.bytes) return failure(origin, 503, "Envoi R2 incomplet");
        if (markerKey) {
          await env.MODELS.put(markerKey, markerBody);
        }
        return new Response(null, { status: 201, headers: headers(origin) });
      }
      if (request.method === "POST" && path === "/verify") {
        let input2;
        try {
          input2 = await request.json();
        } catch {
          return failure(origin, 400, "Requ\xEAte invalide");
        }
        const { publicationId, revision: revision2, names } = input2 || {};
        if (!/^[0-9a-f-]{36}$/.test(publicationId || "") || !Number.isSafeInteger(revision2) || revision2 < 1 || !Array.isArray(names) || names.length < 1 || names.length > 32 || names.some((name2) => !/^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name2))) return failure(origin, 400, "Lot invalide");
        const bearer = request.headers.get("authorization");
        if (!bearer?.startsWith("Bearer ")) return failure(origin, 401, "Licence requise");
        let access2;
        try {
          access2 = await authorize({ action: "r2-verify-authorize", publicationId, revision: revision2, names }, bearer);
        } catch {
          return failure(origin, 403, "Publication non autoris\xE9e");
        }
        if (access2.publicationId !== publicationId || access2.revision !== revision2 || access2.assets?.length !== names.length) return failure(origin, 403, "Manifeste invalide");
        if (new Set(access2.assets.map((asset2) => asset2.name)).size !== names.length || access2.assets.some((asset2) => !names.includes(asset2.name) || !Number.isSafeInteger(asset2.bytes) || asset2.bytes < 1 || !/^[0-9a-f]{64}$/.test(asset2.sha256 || ""))) return failure(origin, 403, "Manifeste invalide");
        if (!await reserve(env.CATALOG, modelRequestCost("verify", { count: access2.assets.length })))
          return failure(origin, 429, "Quota commun atteint ou contr\xF4le expir\xE9");
        const objects = await Promise.all(access2.assets.map((asset2) => env.MODELS.head(`${publicationId}/${revision2}/${asset2.name}`)));
        const valid = objects.filter((object2, index) => object2?.size === access2.assets[index].bytes && object2.customMetadata?.sha256 === access2.assets[index].sha256);
        return new Response(
          JSON.stringify({ expected: names.length, valid: valid.length, bytes: valid.reduce((sum, item) => sum + item.size, 0) }),
          { headers: { ...headers(origin), "content-type": "application/json" } }
        );
      }
      if (request.method === "POST" && path === "/delete") {
        let input2;
        try {
          input2 = await request.json();
        } catch {
          return failure(origin, 400, "Requ\xEAte invalide");
        }
        const { publicationId, revision: revision2, names } = input2 || {};
        if (!/^[0-9a-f-]{36}$/.test(publicationId || "") || !Number.isSafeInteger(revision2) || revision2 < 1 || !Array.isArray(names) || names.length < 1 || names.length > 100 || names.some((name2) => !/^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name2))) return failure(origin, 400, "Lot invalide");
        const bearer = request.headers.get("authorization");
        if (!bearer?.startsWith("Bearer ")) return failure(origin, 401, "Acc\xE8s refus\xE9");
        let access2;
        try {
          access2 = await authorize({ action: "r2-delete-authorize", publicationId, revision: revision2, names }, bearer);
        } catch {
          return failure(origin, 403, "Suppression non autoris\xE9e");
        }
        if (access2.publicationId !== publicationId || access2.revision !== revision2 || access2.names?.length !== names.length || names.some((name2) => !access2.names.includes(name2))) return failure(origin, 403, "Manifeste invalide");
        if (!await reserve(env.CATALOG, modelRequestCost("delete")))
          return failure(origin, 429, "Quota commun atteint ou contr\xF4le expir\xE9");
        await env.MODELS.delete(names.map((name2) => `${publicationId}/${revision2}/${name2}`));
        const markers = await env.MODELS.list({ prefix: `${publicationId}/${revision2}/00 - `, limit: 1e3 });
        if (markers.objects.length) await env.MODELS.delete(markers.objects.map((object2) => object2.key));
        if (markers.truncated) return failure(origin, 503, "Nettoyage incomplet ; aucune r\xE9servation rembours\xE9e");
        return new Response(null, { status: 204, headers: headers(origin) });
      }
      if (request.method !== "POST" || path !== "/asset") return failure(origin, 404, "Introuvable");
      let input;
      try {
        input = await request.json();
      } catch {
        return failure(origin, 400, "Requ\xEAte invalide");
      }
      const { token, revision, name } = input || {};
      if (typeof token !== "string" || !/^[A-Za-z0-9_-]{32,160}$/.test(token) || !Number.isSafeInteger(revision) || revision < 1 || typeof name !== "string" || !/^[A-Za-z0-9._-]{1,120}$/.test(name)) {
        return failure(origin, 400, "Fichier invalide");
      }
      let access;
      try {
        access = await authorize({ action: "r2-asset", token, revision, name });
      } catch {
        return failure(origin, 403, "Lien indisponible");
      }
      if (!/^[0-9a-f-]{36}$/.test(access.publicationId || "") || access.revision !== revision) {
        return failure(origin, 403, "Publication non autoris\xE9e");
      }
      const asset = access.asset;
      if (asset?.name !== name || !Number.isSafeInteger(asset.bytes) || asset.bytes <= 0 || !/^[0-9a-f]{64}$/.test(asset.sha256 || "")) return failure(origin, 404, "Fichier inconnu");
      if (!await reserve(env.CATALOG, modelRequestCost("download")))
        return failure(origin, 429, "Quota commun atteint ou contr\xF4le expir\xE9");
      const object = await env.MODELS.get(`${access.publicationId}/${revision}/${name}`);
      if (!object || object.size !== asset.bytes) return failure(origin, 503, "Copie R2 absente ou incompl\xE8te");
      return new Response(object.body, {
        headers: { ...headers(origin), "Content-Type": "application/octet-stream", "Content-Length": String(object.size) }
      });
    } catch {
      return failure(origin, 503, "Contr\xF4le du quota ou stockage indisponible ; aucun acc\xE8s suppl\xE9mentaire");
    }
  }
};
export {
  worker_default as default
};
//# sourceMappingURL=worker.js.map
