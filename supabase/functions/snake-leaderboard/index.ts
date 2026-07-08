import { serve } from "https://deno.land/std@0.192.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.45.4";
import { verify } from "https://deno.land/x/djwt@v2.4/mod.ts";

const supabaseUrl = Deno.env.get("SUPABASE_URL");
const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? Deno.env.get("SERVICE_ROLE_KEY");
const jwtSecret = Deno.env.get("JWT_SECRET");

if (!supabaseUrl || !serviceRoleKey || !jwtSecret) {
  throw new Error("Missing SUPABASE_URL, service role key, or JWT_SECRET");
}

const supabase = createClient(supabaseUrl, serviceRoleKey, {
  auth: { persistSession: false },
});

const hmacKey = await crypto.subtle.importKey(
  "raw",
  new TextEncoder().encode(jwtSecret),
  { name: "HMAC", hash: "SHA-256" },
  false,
  ["verify"],
);

const allowedModes = new Set(["classic", "arcade", "hardcore", "flappy_bird"]);
const gameName = "snake";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function getScoreCap(mode: string): number {
  const defaults: Record<string, number> = {
    classic: 5000,
    arcade: 5000,
    hardcore: 5000,
    flappy_bird: 500,
  };

  const envNames: Record<string, string> = {
    classic: "SNAKE_MAX_SCORE_CLASSIC",
    arcade: "SNAKE_MAX_SCORE_ARCADE",
    hardcore: "SNAKE_MAX_SCORE_HARDCORE",
    flappy_bird: "SNAKE_MAX_SCORE_FLAPPY_BIRD",
  };

  const configured = Number(Deno.env.get(envNames[mode]));
  return Number.isFinite(configured) && configured > 0 ? Math.trunc(configured) : defaults[mode];
}

function cleanPlayerName(value: unknown): string {
  if (typeof value !== "string") return "Joueur";
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return cleaned.length > 0 ? cleaned.slice(0, 40) : "Joueur";
}

function cleanClientInstallId(value: unknown): string {
  if (typeof value !== "string") return "";
  return value.replace(/[^a-zA-Z0-9_.:-]/g, "").slice(0, 96);
}

function bearerToken(req: Request): string | null {
  const authHeader = req.headers.get("Authorization") ?? "";
  if (!authHeader.startsWith("Bearer ")) return null;
  return authHeader.slice("Bearer ".length).trim();
}

async function requireLicenseToken(req: Request): Promise<{ licenseKey: string; machineId: string }> {
  const token = bearerToken(req);
  if (!token) {
    throw new HttpError(401, "Missing Authorization");
  }

  let payload: any;
  try {
    payload = await verify(token, hmacKey);
  } catch {
    throw new HttpError(401, "Invalid token");
  }

  const licenseKey = typeof payload?.license_key === "string" ? payload.license_key.trim() : "";
  const machineId = typeof payload?.machine_id === "string" ? payload.machine_id.trim() : "";

  if (!licenseKey || !machineId) {
    throw new HttpError(401, "License token required");
  }

  return { licenseKey, machineId };
}

async function stableLeaderboardInstallId(licenseKey: string, machineId: string, clientInstallId: string): Promise<string> {
  const source = machineId ? `${licenseKey}|${machineId}` : `${licenseKey}|${clientInstallId}`;
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(source));
  const hex = Array.from(new Uint8Array(bytes)).map((b) => b.toString(16).padStart(2, "0")).join("");
  return `jwt:${hex.slice(0, 48)}`;
}

class HttpError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

serve(async (req) => {
  try {
    const identity = await requireLicenseToken(req);

    if (req.method === "GET") {
      const url = new URL(req.url);
      const limit = Math.max(1, Math.min(25, Number(url.searchParams.get("limit")) || 10));

      const response: Record<string, unknown> = {};

      for (const mode of allowedModes) {
        const { data, error } = await supabase
          .from("game_leaderboards")
          .select("player_name, score")
          .eq("game", gameName)
          .eq("mode", mode)
          .order("score", { ascending: false })
          .limit(limit);

        if (error) {
          return jsonResponse({ error: error.message }, 500);
        }

        response[mode] = data ?? [];
      }

      return jsonResponse(response);
    }

    if (req.method === "POST") {
      let body: any;
      try {
        body = await req.json();
      } catch {
        return jsonResponse({ error: "Bad JSON" }, 400);
      }

      const mode = typeof body.mode === "string" ? body.mode : "";
      const rawScore = Number(body.score ?? 0);
      const playerName = cleanPlayerName(body.player_name);
      const clientInstallId = cleanClientInstallId(body.install_id);

      if (!allowedModes.has(mode)) {
        return jsonResponse({ error: "Invalid mode" }, 400);
      }

      if (!Number.isFinite(rawScore) || rawScore < 0) {
        return jsonResponse({ error: "Invalid score" }, 400);
      }

      const normalizedScore = Math.trunc(rawScore);
      const scoreCap = getScoreCap(mode);

      if (normalizedScore > scoreCap) {
        return jsonResponse({ error: "Score rejected", max_score: scoreCap }, 400);
      }

      const installId = await stableLeaderboardInstallId(identity.licenseKey, identity.machineId, clientInstallId);

      const { data: existing, error: existingError } = await supabase
        .from("game_leaderboards")
        .select("score")
        .eq("game", gameName)
        .eq("mode", mode)
        .eq("install_id", installId)
        .maybeSingle();

      if (existingError && existingError.code !== "PGRST116") {
        return jsonResponse({ error: existingError.message }, 500);
      }

      if (!existing || normalizedScore > (existing.score ?? 0)) {
        const { error: upsertError } = await supabase
          .from("game_leaderboards")
          .upsert(
            {
              game: gameName,
              mode,
              install_id: installId,
              license_key: identity.licenseKey,
              machine_id_hash: identity.machineId,
              player_name: playerName,
              score: normalizedScore,
              updated_at: new Date().toISOString(),
            },
            { onConflict: "game,mode,install_id" },
          );

        if (upsertError) {
          return jsonResponse({ error: upsertError.message }, 500);
        }

        return jsonResponse({ updated: true });
      }

      return jsonResponse({ updated: false });
    }

    return jsonResponse({ error: "Method not allowed" }, 405);
  } catch (error) {
    if (error instanceof HttpError) {
      return jsonResponse({ error: error.message }, error.status);
    }

    return jsonResponse({ error: error instanceof Error ? error.message : String(error) }, 500);
  }
});

