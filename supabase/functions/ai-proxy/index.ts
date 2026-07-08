import { serve } from "https://deno.land/std@0.177.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.45.4";
import { verify } from "https://deno.land/x/djwt@v2.4/mod.ts";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL") ?? "https://xqovxfgghbqxwsadzhzl.supabase.co";
const SERVICE_KEY = Deno.env.get("SERVICE_ROLE_KEY") ?? Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
const JWT_SECRET = Deno.env.get("JWT_SECRET");
const OPENAI_KEY = Deno.env.get("OPENAI_KEY");
const DEEPSEEK_KEY = Deno.env.get("DEEPSEEK_KEY");

if (!SERVICE_KEY || !JWT_SECRET) {
  throw new Error("Missing env SERVICE_ROLE_KEY/JWT_SECRET");
}

const supabase = createClient(SUPABASE_URL, SERVICE_KEY, {
  auth: { persistSession: false },
});

const HMAC_KEY = await crypto.subtle.importKey(
  "raw",
  new TextEncoder().encode(JWT_SECRET),
  { name: "HMAC", hash: "SHA-256" },
  false,
  ["verify"],
);

type JsonObject = Record<string, unknown>;
type OpenAiRoute = "chat_completions" | "responses" | "images_generations" | "images_edits";

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function jsonResponse(data: unknown, status = 200, extraHeaders: Record<string, string> = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      "Content-Type": "application/json",
      ...extraHeaders,
    },
  });
}

function textResponse(text: string, status = 200) {
  return new Response(text, { status });
}

function getString(value: unknown): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value : null;
}

function isPlainObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function cloneWithoutInternalFields(parameters: JsonObject): JsonObject {
  const copy: JsonObject = { ...parameters };
  delete copy.endpoint;
  delete copy.openai_endpoint;
  delete copy.provider;
  delete copy.feature;
  return copy;
}

function safeNumber(value: unknown): number {
  const n = Number(value ?? 0);
  return Number.isFinite(n) ? n : 0;
}

function extractTotalTokens(json: any): number {
  const usage = json?.usage;
  if (!usage) return 0;

  const direct = safeNumber(usage.total_tokens ?? usage.totalTokens);
  if (direct > 0) return direct;

  const inputOutput = safeNumber(usage.input_tokens) + safeNumber(usage.output_tokens);
  if (inputOutput > 0) return inputOutput;

  const promptCompletion = safeNumber(usage.prompt_tokens) + safeNumber(usage.completion_tokens);
  return promptCompletion > 0 ? promptCompletion : 0;
}

function estimateImageOutputTokens(parameters: JsonObject): number {
  const quality = getString(parameters.quality) ?? "low";
  const size = getString(parameters.size) ?? "1024x1024";
  const normalized = `${quality}|${size}`;

  const table: Record<string, number> = {
    "low|1024x1024": 272,
    "medium|1024x1024": 1056,
    "high|1024x1024": 4160,
    "low|1024x1536": 408,
    "medium|1024x1536": 1584,
    "high|1024x1536": 6240,
    "low|1536x1024": 400,
    "medium|1536x1024": 1568,
    "high|1536x1024": 6208,
  };

  return table[normalized] ?? 272;
}

function dataUrlToBlob(dataUrl: string): { blob: Blob; filename: string } {
  const match = dataUrl.match(/^data:([^;]+);base64,(.*)$/);
  if (!match) {
    throw new Error("Image invalide : le proxy attend une data URL base64 du type data:image/png;base64,...");
  }

  const mimeType = match[1] || "image/png";
  const base64 = match[2];
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);

  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }

  const extension = mimeType.includes("jpeg") || mimeType.includes("jpg")
    ? "jpg"
    : mimeType.includes("webp")
      ? "webp"
      : "png";

  return {
    blob: new Blob([bytes], { type: mimeType }),
    filename: `input.${extension}`,
  };
}

function collectImageDataUrls(parameters: JsonObject): string[] {
  const urls: string[] = [];

  const image = getString(parameters.image);
  if (image) urls.push(image);

  const inputImage = getString(parameters.input_image);
  if (inputImage) urls.push(inputImage);

  const imageUrl = getString(parameters.image_url);
  if (imageUrl) urls.push(imageUrl);

  if (Array.isArray(parameters.images)) {
    for (const item of parameters.images) {
      if (typeof item === "string" && item.trim().length > 0) {
        urls.push(item);
      } else if (isPlainObject(item)) {
        const nested =
          getString(item.image_url) ??
          getString(item.url) ??
          getString(item.data_url) ??
          getString(item.image);
        if (nested) urls.push(nested);
      }
    }
  }

  return urls;
}

function hasImageReference(parameters: JsonObject): boolean {
  return collectImageDataUrls(parameters).length > 0;
}

function hasResponsesShape(parameters: JsonObject): boolean {
  return Array.isArray(parameters.input) || Array.isArray(parameters.tools);
}

function getRequestedEndpoint(parameters: JsonObject): string | null {
  return getString(parameters.endpoint) ?? getString(parameters.openai_endpoint);
}

function detectOpenAiRoute(parameters: JsonObject): OpenAiRoute {
  const explicitEndpoint = getRequestedEndpoint(parameters);

  if (explicitEndpoint) {
    const ep = explicitEndpoint.toLowerCase();
    if (ep.includes("responses")) return "responses";
    if (ep.includes("images/edits") || ep.includes("images.edits")) return "images_edits";
    if (ep.includes("images/generations") || ep.includes("images.generations")) return "images_generations";
    if (ep.includes("chat/completions") || ep.includes("chat.completions")) return "chat_completions";
  }

  if (hasResponsesShape(parameters)) return "responses";
  if (hasImageReference(parameters)) return "images_edits";

  const model = getString(parameters.model);
  if (model && model.startsWith("gpt-image-")) return "images_generations";

  return "chat_completions";
}

function usageMultiplier(provider: string, route: OpenAiRoute | null): number {
  if (provider === "deepseek") return 2;
  if (provider === "openai" && (route === "images_edits" || route === "images_generations")) return 4;
  return 1;
}

async function callOpenAiJsonWithRetry(endpoint: string, payload: unknown, maxRetries = 2): Promise<Response> {
  if (!OPENAI_KEY) throw new Error("Missing env OPENAI_KEY");

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    const resp = await fetch(`https://api.openai.com${endpoint}`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${OPENAI_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(payload),
    });

    if (![429, 500, 502, 503, 504].includes(resp.status) || attempt === maxRetries) {
      return resp;
    }

    const retryAfter = resp.headers.get("retry-after");
    const waitSec = retryAfter ? parseInt(retryAfter, 10) : Math.pow(2, attempt);
    await delay(Math.max(1, waitSec) * 1000);
  }

  throw new Error("OpenAI retry loop failed unexpectedly");
}

async function callOpenAiFormWithRetry(endpoint: string, buildForm: () => FormData, maxRetries = 2): Promise<Response> {
  if (!OPENAI_KEY) throw new Error("Missing env OPENAI_KEY");

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    const resp = await fetch(`https://api.openai.com${endpoint}`, {
      method: "POST",
      headers: { Authorization: `Bearer ${OPENAI_KEY}` },
      body: buildForm(),
    });

    if (![429, 500, 502, 503, 504].includes(resp.status) || attempt === maxRetries) {
      return resp;
    }

    const retryAfter = resp.headers.get("retry-after");
    const waitSec = retryAfter ? parseInt(retryAfter, 10) : Math.pow(2, attempt);
    await delay(Math.max(1, waitSec) * 1000);
  }

  throw new Error("OpenAI form retry loop failed unexpectedly");
}

async function parseProviderResponse(resp: Response): Promise<any> {
  const rawResp = await resp.text();

  return {
    ok: resp.ok,
    status: resp.status,
    raw: rawResp,
    json: safeJsonParse(rawResp),
  };
}

function safeJsonParse(text: string): any {
  try {
    return JSON.parse(text);
  } catch {
    return { raw: text };
  }
}

async function handleOpenAiChatCompletions(parameters: JsonObject): Promise<{ data: any; tokens: number }> {
  const model = getString(parameters.model);
  const prompt = getString(parameters.prompt);
  const n = typeof parameters.n === "number" ? parameters.n : 1;

  if (!model || !prompt) throw new HttpError(400, "Missing model/prompt");

  const resp = await callOpenAiJsonWithRetry("/v1/chat/completions", {
    model,
    messages: [{ role: "user", content: prompt }],
    n,
  });
  const parsed = await parseProviderResponse(resp);

  if (!parsed.ok) throw new HttpError(parsed.status, `OpenAI error ${parsed.status}: ${parsed.raw}`);
  return { data: parsed.json, tokens: extractTotalTokens(parsed.json) };
}

async function handleOpenAiResponses(parameters: JsonObject): Promise<{ data: any; tokens: number }> {
  const payload = cloneWithoutInternalFields(parameters);

  if (!getString(payload.model)) throw new HttpError(400, "Missing model for Responses API");
  if (!("input" in payload)) throw new HttpError(400, "Missing input for Responses API");

  const resp = await callOpenAiJsonWithRetry("/v1/responses", payload);
  const parsed = await parseProviderResponse(resp);

  if (!parsed.ok) throw new HttpError(parsed.status, `OpenAI error ${parsed.status}: ${parsed.raw}`);
  return { data: parsed.json, tokens: extractTotalTokens(parsed.json) };
}

async function handleOpenAiImageGeneration(parameters: JsonObject): Promise<{ data: any; tokens: number }> {
  const model = getString(parameters.model) ?? "gpt-image-2";
  const prompt = getString(parameters.prompt);
  if (!prompt) throw new HttpError(400, "Missing prompt for image generation");

  const payload: JsonObject = { model, prompt };
  copyOptionalString(parameters, payload, "size");
  copyOptionalString(parameters, payload, "quality");
  copyOptionalString(parameters, payload, "background");
  copyOptionalString(parameters, payload, "output_format");
  copyOptionalNumber(parameters, payload, "output_compression");
  copyOptionalNumber(parameters, payload, "n");

  const resp = await callOpenAiJsonWithRetry("/v1/images/generations", payload);
  const parsed = await parseProviderResponse(resp);

  if (!parsed.ok) throw new HttpError(parsed.status, `OpenAI error ${parsed.status}: ${parsed.raw}`);
  return { data: parsed.json, tokens: extractTotalTokens(parsed.json) || estimateImageOutputTokens(parameters) };
}

async function handleOpenAiImageEdit(parameters: JsonObject): Promise<{ data: any; tokens: number }> {
  const model = getString(parameters.model) ?? "gpt-image-2";
  const prompt = getString(parameters.prompt);
  if (!prompt) throw new HttpError(400, "Missing prompt for image edit");

  const imageUrls = collectImageDataUrls(parameters);
  if (imageUrls.length === 0) throw new HttpError(400, "Missing image/input_image/images for image edit");

  const buildForm = () => {
    const form = new FormData();
    form.append("model", model);
    form.append("prompt", prompt);

    appendOptionalString(form, parameters, "size");
    appendOptionalString(form, parameters, "quality");
    appendOptionalString(form, parameters, "background");
    appendOptionalString(form, parameters, "output_format");
    appendOptionalNumber(form, parameters, "output_compression");
    appendOptionalNumber(form, parameters, "n");

    for (const imageUrl of imageUrls) {
      const { blob, filename } = dataUrlToBlob(imageUrl);
      form.append("image[]", blob, filename);
    }

    const maskUrl = getString(parameters.mask);
    if (maskUrl) {
      const { blob, filename } = dataUrlToBlob(maskUrl);
      form.append("mask", blob, `mask_${filename}`);
    }

    return form;
  };

  const resp = await callOpenAiFormWithRetry("/v1/images/edits", buildForm);
  const parsed = await parseProviderResponse(resp);

  if (!parsed.ok) throw new HttpError(parsed.status, `OpenAI error ${parsed.status}: ${parsed.raw}`);
  return { data: parsed.json, tokens: extractTotalTokens(parsed.json) || estimateImageOutputTokens(parameters) };
}

function copyOptionalString(source: JsonObject, target: JsonObject, key: string) {
  const value = getString(source[key]);
  if (value) target[key] = value;
}

function copyOptionalNumber(source: JsonObject, target: JsonObject, key: string) {
  const value = source[key];
  if (typeof value === "number" && Number.isFinite(value)) target[key] = value;
}

function appendOptionalString(form: FormData, source: JsonObject, key: string) {
  const value = getString(source[key]);
  if (value) form.append(key, value);
}

function appendOptionalNumber(form: FormData, source: JsonObject, key: string) {
  const value = source[key];
  if (typeof value === "number" && Number.isFinite(value)) form.append(key, String(value));
}

async function handleDeepSeek(parameters: JsonObject): Promise<{ data: any; tokens: number }> {
  if (!DEEPSEEK_KEY) throw new Error("Missing env DEEPSEEK_KEY");

  const query = getString(parameters.query);
  if (!query) throw new HttpError(400, "Missing query");

  const resp = await fetch("https://api.deepseek.com/chat/completions", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${DEEPSEEK_KEY}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      model: "deepseek-chat",
      messages: [{ role: "user", content: query }],
      stream: false,
    }),
  });

  const parsed = await parseProviderResponse(resp);
  if (!parsed.ok) throw new HttpError(parsed.status, `DeepSeek error ${parsed.status}: ${parsed.raw}`);
  return { data: parsed.json, tokens: extractTotalTokens(parsed.json) };
}

async function logUsage(license_key: string, provider: string, tokens: number, machine_id?: string | null) {
  await supabase.from("api_usage").insert({
    license_key: license_key ?? "",
    provider,
    tokens_used: Math.max(0, Math.ceil(tokens || 0)),
    machine_id: machine_id ?? null,
  });
}

async function getOrCreateQuota(licenseKey: string) {
  const { data: quota, error } = await supabase
    .from("api_quotas")
    .select("token_limit")
    .eq("license_key", licenseKey)
    .is("machine_id", null)
    .maybeSingle();

  if (!error && quota) return quota.token_limit as number;

  await supabase.from("api_quotas").insert({
    license_key: licenseKey,
    machine_id: null,
    token_limit: 200000,
  });

  return 200000;
}

async function tokensUsed(licenseKey: string) {
  const { data, error } = await supabase
    .from("api_usage")
    .select("tokens_used")
    .eq("license_key", licenseKey);

  if (error) throw new Error("Erreur lecture usage tokens");

  return (data ?? []).reduce((acc: number, r: any) => acc + (r.tokens_used ?? 0), 0);
}

class HttpError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, {
      status: 204,
      headers: {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
        "Access-Control-Allow-Methods": "POST, OPTIONS",
      },
    });
  }

  if (req.method !== "POST") return textResponse("Use POST", 405);

  const rawBody = await req.text();
  let body: any;

  try {
    body = JSON.parse(rawBody);
  } catch {
    return textResponse("Bad JSON", 400);
  }

  const authHeader = req.headers.get("Authorization")?.replace("Bearer ", "");
  if (!authHeader) return textResponse("Missing Authorization", 401);

  let payloadJwt: any;
  try {
    payloadJwt = await verify(authHeader, HMAC_KEY);
  } catch {
    return textResponse("Invalid token", 401);
  }

  const licenseKey = payloadJwt.license_key as string;
  const machineId = (payloadJwt.machine_id ?? null) as string | null;
  if (!licenseKey) return textResponse("Missing license_key in token", 401);

  const provider = getString(body?.provider);
  const parameters = isPlainObject(body?.parameters) ? body.parameters : null;

  if (!provider) return textResponse("Missing provider", 400);
  if (!parameters) return textResponse("Missing parameters", 400);

  try {
    const limit = await getOrCreateQuota(licenseKey);
    const used = await tokensUsed(licenseKey);

    if (used >= limit) {
      return jsonResponse(
        {
          error: `Quota IA depasse (${used}/${limit} tokens). Contactez l'administrateur.`,
          used,
          limit,
          scope: "license",
        },
        403,
      );
    }

    let data: any;
    let rawUsed = 0;
    let route: OpenAiRoute | null = null;

    if (provider === "openai") {
      route = detectOpenAiRoute(parameters);

      if (route === "responses") {
        const result = await handleOpenAiResponses(parameters);
        data = result.data;
        rawUsed = result.tokens;
      } else if (route === "images_edits") {
        const result = await handleOpenAiImageEdit(parameters);
        data = result.data;
        rawUsed = result.tokens;
      } else if (route === "images_generations") {
        const result = await handleOpenAiImageGeneration(parameters);
        data = result.data;
        rawUsed = result.tokens;
      } else {
        const result = await handleOpenAiChatCompletions(parameters);
        data = result.data;
        rawUsed = result.tokens;
      }
    } else if (provider === "deepseek") {
      const result = await handleDeepSeek(parameters);
      data = result.data;
      rawUsed = result.tokens;
    } else {
      return textResponse(`Unknown provider "${provider}"`, 400);
    }

    const multiplier = usageMultiplier(provider, route);
    const billableUsed = Math.ceil(rawUsed * multiplier);
    await logUsage(licenseKey, provider, billableUsed, machineId);

    const newUsed = used + billableUsed;
    const remaining = limit - newUsed;
    const headers: Record<string, string> = {};

    if (limit > 0 && remaining >= 0 && remaining < Math.max(1000, Math.floor(limit * 0.1))) {
      headers["X-Usage-Remaining"] = remaining.toString();
      headers["X-Usage-Limit"] = limit.toString();
    }

    return jsonResponse(data, 200, headers);
  } catch (err) {
    if (err instanceof HttpError) return jsonResponse({ error: err.message }, err.status);
    const msg = err instanceof Error ? err.message : String(err);
    return jsonResponse({ error: msg }, 500);
  }
});

