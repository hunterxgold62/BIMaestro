// supabase/functions/upsert-profile/index.ts
import { serve } from "https://deno.land/std@0.177.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { verify } from "https://deno.land/x/djwt@v2.4/mod.ts";

/* ─────────────────────────── C O N F I G ─────────────────────────── */
function cors() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Headers": "authorization,content-type",
    "Access-Control-Allow-Methods": "POST,OPTIONS",
  };
}

function pickFrom(obj: any, keys: string[]) {
  if (!obj) return null;
  for (const k of keys) {
    const v = obj[k];
    if (typeof v === "string" && v.trim().length > 0) return v.trim();
  }
  return null;
}

function isValidEmail(email: string) {
  return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email);
}

function normalizePluginLanguage(body: any): "fr" | "en" | null {
  const value = pickFrom(body, [
    "plugin_language",
    "pluginLanguage",
    "language",
    "lang",
  ]);
  if (!value) return null;

  const language = value.toLowerCase().replace("_", "-").split("-")[0];
  return language === "fr" || language === "en" ? language : null;
}

/* ─────────────────────────── S E R V E ─────────────────────────── */
serve(async (req) => {
  try {
    if (req.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: cors() });
    }
    if (req.method !== "POST") {
      return new Response("Use POST", { status: 405, headers: cors() });
    }

    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SERVICE_KEY = Deno.env.get("SERVICE_ROLE_KEY");
    const JWT_SECRET = Deno.env.get("JWT_SECRET");
    if (!SUPABASE_URL || !SERVICE_KEY || !JWT_SECRET) {
      return new Response("Missing env", { status: 500, headers: cors() });
    }

    const auth = req.headers.get("Authorization") || "";
    if (!auth.startsWith("Bearer ")) {
      return new Response("Unauthorized", { status: 401, headers: cors() });
    }
    const token = auth.slice("Bearer ".length);

    const hmacKey = await crypto.subtle.importKey(
      "raw",
      new TextEncoder().encode(JWT_SECRET),
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["verify"],
    );

    let payloadJwt: any;
    try {
      payloadJwt = await verify(token, hmacKey);
    } catch {
      return new Response("Invalid token", { status: 401, headers: cors() });
    }

    const raw = await req.text();
    let body: any;
    try {
      body = JSON.parse(raw);
    } catch {
      return new Response("Bad JSON", { status: 400, headers: cors() });
    }

    const license_key =
      pickFrom(payloadJwt, ["license_key", "licenseKey", "lic", "license", "sub"]);
    if (!license_key) {
      return new Response("Missing license_key in token", {
        status: 400,
        headers: cors(),
      });
    }

    const install_id = pickFrom(body, ["install_id", "installId"]) || "";
    const email = pickFrom(body, ["email", "mail"]) || "";
    const first_name =
      pickFrom(body, ["first_name", "firstName", "prenom"]) || null;
    const last_name =
      pickFrom(body, ["last_name", "lastName", "nom"]) || null;
    const company = pickFrom(body, ["company", "entreprise"]) || null;
    const plugin_language = normalizePluginLanguage(body);
    const machine_id_hash =
      pickFrom(payloadJwt, ["machine_id", "machineId", "mid", "device", "device_id"]) ||
      pickFrom(body, ["machine_id_hash", "machineIdHash"]) ||
      null;

    if (!install_id) {
      return new Response("Missing install_id", {
        status: 400,
        headers: cors(),
      });
    }
    if (!email || !isValidEmail(email)) {
      return new Response("Invalid email", {
        status: 400,
        headers: cors(),
      });
    }

    const supabase = createClient(SUPABASE_URL, SERVICE_KEY, {
      auth: { persistSession: false },
    });

    const nowIso = new Date().toISOString();
    const row = {
      license_key,
      install_id,
      machine_id_hash,
      email,
      first_name,
      last_name,
      company,
      consent: true,
      consent_at: nowIso,
      last_seen_at: nowIso,
      updated_at: nowIso,
      ...(plugin_language ? { plugin_language } : {}),
    };

    const { error } = await supabase
      .from("license_profiles")
      .upsert(row, { onConflict: "license_key,install_id" });

    if (error) {
      return new Response("Upsert error: " + error.message, {
        status: 500,
        headers: cors(),
      });
    }

    return new Response(null, { status: 204, headers: cors() });
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    return new Response("Unhandled error: " + msg, {
      status: 500,
      headers: cors(),
    });
  }
});
