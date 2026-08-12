// supabase/functions/validate/index.ts
import { serve } from "https://deno.land/std@0.177.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";
import { create } from "https://deno.land/x/djwt@v2.4/mod.ts";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
const PUBLIC_API_KEY = Deno.env.get("SUPABASE_ANON_KEY");
const SERVICE_KEY =
  Deno.env.get("SERVICE_ROLE_KEY") ??
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ??
  Deno.env.get("SUPABASE_SERVICE_KEY");
const JWT_SECRET = Deno.env.get("JWT_SECRET");
const MAX_DEVICES = parseInt(Deno.env.get("MAX_DEVICES") ?? "5", 10);

console.log("validate() env flags", {
  hasUrl: !!SUPABASE_URL,
  hasPublicApiKey: !!PUBLIC_API_KEY,
  hasServiceKey: !!SERVICE_KEY,
  hasJwtSecret: !!JWT_SECRET,
  maxDevices: MAX_DEVICES,
});

const supabase = SUPABASE_URL && SERVICE_KEY
  ? createClient(SUPABASE_URL, SERVICE_KEY, {
      auth: { persistSession: false },
    })
  : null;

const HMAC_KEY = JWT_SECRET
  ? await crypto.subtle.importKey(
      "raw",
      new TextEncoder().encode(JWT_SECRET),
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["sign"],
    )
  : null;

serve(async (req) => {
  try {
    if (
      !SUPABASE_URL ||
      !PUBLIC_API_KEY ||
      !SERVICE_KEY ||
      !JWT_SECRET ||
      !supabase ||
      !HMAC_KEY
    ) {
      console.error("Server misconfigured: missing env vars", {
        hasUrl: !!SUPABASE_URL,
        hasPublicApiKey: !!PUBLIC_API_KEY,
        hasServiceKey: !!SERVICE_KEY,
        hasJwtSecret: !!JWT_SECRET,
        hasSupabase: !!supabase,
        hasHmacKey: !!HMAC_KEY,
      });
      return json({ message: "Server misconfigured" }, 500);
    }

    if (req.method !== "POST") {
      return new Response("Use POST", { status: 405 });
    }

    // verify_jwt est désactivé pour cette fonction d'amorçage. On vérifie
    // néanmoins explicitement la clé publique envoyée dans le bon header.
    // Aucun secret service_role n'est exposé au client.
    const requestApiKey = req.headers.get("apikey") ?? "";
    if (requestApiKey !== PUBLIC_API_KEY) {
      return json({ message: "Unauthorized" }, 401);
    }

    let body: unknown;
    try {
      body = await req.json();
    } catch (error) {
      console.error("Invalid JSON body:", error);
      return json({ message: "invalid JSON body" }, 400);
    }

    const payload = body as {
      license_key?: unknown;
      machine_id?: unknown;
    };
    const licenseKey = typeof payload?.license_key === "string"
      ? payload.license_key.trim()
      : "";
    const machineId = typeof payload?.machine_id === "string"
      ? payload.machine_id.trim()
      : "";

    console.log("validate payload", {
      hasLicenseKey: licenseKey.length > 0,
      licensePrefix: licenseKey ? licenseKey.slice(0, 6) : null,
      machineLen: machineId ? machineId.length : null,
    });

    if (!licenseKey || !machineId) {
      return json({ message: "missing license_key or machine_id" }, 400);
    }

    const { data: existingLicense, error: selectError } = await supabase
      .from("licenses")
      .select("license_key, machine_ids, expires_at, issued_at, status")
      .eq("license_key", licenseKey)
      .maybeSingle();

    if (selectError) {
      console.error("select licenses error:", selectError);
      return json({ message: "read error" }, 500);
    }

    let license = existingLicense;

    // Conserve le fonctionnement actuel : création paresseuse d'un essai
    // de 90 jours pour une nouvelle clé de licence.
    if (!license) {
      const now = new Date();
      const expiresAt = new Date(now.getTime() + 90 * 24 * 3600 * 1000);
      const insertPayload = {
        license_key: licenseKey,
        machine_ids: [machineId],
        issued_at: now.toISOString(),
        expires_at: expiresAt.toISOString(),
        status: "active",
      };

      const { error: insertError } = await supabase
        .from("licenses")
        .insert(insertPayload);

      if (insertError) {
        console.error("insert license error:", insertError);
        return json({ message: "cannot create license" }, 500);
      }

      const { data: insertedLicense, error: reloadError } = await supabase
        .from("licenses")
        .select("license_key, machine_ids, expires_at, issued_at, status")
        .eq("license_key", licenseKey)
        .maybeSingle();

      if (reloadError || !insertedLicense) {
        console.error("re-select license error:", reloadError);
        return json({ message: "read error" }, 500);
      }

      license = insertedLicense;
    }

    let devices: string[] = Array.isArray(license.machine_ids)
      ? license.machine_ids
      : [];
    devices = devices
      .filter((value): value is string =>
        typeof value === "string" && value.trim().length > 0
      )
      .map((value) => value.trim());

    const expiresAt = new Date(license.expires_at);
    if (
      license.status !== "active" ||
      Number.isNaN(expiresAt.getTime()) ||
      expiresAt < new Date()
    ) {
      return json({ message: "Licence expirée" }, 403);
    }

    if (devices.includes(machineId)) {
      const token = await issueJwt(licenseKey, machineId, license.expires_at);
      return json({
        token,
        device_count: devices.length,
        max_devices: MAX_DEVICES,
      });
    }

    if (devices.length < MAX_DEVICES) {
      const newDevices = Array.from(new Set([...devices, machineId]));
      const { error: updateError } = await supabase
        .from("licenses")
        .update({ machine_ids: newDevices })
        .eq("license_key", licenseKey);

      if (updateError) {
        console.error("update license append device error:", updateError);
        return json({ message: "cannot append device" }, 500);
      }

      const token = await issueJwt(licenseKey, machineId, license.expires_at);
      return json({
        token,
        device_count: newDevices.length,
        max_devices: MAX_DEVICES,
      });
    }

    return json(
      { message: `Nombre maximum d'appareils atteint (${MAX_DEVICES}).` },
      403,
    );
  } catch (error) {
    console.error("Error in validate():", error);
    const message = error instanceof Error ? error.message : "internal error";
    return json({ message }, 500);
  }
});

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function issueJwt(
  licenseKey: string,
  machineId: string,
  expiresAt: string,
) {
  const exp = Math.floor(new Date(expiresAt).getTime() / 1000);
  return await create(
    { alg: "HS256", typ: "JWT" },
    { license_key: licenseKey, machine_id: machineId, exp },
    HMAC_KEY!,
  );
}
