import { z } from 'zod';

export const tileSchema = z.object({
  name: z.string().regex(/^tile-\d{5}\.glb\.gz$/),
  size: z.number().int().positive().max(48 * 1024 * 1024),
  sha256: z.string().regex(/^[a-f0-9]{64}$/),
  decodedBytes: z.number().int().positive().max(64 * 1024 * 1024),
  bounds: z.tuple([z.number(), z.number(), z.number(), z.number(), z.number(), z.number()]),
  elements: z.array(z.number().int().nonnegative()),
});
export type ModelTile = z.infer<typeof tileSchema>;
export const manifestSchema = z.object({
  schemaVersion: z.union([z.literal(1), z.literal(2)]),
  tiles: z.array(tileSchema).max(4095).optional(),
  name: z.string(),
  documentTitle: z.string().optional().default(''),
  viewName: z.string().optional().default(''),
  units: z.string(),
  coordinateSystem: z.string(),
  sourceDocumentId: z.string().optional(),
  sourceOrigin: z.tuple([z.number(), z.number(), z.number()]).optional(),
  sharedCoordinates: z.object({
    origin: z.tuple([z.number().finite(), z.number().finite(), z.number().finite()]),
    xAxis: z.tuple([z.number().finite(), z.number().finite(), z.number().finite()]).refine(axis => Math.abs(Math.hypot(axis[0], axis[1]) - 1) < .00001 && Math.abs(axis[2]) < .00001, 'Axe du repère partagé invalide'),
    siteName: z.string(),
  }).nullish(),
  files: z.record(z.string(), z.object({ bytes: z.number(), sha256: z.string() })),
}).refine(value => value.schemaVersion === 1 || (value.tiles && value.tiles.length > 0 && new Set(value.tiles.map(tile => tile.name)).size === value.tiles.length), 'Zones absentes ou dupliquées');

export type WebProperty = {
  index: number;
  key: string;
  stableKey?: string;
  center?: [number, number, number];
  size?: [number, number, number];
  elementId: number;
  name: string;
  category: string;
  typeName: string;
  levelName: string;
  documentTitle: string;
  selectionTargetKey?: string;
  properties: Record<string, string>;
};

export type MepPoint = { x: number; y: number; z: number } | { X: number; Y: number; Z: number } | [number, number, number] | string;
export type MepConnector = { index: number; key?: string; persistentKey?: string; elementKey: string; systemKey: string; position: MepPoint; endpointRole?: number; flowDirection?: string; crossSectionArea?: number; isConnected?: boolean };
export type MepConnection = { connectorA: number; connectorB: number; isInternal: boolean; isValveGateCandidate: boolean; elementKey: string };
export type MepDirectionExplanation = { reliability?: number | string; primarySourceName?: string; alternativeSourceNames?: string[]; influencingReturnName?: string; upstreamElementNames?: string[]; limitingControls?: string[]; rule?: string; hasAlternativeRoute?: boolean; isManual?: boolean };
export type MepPath = { elementKey: string; systemKey: string; startConnector: number; endConnector: number; points: MepPoint[]; flowState: number | string; hasCirculation: boolean; flowForward: boolean; directionState?: number | string; directionReason?: string; directionExplanation?: MepDirectionExplanation };
export type MepElement = { connectedToInlet?: boolean; connectedToReturn?: boolean; isPipeCurve?: boolean; isPipeFitting?: boolean; key: string; persistentId?: string; elementId: number; name: string; category: string; typeName: string; systemKey: string; systemName: string; classification?: string; connectorIndices: number[]; paths: MepPath[]; flowState: number | string };
export type MepValve = { elementKey: string; isEnabledAsValve: boolean; isClosed: boolean; confidence?: number | string; detectionReason?: string; upstreamState?: number | string; downstreamState?: number | string };
export type MepSource = { elementKey: string; systemKey?: string; name?: string; confidence?: number | string; boundaryKind?: number | string; isActive: boolean; entryConnectorIndex: number; exitConnectorIndex: number };
export type MepSystemColor = {
  r?: number; g?: number; b?: number; a?: number;
  R?: number; G?: number; B?: number; A?: number;
  scR?: number; scG?: number; scB?: number; scA?: number;
  ScR?: number; ScG?: number; ScB?: number; ScA?: number;
};
export type MepSystem = { key: string; name: string; color?: MepSystemColor | string; isVisible: boolean; elementCount: number };
export type MepAnalysisSettings = { allowImplicitTerminals?: boolean; endpoints?: Record<string, number> };
export type MepAnalysisReference = { modelFingerprint: string; scenarioFingerprint: string; engineVersion: string; label: string; elements: unknown[]; valves: Record<string, boolean> };
export type MepImpactItem = { elementKey: string; elementId: number; name: string; systemName: string; isEquipment: boolean; change: string; before: string; after: string; limitingValveKeys: string[]; alternativeRouteKeys: string[] };
export type MepImpactReport = { engineVersion: string; comparable: boolean; referenceLabel: string; modelFingerprint: string; scenarioFingerprint: string; assumptions: string[]; items: MepImpactItem[]; changedValveKeys: string[]; lostArrivalCount: number; equipmentLostArrivalCount: number; alternativeCount: number };
export type MepDiagnostic = { key: string; title: string; explanation: string; elementKey: string; severity: number; isAggregate?: boolean };
export type MepGraph = { connectors: MepConnector[]; connections: MepConnection[]; elements: MepElement[]; valves: MepValve[]; sources: MepSource[]; systems: MepSystem[]; directionConstraints?: unknown[]; engineVersion?: string; allowImplicitTerminals?: boolean; analysisReference?: MepAnalysisReference; impactReport?: MepImpactReport; diagnostics?: MepDiagnostic[] };
export type MepReplay = { schemaVersion: number; documentLabel: string; graph: MepGraph };
export type ViewerDoor = { key: string; center: number[]; hinge: number[]; secondHinge: number[] };
export type ViewerConfig = { spawn: number[]; eyeHeight: number; initialYaw: number; doors: ViewerDoor[] };

export type ViewerPackage = {
  manifest: z.infer<typeof manifestSchema>;
  modelUrl: string;
  overviewUrl?: string;
  replay: MepReplay;
  properties: WebProperty[];
  viewer: ViewerConfig | null;
  dispose: () => void;
};

export type MepSourceOverride = 'inlet' | 'outlet' | 'none';
export type Scenario = { revision: number; state: { reservationLots?: import('./mep-lots').ReservationLots; markups?: Record<string, import('./mep-markup').Markup>; valves?: Record<string, boolean>; sources?: Record<string, MepSourceOverride>; analysis?: MepAnalysisSettings }; updated_by: string; updated_at: string };
export type ResolvedShare = {
  publication: { id: string; name: string; slug: string; revision: number; expiresAt: string };
  role: 'viewer' | 'editor';
  packageUrl: string;
  storageBackend?: 'supabase' | 'r2';
  scenario: Scenario;
  events: Array<{ scenario_revision: number; participant_name: string; target_id: string; previous_value: boolean | null; next_value: boolean; created_at: string }>;
  realtimeToken: string;
};
