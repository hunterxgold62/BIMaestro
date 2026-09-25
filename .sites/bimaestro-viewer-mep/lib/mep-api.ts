import { createClient } from '@supabase/supabase-js';
import type { MepSourceOverride, ResolvedShare, Scenario } from './mep-contract';

const projectUrl = process.env.NEXT_PUBLIC_SUPABASE_URL || 'https://xqovxfgghbqxwsadzhzl.supabase.co';
const publishableKey = process.env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY ||
  'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo';
const functionUrl = `${projectUrl.replace('.supabase.co', '.functions.supabase.co')}/mep-share`;

async function call<T>(body: object): Promise<T> {
  const response = await fetch(functionUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(publishableKey ? { apikey: publishableKey } : {}) },
    body: JSON.stringify(body),
    cache: 'no-store',
    referrerPolicy: 'no-referrer',
  });
  const value = await response.json() as Record<string, unknown>;
  if (!response.ok) throw new Error(typeof value.error === 'string' ? value.error : `Erreur ${response.status}`);
  return value as T;
}

export const resolveShare = (token: string) => call<ResolvedShare>({ action: 'resolve', token });
export const resolveTile = (token: string, revision: number, name: string) => call<{ url: string; bytes: number; sha256: string }>({ action: 'tile', token, revision, name });
export const updateValve = (token: string, expectedRevision: number, targetId: string, closed: boolean, participantName: string) =>
  call<{ scenario: Scenario }>({ action: 'scenario', commandKind: 'valve', token, expectedRevision, targetId, value: closed, participantName, operationId: crypto.randomUUID() });
export const updateSource = (token: string, expectedRevision: number, targetId: string, source: MepSourceOverride, participantName: string) =>
  call<{ scenario: Scenario }>({ action: 'scenario', commandKind: 'source', token, expectedRevision, targetId, value: source, participantName, operationId: crypto.randomUUID() });

export function subscribeToScenario(resolved: ResolvedShare, onScenario: (scenario: Scenario) => void) {
  if (!publishableKey) return () => {};
  const client = createClient(projectUrl, publishableKey, { auth: { persistSession: false } });
  void client.realtime.setAuth(resolved.realtimeToken);
  const channel = client.channel(`mep:${resolved.publication.id}`, { config: { private: true } })
    .on('broadcast', { event: 'scenario' }, ({ payload }) => onScenario(payload as Scenario))
    .subscribe();
  return () => { void client.removeChannel(channel); };
}

export function shareTokenFromLocation(): string | null {
  if (typeof window === 'undefined') return null;
  const match = window.location.hash.match(/^#\/share\/([A-Za-z0-9_-]{32,160})/);
  return match?.[1] || null;
}

export const readScenario = (token: string) => call<{ scenario: Scenario }>({ action: 'state', token });
export const saveMarkup = (token: string, markup: import('./mep-markup').Markup, remove = false) =>
  call<{ scenario: Scenario }>({ action: 'markup', token, markup, remove });

export const resolveTiles = (token: string, revision: number, names: string[]) => call<{ tiles: { name: string; url: string; bytes: number; sha256: string }[] }>({ action: 'tile', token, revision, names });
