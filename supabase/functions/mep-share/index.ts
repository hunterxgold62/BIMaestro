import { serve } from "https://deno.land/std@0.192.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.45.4";
import { create, verify } from "https://deno.land/x/djwt@v2.4/mod.ts";

const supabaseUrl = Deno.env.get("SUPABASE_URL") ?? "";
const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ??
  Deno.env.get("SERVICE_ROLE_KEY") ?? "";
const jwtSecret = Deno.env.get("JWT_SECRET") ?? "";
if (!supabaseUrl || !serviceRoleKey || !jwtSecret) {
  throw new Error("Missing Supabase configuration");
}

const admin = createClient(supabaseUrl, serviceRoleKey, {
  auth: { persistSession: false },
});
const encoder = new TextEncoder();
const hmacKey = await crypto.subtle.importKey(
  "raw", encoder.encode(jwtSecret), { name: "HMAC", hash: "SHA-256" },
  false, ["sign", "verify"],
);
const allowedOrigins = new Set([
  "https://viewer.bimaestro.fr",
  "https://bimaestro-mep-viewer.hunterxgold.chatgpt.site",
  "http://localhost:3000",
]);
const maxPackageBytes = 25 * 1024 * 1024;
const maxViewerStorageBytes = 700 * 1024 * 1024;

class HttpError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

function cors(req: Request): HeadersInit {
  const origin = req.headers.get("origin") ?? "";
  return {
    "Access-Control-Allow-Origin": allowedOrigins.has(origin)
      ? origin
      : "https://viewer.bimaestro.fr",
    "Access-Control-Allow-Headers": "authorization, apikey, content-type",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Vary": "Origin",
    "Referrer-Policy": "no-referrer",
  };
}

function response(req: Request, value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { ...cors(req), "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}

async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function randomToken(bytes = 32): string {
  const value = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...value))
    .replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function cleanName(value: unknown, fallback = "Maquette MEP"): string {
  if (typeof value !== "string") return fallback;
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return cleaned ? cleaned.slice(0, 120) : fallback;
}

async function licenseIdentity(req: Request): Promise<{ licenseHash: string }> {
  const authorization = req.headers.get("authorization") ?? "";
  if (!authorization.startsWith("Bearer ")) throw new HttpError(401, "Licence requise");
  let payload: any;
  try { payload = await verify(authorization.slice(7).trim(), hmacKey); }
  catch { throw new HttpError(401, "Jeton de licence invalide"); }
  const licenseKey = typeof payload?.license_key === "string" ? payload.license_key.trim() : "";
  const machineId = typeof payload?.machine_id === "string" ? payload.machine_id.trim() : "";
  if (!licenseKey || !machineId) throw new HttpError(401, "Licence requise");
  return { licenseHash: await sha256(licenseKey) };
}

async function shareAccess(token: unknown) {
  if (typeof token !== "string" || token.length < 32 || token.length > 160) {
    throw new HttpError(401, "Lien de partage invalide");
  }
  const tokenHash = await sha256(token);
  const { data, error } = await admin.from("mep_publication_tokens")
    .select("access_role, revoked_at, mep_publications!inner(id, name, slug, active_revision, expires_at, revoked_at)")
    .eq("token_hash", tokenHash).maybeSingle();
  if (error || !data) throw new HttpError(404, "Partage introuvable");
  const publication = Array.isArray(data.mep_publications)
    ? data.mep_publications[0]
    : data.mep_publications;
  if (!publication || data.revoked_at || publication.revoked_at ||
      new Date(publication.expires_at).getTime() <= Date.now()) {
    throw new HttpError(410, "Ce partage a expiré ou a été révoqué");
  }
  return { tokenHash, role: data.access_role as "viewer" | "editor", publication };
}

async function publicationOwned(publicationId: string, ownerHash: string) {
  const { data, error } = await admin.from("mep_publications").select("*")
    .eq("id", publicationId).eq("owner_license_hash", ownerHash).maybeSingle();
  if (error || !data) throw new HttpError(404, "Publication introuvable");
  return data;
}

async function cleanupExpiredPublications() {
  const now = new Date().toISOString();
  const { data: expired } = await admin.from("mep_publications").select("id")
    .or(`expires_at.lt.${now},revoked_at.not.is.null`).limit(50);
  const ids = (expired ?? []).map((item) => item.id);
  if (!ids.length) return;
  const { data: revisions } = await admin.from("mep_publication_revisions")
    .select("storage_path").in("publication_id", ids);
  const paths = (revisions ?? []).map((item) => item.storage_path);
  if (paths.length) await admin.storage.from("mep-publications").remove(paths);
  await admin.from("mep_publications").delete().in("id", ids);
}

async function startPublication(req: Request, body: any) {
  const identity = await licenseIdentity(req);
  const packageBytes = Number(body.packageBytes);
  const packageSha256 = String(body.packageSha256 ?? "").toLowerCase();
  if (!Number.isSafeInteger(packageBytes) || packageBytes <= 0 || packageBytes > maxPackageBytes) {
    throw new HttpError(413, "Taille de paquet invalide");
  }
  if (!/^[0-9a-f]{64}$/.test(packageSha256)) throw new HttpError(400, "Empreinte SHA-256 invalide");
  await cleanupExpiredPublications();
  const { data: storedBytes, error: storageUsageError } = await admin.rpc("mep_viewer_storage_bytes");
  if (storageUsageError) throw new HttpError(500, "Quota de stockage indisponible");
  if (Number(storedBytes) + packageBytes > maxViewerStorageBytes) {
    throw new HttpError(507, "Quota gratuit du viewer atteint. Révoquez un ancien partage avant de publier.");
  }
  const modelKeyHash = await sha256(String(body.modelKey ?? "model"));
  let publication: any;
  let viewerToken: string | null = null;
  let editorToken: string | null = null;

  if (typeof body.publicationId === "string" && body.publicationId) {
    publication = await publicationOwned(body.publicationId, identity.licenseHash);
  } else {
    viewerToken = randomToken();
    editorToken = randomToken();
    const slug = randomToken(9).toLowerCase();
    const expiresAt = new Date(Date.now() + 30 * 86400000).toISOString();
    const { data, error } = await admin.from("mep_publications").insert({
      owner_license_hash: identity.licenseHash,
      model_key_hash: modelKeyHash,
      slug,
      name: cleanName(body.name),
      expires_at: expiresAt,
    }).select("*").single();
    if (error) throw new HttpError(500, error.message);
    publication = data;
    const { error: tokenError } = await admin.from("mep_publication_tokens").insert([
      { publication_id: publication.id, token_hash: await sha256(viewerToken), access_role: "viewer" },
      { publication_id: publication.id, token_hash: await sha256(editorToken), access_role: "editor" },
    ]);
    if (tokenError) throw new HttpError(500, tokenError.message);
    await admin.from("mep_scenarios").insert({ publication_id: publication.id });
  }

  const revision = Number(publication.active_revision) + 1;
  const storagePath = `${publication.id}/${revision}.bimaestro-mep.zip`;
  const valveIds = Array.isArray(body.valveIds)
    ? [...new Set(body.valveIds.filter((item: unknown) => typeof item === "string").map((item: string) => item.slice(0, 300)))].slice(0, 50000)
    : [];
  // A failed upload may leave an inactive draft. Retrying the same publication
  // safely replaces that draft without touching the active immutable revision.
  await admin.storage.from("mep-publications").remove([storagePath]);
  await admin.from("mep_publication_revisions").delete()
    .eq("publication_id", publication.id).eq("revision", revision);
  const { error: revisionError } = await admin.from("mep_publication_revisions").insert({
    publication_id: publication.id,
    revision,
    storage_path: storagePath,
    package_sha256: packageSha256,
    package_bytes: packageBytes,
    manifest: typeof body.manifest === "object" && body.manifest ? body.manifest : {},
    valve_ids: valveIds,
  });
  if (revisionError) throw new HttpError(409, revisionError.message);
  const { data: upload, error: uploadError } = await admin.storage
    .from("mep-publications").createSignedUploadUrl(storagePath);
  if (uploadError || !upload) throw new HttpError(500, uploadError?.message ?? "Upload impossible");
  return {
    publicationId: publication.id,
    revision,
    expiresAt: publication.expires_at,
    uploadPath: upload.path,
    uploadToken: upload.token,
    uploadUrl: upload.signedUrl,
    viewerToken,
    editorToken,
  };
}

async function completePublication(req: Request, body: any) {
  const identity = await licenseIdentity(req);
  const publication = await publicationOwned(String(body.publicationId ?? ""), identity.licenseHash);
  const revision = Number(body.revision);
  const { data: stored } = await admin.storage.from("mep-publications")
    .list(publication.id, { search: `${revision}.bimaestro-mep.zip`, limit: 2 });
  if (!stored?.some((item) => item.name === `${revision}.bimaestro-mep.zip`)) {
    throw new HttpError(409, "Le paquet n'a pas encore été transféré");
  }
  const { data: activated, error } = await admin.from("mep_publications").update({
    active_revision: revision,
    updated_at: new Date().toISOString(),
  }).eq("id", publication.id).eq("active_revision", revision - 1)
    .select("id").maybeSingle();
  if (error || !activated) throw new HttpError(409, "Une autre publication a déjà été activée");

  const { data: activeRevision } = await admin.from("mep_publication_revisions")
    .select("valve_ids").eq("publication_id", publication.id).eq("revision", revision).single();
  const compatible = new Set<string>(activeRevision?.valve_ids ?? []);
  const { data: scenario } = await admin.from("mep_scenarios").select("state")
    .eq("publication_id", publication.id).single();
  const previousValves = ((scenario?.state as any)?.valves ?? {}) as Record<string, boolean>;
  const valves = Object.fromEntries(Object.entries(previousValves).filter(([id]) => compatible.has(id)));
  const removedValveIds = Object.keys(previousValves).filter((id) => !compatible.has(id));
  await admin.from("mep_scenarios").update({
    state: { ...(scenario?.state ?? {}), valves },
    updated_at: new Date().toISOString(),
  }).eq("publication_id", publication.id);
  return { publicationId: publication.id, revision, active: true, removedValveIds };
}

async function resolveShare(body: any) {
  const access = await shareAccess(body.token);
  if (access.publication.active_revision <= 0) throw new HttpError(409, "Publication incomplète");
  const { data: revision, error } = await admin.from("mep_publication_revisions")
    .select("storage_path, manifest, package_bytes, created_at")
    .eq("publication_id", access.publication.id)
    .eq("revision", access.publication.active_revision).single();
  if (error) throw new HttpError(500, error.message);
  const { data: signed, error: signedError } = await admin.storage
    .from("mep-publications").createSignedUrl(revision.storage_path, 900);
  if (signedError || !signed) throw new HttpError(500, signedError?.message ?? "Fichier indisponible");
  const { error: budgetError } = await admin.rpc("reserve_mep_viewer_usage", {
    p_kind: "download", p_amount: Number(revision.package_bytes),
  });
  if (budgetError) {
    if (budgetError.message.includes("VIEWER_EGRESS_LIMIT")) {
      throw new HttpError(429, "Budget mensuel gratuit du viewer atteint");
    }
    throw new HttpError(500, "Contrôle du quota indisponible");
  }
  const { data: scenario } = await admin.from("mep_scenarios").select("revision, state, updated_by, updated_at")
    .eq("publication_id", access.publication.id).single();
  const { data: events } = await admin.from("mep_scenario_events")
    .select("scenario_revision, participant_name, target_id, previous_value, next_value, created_at")
    .eq("publication_id", access.publication.id).order("scenario_revision", { ascending: false }).limit(20);
  const now = Math.floor(Date.now() / 1000);
  const realtimeToken = await create({ alg: "HS256", typ: "JWT" }, {
    iss: "supabase", aud: "authenticated", role: "authenticated",
    sub: `share:${access.tokenHash.slice(0, 32)}`,
    publication_id: access.publication.id,
    share_role: access.role,
    iat: now, exp: now + 900,
  }, hmacKey);
  return {
    publication: { id: access.publication.id, name: access.publication.name, slug: access.publication.slug, revision: access.publication.active_revision, expiresAt: access.publication.expires_at },
    role: access.role,
    packageUrl: signed.signedUrl,
    manifest: revision.manifest,
    scenario,
    events: events ?? [],
    realtimeToken,
  };
}

async function mutateScenario(body: any) {
  const access = await shareAccess(body.token);
  if (access.role !== "editor") throw new HttpError(403, "Lien en lecture seule");
  const targetId = typeof body.targetId === "string" ? body.targetId.slice(0, 300) : "";
  const expectedRevision = Number(body.expectedRevision);
  const nextValue = body.closed;
  const operationId = String(body.operationId ?? "");
  if (!targetId || typeof nextValue !== "boolean" || !Number.isSafeInteger(expectedRevision) ||
      !/^[0-9a-f-]{36}$/i.test(operationId)) throw new HttpError(400, "Commande invalide");
  const participantName = cleanName(body.participantName, "Invité").slice(0, 40);
  const { error: realtimeBudgetError } = await admin.rpc("reserve_mep_viewer_usage", {
    p_kind: "scenario", p_amount: 1,
  });
  if (realtimeBudgetError) {
    if (realtimeBudgetError.message.includes("VIEWER_REALTIME_LIMIT")) {
      throw new HttpError(429, "Budget collaboratif mensuel atteint");
    }
    throw new HttpError(500, "Contrôle du quota indisponible");
  }
  const { data: result, error } = await admin.rpc("apply_mep_scenario_command", {
    p_publication_id: access.publication.id,
    p_operation_id: operationId,
    p_target_id: targetId,
    p_expected_revision: expectedRevision,
    p_next_value: nextValue,
    p_participant_name: participantName,
  });
  if (error) {
    if (error.message.includes("VALVE_NOT_FOUND")) throw new HttpError(400, "Vanne absente de la publication");
    if (error.message.includes("REVISION_CONFLICT")) throw new HttpError(409, "Le scénario a été modifié par un autre participant");
    throw new HttpError(500, "Commande collaborative impossible");
  }
  const updated = result?.scenario;
  if (!updated) throw new HttpError(500, "Scénario indisponible");
  try {
    const channel = admin.channel(`mep:${access.publication.id}`, { config: { private: true } });
    await new Promise<void>((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Realtime timeout")), 4000);
      channel.subscribe((status) => {
        if (status === "SUBSCRIBED") { clearTimeout(timeout); resolve(); }
        if (status === "CHANNEL_ERROR" || status === "TIMED_OUT") {
          clearTimeout(timeout); reject(new Error(status));
        }
      });
    });
    await channel.send({ type: "broadcast", event: "scenario", payload: updated });
    await admin.removeChannel(channel);
  } catch { /* Le client se resynchronise aussi après chaque commande. */ }
  return { scenario: updated, replayed: result?.replayed === true };
}

async function managePublication(req: Request, body: any) {
  const identity = await licenseIdentity(req);
  const publication = await publicationOwned(String(body.publicationId ?? ""), identity.licenseHash);
  if (body.command === "revoke") {
    const now = new Date().toISOString();
    await admin.from("mep_publications").update({ revoked_at: now, updated_at: now }).eq("id", publication.id);
    await admin.from("mep_publication_tokens").update({ revoked_at: now }).eq("publication_id", publication.id);
    const { data: revisions } = await admin.from("mep_publication_revisions")
      .select("storage_path").eq("publication_id", publication.id);
    const paths = (revisions ?? []).map((item) => item.storage_path);
    if (paths.length) await admin.storage.from("mep-publications").remove(paths);
    await admin.from("mep_publications").delete().eq("id", publication.id);
    return { revoked: true };
  }
  if (body.command === "extend") {
    const days = Math.max(1, Math.min(365, Number(body.days) || 30));
    const expiresAt = new Date(Date.now() + days * 86400000).toISOString();
    await admin.from("mep_publications").update({ expires_at: expiresAt, updated_at: new Date().toISOString() }).eq("id", publication.id);
    return { expiresAt };
  }
  throw new HttpError(400, "Commande de gestion inconnue");
}

serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: cors(req) });
  if (req.method !== "POST") return response(req, { error: "Method not allowed" }, 405);
  try {
    const body = await req.json();
    let result: unknown;
    switch (body.action) {
      case "publish-start": result = await startPublication(req, body); break;
      case "publish-complete": result = await completePublication(req, body); break;
      case "resolve": result = await resolveShare(body); break;
      case "scenario": result = await mutateScenario(body); break;
      case "manage": result = await managePublication(req, body); break;
      default: throw new HttpError(400, "Action inconnue");
    }
    return response(req, result);
  } catch (error) {
    const status = error instanceof HttpError ? error.status : 500;
    console.error("mep-share", error);
    return response(req, { error: error instanceof Error ? error.message : "Erreur interne" }, status);
  }
});
