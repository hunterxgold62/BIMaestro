'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { PanelLeft, PanelRight } from 'lucide-react';
import { ArrowDown, ArrowUp, ArrowLeftRight, Scissors, ChartNoAxesCombined, MessageSquare, Box, ChevronRight, CircleGauge, Contrast, Eye, EyeOff, Footprints, Hand, Layers3, LoaderCircle, LockKeyhole, Pencil, Plane, RotateCcw, SunMedium, Users, Waves } from 'lucide-react';
import * as THREE from 'three';
import { MepVrVisit } from '@/lib/mep-vr';
import { VrEntry } from './vr-entry';
import { MepGamepad, MepWalkGamepad, maquetteXbox, readBrowserPads, xbox } from '@/lib/mep-gamepad';
import { adjustGamepadControl, focusGamepadControl, gamepadControls, gamepadScope, GamepadMenuRepeat, moveGamepadKey, type GamepadField } from '@/lib/mep-gamepad-ui';
import { GamepadFieldEditor } from '@/components/gamepad-field-editor';
import { locateRoom, type RoomLocation } from '@/lib/mep-room';
import { MepMiniMap } from '@/lib/mep-minimap';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { downloadExchange } from '@/lib/mep-exchange';
import { annotationsBcf, downloadBcf, type BcfCapture } from '@/lib/mep-bcf';
import { ElementSelection, zoomStep, orbitSelection } from '@/lib/mep-selection';
import { anchorMarkup, reconcileMarkups } from '@/lib/mep-revisions';
import { TransformControls } from 'three/examples/jsm/controls/TransformControls.js';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { Line2 } from 'three/examples/jsm/lines/Line2.js';
import { LineGeometry } from 'three/examples/jsm/lines/LineGeometry.js';
import { LineMaterial } from 'three/examples/jsm/lines/LineMaterial.js';
import { acceleratedRaycast, computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { Button } from '@/components/ui/button';
import { SectionControls } from '@/components/section-controls';
import { sectionFromFace, sectionPlanes, sectionContains, maximumSections, type FaceSection } from '@/lib/mep-section';
import { BuildingVisibility, blocksView, isObjectVisible } from '@/lib/mep-visibility';
import { ModelStream, tileBounds, disposeModel } from '@/lib/mep-stream';
import { isReservationHost, hostDepthCm } from '@/lib/mep-host';
import { ModelPreparation } from '@/lib/mep-prepare';
import { AsyncWallCutouts } from '@/lib/mep-cutouts-async';
import { markupObject, disposeMarkups, reservationLot, reservationLabel, dimensionDistance, dimensionFoot, moveToDimension, editMarkupOnFace, type ReservationDimension, type Markup } from '@/lib/mep-markup';
import { ReservationFields } from './reservation-fields';
import { ReservationLotsPanel } from './reservation-lots';
import { MeasureControls } from './measure-controls';
import { lotDetails, reservationLots, type ReservationLot, type ReservationLots } from '@/lib/mep-lots';
import { measureDistances, measureObject, horizontalDimension, measureEnd, type MeasureMode, type TemporaryMeasure, type MeasurePoint } from '@/lib/mep-measure';
import { saveMarkup, readScenario, saveReservationLot } from '@/lib/mep-api';
import { Badge } from '@/components/ui/badge';
import type { MepGraph, MepSourceOverride, MepSystemColor, ResolvedShare, Scenario, ViewerPackage, WebProperty } from '@/lib/mep-contract';
import { canAnimate, connectionLabel } from '@/lib/mep-engine';
import { createFlowMaterial, attachFlowDistances, createFlowChevronGeometry } from '@/lib/mep-flow-material';
import { createWaterVolume } from '@/lib/mep-water-volume';
import { waterPathRadii } from '@/lib/mep-water-size';
import { flowPhases, flowPathKey, flowArrowDistance, flowTrace, flowFollowChain, flowPulseArrivals } from '@/lib/mep-flow-continuity';
import { explainFlowEnd, type FlowEnd } from '@/lib/mep-flow-end';
import { equipmentStopKind } from '@/lib/mep-equipment-stop';
import { useMepCalculation } from '@/hooks/use-mep-calculation';
import { MepImpactPanel } from '@/components/mep-impact-panel';
import { fetchR2PilotAsset, resolveShare, shareTokenFromLocation, subscribeToScenario, updateSource, updateValve, useR2Storage } from '@/lib/mep-api';
import { openViewerPackage } from '@/lib/mep-package';
import { toWebPoint } from '@/lib/mep-point';

// Même principe que l'index spatial du plugin Revit : les collisions ne
// parcourent plus tous les triangles de la maquette à chaque rayon.
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
THREE.Mesh.prototype.raycast = acceleratedRaycast;

type NavigationMode = 'orbit' | 'maquette';
type ContextMenuState = { depthCm?: number; x: number; y: number; index: number; position: [number, number, number]; normal: [number, number, number] };
type VisualRole = 'floor' | 'wall' | 'base' | 'ceiling' | 'mep' | 'other';
type AnimatedFlowArrow = {
  mesh: THREE.Mesh;
  elementKey: string;
  pathOrdinal: number;
  systemKey: string;
  points: THREE.Vector3[];
  cumulative: number[];
  length: number;
  slot: number;
  flowStart: number;
  spacing: number;
  enabled: boolean;
  forward: boolean;
  waterRadius: number | null;
};
type FlowVisual = {
  objects: THREE.Object3D[];
  material: LineMaterial;
  haloMaterial: LineMaterial;
  elementKey: string;
  pathOrdinal: number;
  systemKey: string;
  center: THREE.Vector3;
  stateOpacity: number;
  targetActive: number;
  current: ReturnType<typeof createFlowMaterial>;
  water?: ReturnType<typeof createWaterVolume>;
  points: THREE.Vector3[];
  color: string;
  radii: { start: number; end: number } | null;
};
type ValveVisual = { marker: THREE.Mesh; handle: THREE.Mesh; targetAngle: number; available: boolean; elementKey: string; systemKey: string };
type ModelMaterialState = {
  material: THREE.Material;
  opacity: number;
  transparent: boolean;
  depthWrite: boolean;
};
type ViewActions = {
  traceSelectedFlow: () => void;
  followSelectedFlow: (elementKey?: string, reverse?: boolean, branchKey?: string) => void;
  chooseFollowBranch: (key: string) => void;
  stopFollowing: () => void;
  toggleLocalCut: () => void;
  isometric: () => void;
  frameSelected: () => void;
  frameMarkup: (mark: Markup) => void;
  captureMarkup: (mark: Markup) => Promise<BcfCapture>;
  enterSpectator: () => void;
  setInput: (code: string, active: boolean) => void;
  toggleFlight: () => void;
  applyAppearance: () => void;
  updateMepState: (graph: MepGraph | null, hiddenSystems: Set<string>) => void;
  applyFlowPresentation: () => void;
};
const demoNetworks = [
  { key: 'demo-a', name: 'Eau glacée — Aller', color: '#29b97f', elementCount: 184 },
  { key: 'demo-r', name: 'Eau glacée — Retour', color: '#4a8ed8', elementCount: 177 },
  { key: 'demo-c', name: 'Chauffage', color: '#e37b45', elementCount: 96 },
];

function visualRoleOf(mesh: THREE.Mesh, material: THREE.Material): VisualRole {
  const metadata = Object.values(mesh.userData || {})
    .filter((value): value is string => typeof value === 'string')
    .join(' ');
  const label = `${mesh.name} ${material.name} ${metadata}`.toLocaleLowerCase('fr');
  if (/\b(sol|floor|dalle|slab|radier)\b/.test(label)) return 'floor';
  if (/\b(mur|wall|cloison|voile)\b/.test(label)) return 'wall';
  if (/\b(massif|socle|plinth|base|fondation|foundation|pad)\b/.test(label)) return 'base';
  if (/\b(plafond|ceiling|toit|roof)\b/.test(label)) return 'ceiling';
  if (/\b(tuyau|pipe|gaine|duct|raccord|fitting|canalisation|equipment|équipement)\b/.test(label)) return 'mep';
  return 'other';
}

function cleanViewerColor(source: THREE.Color, role: VisualRole) {
  const color = source.clone();
  const hsl = { h: 0, s: 0, l: 0 };
  color.getHSL(hsl);
  // Les couleurs de réseaux restent intactes. Les neutres Revit reçoivent en
  // revanche des valeurs distinctes pour que murs, sol et massifs ne fusionnent plus.
  if (hsl.s > .22) return color;
  if (role === 'floor') return new THREE.Color('#7c8389');
  if (role === 'wall') return new THREE.Color('#c9cdd0');
  if (role === 'base') return new THREE.Color('#aeb5ba');
  if (role === 'ceiling') return new THREE.Color('#d9dde0');
  if (role === 'mep') return new THREE.Color('#e0e4e6');
  color.setHSL(hsl.h, Math.min(hsl.s, .08), THREE.MathUtils.clamp(hsl.l + (hsl.l < .45 ? .2 : .1), .38, .86));
  return color;
}

function legacyColorString(value: string) {
  const trimmed = value.trim();
  const argb = /^#([0-9a-f]{8})$/i.exec(trimmed);
  if (argb) return `#${argb[1].slice(2)}`;
  const scRgb = /^sc#\s*([\d.+-]+)\s*,\s*([\d.+-]+)\s*,\s*([\d.+-]+)\s*,\s*([\d.+-]+)$/i.exec(trimmed);
  if (scRgb) {
    const channels = scRgb.slice(2).map(Number);
    if (channels.every(Number.isFinite)) {
      const [r, g, b] = channels.map((channel) => {
        const linear = Math.min(1, Math.max(0, channel));
        const srgb = linear <= .0031308 ? linear * 12.92 : 1.055 * linear ** (1 / 2.4) - .055;
        return Math.round(255 * srgb);
      });
      return `rgb(${r},${g},${b})`;
    }
  }
  return trimmed;
}

function colorOf(system: { color?: MepSystemColor | string }, index: number) {
  if (typeof system.color === 'string') return legacyColorString(system.color);
  if (system.color) {
    const r = system.color.r ?? system.color.R ?? system.color.scR ?? system.color.ScR;
    const g = system.color.g ?? system.color.G ?? system.color.scG ?? system.color.ScG;
    const b = system.color.b ?? system.color.B ?? system.color.scB ?? system.color.ScB;
    if ([r, g, b].every((value) => typeof value === 'number' && Number.isFinite(value))) {
      const multiplier = Math.max(r!, g!, b!) <= 1 ? 255 : 1;
      return `rgb(${Math.round(r! * multiplier)},${Math.round(g! * multiplier)},${Math.round(b! * multiplier)})`;
    }
  }
  return ['#29b97f', '#4a8ed8', '#e37b45', '#b978cf'][index % 4];
}

function flowStateLabel(value: number | string | undefined) {
  const normalized = typeof value === 'string' ? value.toLowerCase() : value;
  if (normalized === 2 || normalized === 'supplied') return 'relié à une arrivée ou un retour';
  if (normalized === 1 || normalized === 'isolated') return 'isolé';
  return 'indéterminé';
}

function confidenceLabel(value: number | string | undefined) {
  const normalized = typeof value === 'string' ? value.toLowerCase() : value;
  if (normalized === 2 || normalized === 'high') return 'élevée';
  if (normalized === 1 || normalized === 'medium') return 'moyenne';
  return 'faible';
}

function reliabilityLabel(value: number | string | undefined) {
  const normalized = typeof value === 'string' ? value.toLowerCase() : value;
  if (normalized === 0 || normalized === 'reliable') return 'Sens cohérent avec les règles';
  if (normalized === 1 || normalized === 'inferred') return 'Sens déduit';
  if (normalized === 3 || normalized === 'manual') return 'Sens imposé manuellement';
  return 'Sens à confirmer';
}

function sampleFlowPath(animation: Pick<AnimatedFlowArrow, 'points' | 'cumulative' | 'length'>, progress: number) {
  const distance = progress * animation.length;
  let segment = 1;
  while (segment < animation.cumulative.length - 1 && animation.cumulative[segment] < distance) segment++;
  const previousDistance = animation.cumulative[segment - 1];
  const segmentLength = Math.max(animation.cumulative[segment] - previousDistance, 0.000001);
  const amount = THREE.MathUtils.clamp((distance - previousDistance) / segmentLength, 0, 1);
  const start = animation.points[segment - 1];
  const end = animation.points[segment];
  return { position: start.clone().lerp(end, amount), direction: end.clone().sub(start).normalize() };
}

function repairLegacyNormals(geometry: THREE.BufferGeometry) {
  const positions = geometry.getAttribute('position');
  const normals = geometry.getAttribute('normal');
  if (!positions) return false;
  geometry.computeBoundingBox();
  const size = geometry.boundingBox?.getSize(new THREE.Vector3()) || new THREE.Vector3();
  const scale = Math.max(size.x, size.y, size.z, 1);
  const isVolume = [size.x, size.y, size.z].filter((extent) => extent > scale * .001).length === 3;
  let invalid = !normals || normals.count !== positions.count;
  if (!invalid && isVolume && normals.count > 3) {
    const first = new THREE.Vector3().fromBufferAttribute(normals, 0).normalize();
    const stride = Math.max(1, Math.floor(normals.count / 96));
    let different = false;
    for (let index = stride; index < normals.count; index += stride) {
      const sample = new THREE.Vector3().fromBufferAttribute(normals, index).normalize();
      if (Math.abs(first.dot(sample)) < .995) { different = true; break; }
    }
    invalid = !different;
  }
  if (!invalid) return false;
  geometry.computeVertexNormals();
  geometry.normalizeNormals();
  return true;
}

export function MepViewer() {
  const streamRef = useRef<ModelStream | null>(null);
  const [cutting, setCutting] = useState(false);
  const [loadedZones, setLoadedZones] = useState(0);
  const cutoutsRef = useRef<AsyncWallCutouts | null>(null);
  const updateCutoutsRef = useRef<(() => void) | null>(null);
  const markupGroupRef = useRef<THREE.Group | null>(null);
  const markupLabelsRef = useRef<HTMLDivElement>(null);
  const markupsRef = useRef<Markup[]>([]);
  const cutoutMarkupsRef = useRef<Markup[]>([]);
  const [draftMarkup, setDraftMarkup] = useState<Markup | null>(null);
  const [markupError, setMarkupError] = useState<string | null>(null);
  const [activeMarkup, setActiveMarkup] = useState<string | null>(null);
  const draftOpenRef = useRef(false);
  const draftRef = useRef<Markup | null>(null);
  useEffect(() => { draftRef.current = draftMarkup; }, [draftMarkup]);
  const [editMode, setEditMode] = useState<'translate' | 'scale'>('translate');
  const editModeRef = useRef(editMode);
  useEffect(() => { editModeRef.current = editMode; }, [editMode]);
  const [relocating, setRelocating] = useState<Markup | null>(null);
  const relocatingRef = useRef(relocating);
  useEffect(() => { relocatingRef.current = relocating; }, [relocating]);
  const [markupListOpen, setMarkupListOpen] = useState(false);
  const [currentLot, setCurrentLot] = useState('MEP');
  const [lotsOpen, setLotsOpen] = useState(false);
  const lotSettingsRef = useRef<ReservationLots>({});
  const activeMarkupRef = useRef<string | null>(null);
  useEffect(() => { activeMarkupRef.current = activeMarkup; }, [activeMarkup]);
  const [measureOpen, setMeasureOpen] = useState(false);
  const [measureMode, setMeasureMode] = useState<MeasureMode>('free');
  const measureModeRef = useRef<MeasureMode>('free');
  const [measures, setMeasures] = useState<TemporaryMeasure[]>([]);
  const [pickingMeasure, setPickingMeasure] = useState(false);
  const [measureStart, setMeasureStart] = useState<MeasurePoint | null>(null);
  const measurePickRef = useRef<{ picking: boolean; start: MeasurePoint | null }>({ picking: false, start: null });
  const measureLabelsRef = useRef<HTMLDivElement>(null);
  const measureStateRef = useRef({ open: measureOpen, measures, start: measureStart });
  useEffect(() => { measureStateRef.current = { open: measureOpen, measures, start: measureStart }; }, [measureOpen, measures, measureStart]);
  const cancelMeasure = () => { measurePickRef.current = { picking: false, start: null }; setPickingMeasure(false); setMeasureStart(null); };

  const [onlyCurrentLot, setOnlyCurrentLot] = useState(false);
  const [exportLot, setExportLot] = useState('');
  const [exporting, setExporting] = useState(false);
  const exportWorkerRef = useRef<Worker | null>(null);
  useEffect(() => () => { exportWorkerRef.current?.terminate(); }, []);
  const [pickingDimension, setPickingDimension] = useState(false);
  const pickingDimensionRef = useRef(false);
  useEffect(() => { pickingDimensionRef.current = pickingDimension; }, [pickingDimension]);
  useEffect(() => { if (!draftMarkup) setPickingDimension(false); }, [draftMarkup]);
  useEffect(() => { draftOpenRef.current = !!draftMarkup; }, [draftMarkup]);
  const mountRef = useRef<HTMLDivElement>(null);
  const miniMapRef = useRef<HTMLCanvasElement>(null);
  const [roomLocation, setRoomLocation] = useState<RoomLocation | null>(null);
  const [miniMapVisible, setMiniMapVisible] = useState(false);
  const miniMapVisibleRef = useRef(false);
  useEffect(() => { miniMapVisibleRef.current = miniMapVisible; }, [miniMapVisible]);
  const [inputDevice, setInputDevice] = useState<'keyboard' | 'gamepad'>('keyboard');
  const inputDeviceRef = useRef<'keyboard' | 'gamepad'>('keyboard');
  const [connectedPad, setConnectedPad] = useState<string | null>(null);
  const [gamepadMenu, setGamepadMenu] = useState(false);
  const [gamepadField, setGamepadField] = useState<GamepadField | null>(null);
  const gamepadFieldRef = useRef<GamepadField | null>(null);
  const closeGamepadField = useCallback(() => {
    const target = gamepadFieldRef.current; gamepadFieldRef.current = null; setGamepadField(null);
    requestAnimationFrame(() => { if (target?.isConnected) target.focus({ preventScroll: true }); });
  }, []);
  const controlsRef = useRef<OrbitControls | null>(null);
  const vrVisitRef = useRef<MepVrVisit | null>(null);
  const [vrActive, setVrActive] = useState(false);
  const flowGroupRef = useRef<THREE.Group | null>(null);
  const valveGroupRef = useRef<THREE.Group | null>(null);
  const modelMaterialStatesRef = useRef<ModelMaterialState[]>([]);
  const flowFocusAppliedRef = useRef<boolean | null>(null);
  const flowsVisibleRef = useRef(false);
  const waterViewRef = useRef(false);
  const valveMarkersVisibleRef = useRef(false);
  const viewActionsRef = useRef<ViewActions | null>(null);
  const [section, setSection] = useState<FaceSection[]>([]);
  const sectionRef = useRef(section);
  const [selectedSection, setSelectedSection] = useState<string | null>(null);
  const selectedSectionRef = useRef(selectedSection);
  const [pickingSection, setPickingSection] = useState(false);
  const pickingSectionRef = useRef(false);
  const sectionSerial = useRef(0);
  const [sectionExtent, setSectionExtent] = useState(30);
  useEffect(() => { selectedSectionRef.current = selectedSection; }, [selectedSection]);
  useEffect(() => { pickingSectionRef.current = pickingSection; }, [pickingSection]);
  const [sectionOpen, setSectionOpen] = useState(false);
  useEffect(() => { sectionRef.current = section; setContextMenu(null); }, [section]);
  const modelOpacityRef = useRef(1);
  const realisticLightingRef = useRef(true);
  const highContrastRef = useRef(true);
  const navigationModeRef = useRef<NavigationMode>('orbit');
  const [navigationMode, setNavigationMode] = useState<NavigationMode>('orbit');
  const [flowsVisible, setFlowsVisible] = useState(false);
  const [panelCollapsed, setPanelCollapsed] = useState(false);
  const [followingFlow, setFollowingFlow] = useState(false);
  const [followEnd, setFollowEnd] = useState<FlowEnd | null>(null);
  const [followPaused, setFollowPaused] = useState(false);
  const followPausedRef = useRef(false);
  const [equipmentStops, setEquipmentStops] = useState(true);
  const equipmentStopsRef = useRef(true);
  equipmentStopsRef.current = equipmentStops;
  const [equipmentPause, setEquipmentPause] = useState<string | null>(null);
  const [followSpeed, setFollowSpeed] = useState(1);
  const followSpeedRef = useRef(1);
  const [followStep, setFollowStep] = useState<{ network: string; element: string; color: string; history: string[]; reverse: boolean } | null>(null);
  const [clearFollowView, setClearFollowView] = useState(true);
  const [followBranches, setFollowBranches] = useState<Array<{ key: string; name: string }>>([]);
  const clearFollowViewRef = useRef(true);
  clearFollowViewRef.current = clearFollowView;
  const followingSystemKeyRef = useRef<string | null>(null);
  const [localCut, setLocalCut] = useState(false);
  const [waterView, setWaterView] = useState(false);
  const [valveMarkersVisible, setValveMarkersVisible] = useState(false);
  const [flowRenderStats, setFlowRenderStats] = useState({ paths: 0, arrows: 0, fallbackPaths: 0 });
  const [flightMode, setFlightMode] = useState(false);
  const [modelOpacity, setModelOpacity] = useState(1);
  const [realisticLighting, setRealisticLighting] = useState(true);
  const [highContrast, setHighContrast] = useState(true);
  const [resolved, setResolved] = useState<ResolvedShare | null>(null);
  const [viewerPackage, setViewerPackage] = useState<ViewerPackage | null>(null);
  const [scenario, setScenario] = useState<Scenario | null>(null);
  const { graph, reportText, calculating, calculationError, setReference } = useMepCalculation(viewerPackage?.replay.graph, scenario);
  const [impactOpen, setImpactOpen] = useState(false);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const selectedIndexRef = useRef<number | null>(null);
  const flowPulseRequestRef = useRef(0);
  useEffect(() => { selectedIndexRef.current = selectedIndex; }, [selectedIndex]);
  const [inspectionHistory, setInspectionHistory] = useState<number[]>([]);
  const [advancedPanelOpen, setAdvancedPanelOpen] = useState(false);
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null);
  const [hiddenSystems, setHiddenSystems] = useState<Set<string>>(new Set());
  const [demoValveClosed, setDemoValveClosed] = useState(false);
  const [loading, setLoading] = useState(true);
  const [opening, setOpening] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const tokenRef = useRef<string | null>(null);
  const [shareToken, setShareToken] = useState<string | null>(null);
  const graphStateRef = useRef<MepGraph | null>(null);
  const hiddenSystemsRef = useRef<Set<string>>(new Set());
  const selectedSystemKeyRef = useRef<string | null>(null);
  const hoveredSystemKeyRef = useRef<string | null>(null);

  useEffect(() => {
    graphStateRef.current = graph;
    hiddenSystemsRef.current = hiddenSystems;
  }, [graph, hiddenSystems]);

  useEffect(() => {
    let disposed = false;
    const token = shareTokenFromLocation();
    tokenRef.current = token;
    void (async () => {
      if (!token) { setLoading(false); setOpening(false); return; }
      try {
        const access = await resolveShare(token);
        const pkg = await openViewerPackage(access.packageUrl, useR2Storage(access.publication.id, access.storageBackend)
          ? () => fetchR2PilotAsset(token, access.publication.revision, 'index.zip') : undefined);
        if (disposed) { pkg.dispose(); return; }
        setResolved(access); setViewerPackage(pkg); setScenario(access.scenario); setShareToken(token);
      } catch (caught) { setError(caught instanceof Error ? caught.message : 'Partage indisponible'); }
      finally { if (!disposed) setLoading(false); }
    })();
    return () => { disposed = true; };
  }, []);
  useEffect(() => () => viewerPackage?.dispose(), [viewerPackage]);
  useEffect(() => {
    if (!resolved || !viewerPackage) return;
    return subscribeToScenario(resolved, (next) => {
      setScenario(current => !current || next.revision > current.revision ? next : current);
    });
  }, [resolved, viewerPackage]);

  useEffect(() => { setMeasures([]); setMeasureOpen(false); cancelMeasure(); }, [viewerPackage]);
  const propertiesByIndex = useMemo(() => new Map(viewerPackage?.properties.map((item) => [item.index, item]) || []), [viewerPackage]);
  const propertiesByKey = useMemo(() => new Map(viewerPackage?.properties.map((item) => [item.key, item]) || []), [viewerPackage]);
  const allMarkups = useMemo(() => reconcileMarkups(Object.values(scenario?.state.markups || {}), viewerPackage?.properties || [], resolved?.publication.revision || 1), [scenario, resolved, viewerPackage]);
  const lotSettings = useMemo(() => scenario?.state.reservationLots || {}, [scenario?.state.reservationLots]);
  useEffect(() => { lotSettingsRef.current = lotSettings; }, [lotSettings]);
  const lots = useMemo(() => reservationLots(allMarkups, lotSettings), [allMarkups, lotSettings]);
  const listedMarkups = useMemo(() => allMarkups.filter(mark => !onlyCurrentLot || reservationLot(mark) === currentLot), [allMarkups, onlyCurrentLot, currentLot]);
  const reviewMark = relocating || allMarkups.find(mark => mark.id === activeMarkup && mark.needsReview);
  const reviewCount = allMarkups.filter(mark => mark.needsReview).length;
  const reviewSections = useRef<typeof section | null>(null);
  const reviewing = !!reviewMark;
  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      if (reviewing && reviewSections.current === null) {
        reviewSections.current = section; setSection([]);
      } else if (!reviewing && reviewSections.current !== null) {
        setSection(reviewSections.current); reviewSections.current = null;
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [reviewing, section]);
  const visibleMarkups = useMemo(() => reviewMark ? [reviewMark] : listedMarkups.filter(mark => !mark.needsReview), [listedMarkups, reviewMark]);
  const cutoutMarkups = useMemo(() => reviewMark ? [reviewMark] : allMarkups.filter(mark => !mark.needsReview), [allMarkups, reviewMark]);
  const dimensionMarks = useMemo(() => draftMarkup ? [draftMarkup] : visibleMarkups.filter(mark => mark.id === activeMarkup), [visibleMarkups, draftMarkup, activeMarkup]);
  const hasDraftMarkup = !!draftMarkup;
  useEffect(() => {
    const marks = visibleMarkups;
    markupsRef.current = marks;
    const group = markupGroupRef.current;
    if (group) { disposeMarkups(group); marks.forEach(mark => group.add(markupObject(mark, { color: lotDetails(reservationLot(mark), lotSettings).color, showDimensions: mark.id === activeMarkup && !hasDraftMarkup }))); }
  }, [visibleMarkups, viewerPackage, propertiesByKey, activeMarkup, hasDraftMarkup, lotSettings]);
  useEffect(() => {
    cutoutMarkupsRef.current = cutoutMarkups;
    const timer = window.setTimeout(() => {
      try { updateCutoutsRef.current?.(); }
      catch (caught) { setError(caught instanceof Error ? caught.message : 'Découpe impossible sur ce mur.'); }
    }, 200);
    return () => window.clearTimeout(timer);
  }, [cutoutMarkups]);
  useEffect(() => {
    if (!resolved) return;
    let disposed = false;
    const refresh = async () => {
      if (!tokenRef.current || document.hidden) return;
      try { const { scenario: next } = await readScenario(tokenRef.current); if (!disposed) setScenario(current => !current || next.revision > current.revision ? next : current); } catch { /* Retry on next poll. */ }
    };
    const timer = window.setInterval(() => void refresh(), 5000);
    return () => { disposed = true; window.clearInterval(timer); };
  }, [resolved]);

  const selectedProperty = selectedIndex === null ? null : propertiesByIndex.get(selectedIndex) || null;
  const selectedMepElement = selectedProperty && graph ? graph.elements.find((element) => element.key === selectedProperty.key) || null : null;
  const selectedValve = selectedMepElement && graph ? graph.valves.find((valve) => valve.elementKey === selectedMepElement.key && valve.isEnabledAsValve) || null : null;
  const systems = graph?.systems?.length ? graph.systems : demoNetworks;

  useEffect(() => {
    selectedSystemKeyRef.current = selectedMepElement?.systemKey || null;
    viewActionsRef.current?.applyFlowPresentation();
  }, [selectedMepElement]);

  const changeNavigationMode = useCallback((mode: NavigationMode) => {
    navigationModeRef.current = mode; setNavigationMode(mode);
    setMiniMapVisible(mode === 'maquette');
    if (controlsRef.current) controlsRef.current.enabled = mode === 'orbit';
    if (mode === 'maquette') viewActionsRef.current?.enterSpectator();
  }, []);

  useEffect(() => { modelOpacityRef.current = modelOpacity; viewActionsRef.current?.applyAppearance(); }, [modelOpacity]);
  useEffect(() => { waterViewRef.current = waterView; viewActionsRef.current?.applyFlowPresentation(); }, [waterView]);
  useEffect(() => { realisticLightingRef.current = realisticLighting; viewActionsRef.current?.applyAppearance(); }, [realisticLighting]);
  useEffect(() => { highContrastRef.current = highContrast; viewActionsRef.current?.applyAppearance(); }, [highContrast]);

  const applyFlowFocus = useCallback((visible: boolean, force = false) => {
    if (!force && flowFocusAppliedRef.current === visible) return;
    for (const state of modelMaterialStatesRef.current) {
      const opacity = state.opacity * modelOpacityRef.current;
      // Afficher les flux ne doit plus modifier la maquette. Seul le réglage
      // explicite de transparence peut changer son opacité.
      state.material.opacity = opacity;
      state.material.transparent = modelOpacityRef.current < 0.995 || state.transparent;
      state.material.depthWrite = modelOpacityRef.current < 0.995 ? false : state.depthWrite;
      state.material.needsUpdate = true;
    }
    flowFocusAppliedRef.current = visible;
  }, []);

  useEffect(() => {
    flowsVisibleRef.current = flowsVisible;
    if (flowGroupRef.current) flowGroupRef.current.visible = flowsVisible;
    applyFlowFocus(flowsVisible, true);
  }, [applyFlowFocus, flowsVisible]);

  useEffect(() => {
    valveMarkersVisibleRef.current = valveMarkersVisible;
    if (valveGroupRef.current) valveGroupRef.current.visible = valveMarkersVisible;
  }, [valveMarkersVisible]);

  useEffect(() => {
    const mount = mountRef.current;
    if (!mount) return;
    const initialGraph = graphStateRef.current;
    const initialHiddenSystems = hiddenSystemsRef.current;
    let disposed = false;
    let sceneReady = !viewerPackage, geometryReady = !viewerPackage, cutsPending = false;
    let openingReleased = !viewerPackage;
    if (viewerPackage) queueMicrotask(() => { if (!disposed) { setOpening(true); setLoadedZones(0); } });
    let statsTimer: number | null = null;
    const scene = new THREE.Scene();
    const markupGroup = new THREE.Group(); scene.add(markupGroup); markupGroupRef.current = markupGroup;
    markupsRef.current.forEach(mark => markupGroup.add(markupObject(mark, { color: lotDetails(reservationLot(mark), lotSettingsRef.current).color, showDimensions: mark.id === activeMarkupRef.current && !draftRef.current })));
    const measureGroup = new THREE.Group(); scene.add(measureGroup);
    let lastMeasureState: typeof measureStateRef.current | null = null;
    const overlayScene = new THREE.Scene();
    let buildingVisibility: BuildingVisibility | null = viewerPackage ? new BuildingVisibility([], propertiesByIndex) : null;
    const preparation = viewerPackage ? new ModelPreparation([...propertiesByIndex].filter(([, prop]) => blocksView(prop)).map(([id]) => id)) : null;
    let stream: ModelStream | null = null;
    let geometryTimer: number | undefined;
    let lastSelection: number | null = null;

    let selectedPivot: THREE.Vector3 | null = null;
    let orbitPointer: { id: number; x: number; y: number; pivot: THREE.Vector3 } | null = null;
    const selection = new ElementSelection(); scene.add(selection.group);
    scene.background = new THREE.Color('#dfe8ee');
    const camera = new THREE.PerspectiveCamera(48, 1, 0.05, 10000);
    camera.position.set(11, 8, 13);
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' });
    let activeSection: THREE.Plane[] = [];
    const sectionBounds = new THREE.Box3(new THREE.Vector3(-10, 0, -10), new THREE.Vector3(10, 12, 10));
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.32;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    let sectionWasEnabled = false, shadowsBeforeSection = renderer.shadowMap.enabled;
    mount.appendChild(renderer.domElement);
    renderer.domElement.tabIndex = 0;
    renderer.domElement.setAttribute('aria-label', 'Maquette 3D interactive');
    const controls = new OrbitControls(camera, renderer.domElement);
    controlsRef.current = controls; controls.enabled = navigationModeRef.current === 'orbit';
    controls.target.set(0, 1.5, 0); controls.enableDamping = true; controls.dampingFactor = .14; controls.rotateSpeed = 1.15; controls.panSpeed = .4; controls.enableZoom = false; controls.screenSpacePanning = true;
    controls.minPolarAngle = .001; controls.maxPolarAngle = Math.PI - .001;
    const editRoot = new THREE.Group(); scene.add(editRoot);
    const gizmo = new TransformControls(camera, renderer.domElement); scene.add(gizmo.getHelper());
    gizmo.setSpace('local'); gizmo.setSize(.8); gizmo.showZ = false;
    let draggingEdit = false, dragBase: Markup | null = null, previewMark: Markup | null = null, previewColor = '';
    gizmo.addEventListener('dragging-changed', event => { draggingEdit = !!event.value; dragBase = draftRef.current; controls.enabled = !draggingEdit && navigationModeRef.current === 'orbit'; if (!draggingEdit) previewMark = null; });
    gizmo.addEventListener('objectChange', () => {
      if (!draggingEdit || !dragBase) return;
      const dimensions = (n: number) => Math.round(THREE.MathUtils.clamp(n, 1, 1000) * 10) / 10;
      setDraftMarkup({ ...dragBase, position: editRoot.position.toArray(), widthCm: dimensions(dragBase.widthCm * Math.abs(editRoot.scale.x)), heightCm: dimensions(dragBase.heightCm * Math.abs(editRoot.scale.y)), diameterCm: dragBase.shape === 'round' ? dimensions((dragBase.diameterCm || dragBase.widthCm) * Math.abs(gizmo.axis === 'Y' ? editRoot.scale.y : editRoot.scale.x)) : dragBase.diameterCm });
    });
    // Éclairage de lecture BIM : ambiance claire, lumière du ciel, modelé doux
    // et phare de caméra discret pour conserver les détails dans les locaux fermés.
    const ambient = new THREE.AmbientLight('#ffffff', .42); scene.add(ambient);
    const hemisphere = new THREE.HemisphereLight('#ffffff', '#7d858c', 1.05); scene.add(hemisphere);
    const sun = new THREE.DirectionalLight('#fff9ef', 1.55); const sunTarget = new THREE.Object3D(); scene.add(sun, sunTarget); sun.target = sunTarget;
    sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048); sun.shadow.bias = -.00015; sun.shadow.normalBias = .025;
    const fill = new THREE.DirectionalLight('#b9d8ef', .65); const fillTarget = new THREE.Object3D(); scene.add(fill, fillTarget); fill.target = fillTarget;
    const head = new THREE.DirectionalLight('#ffffff', .3); const headTarget = new THREE.Object3D(); scene.add(head, headTarget); head.target = headTarget;

    const pickables: THREE.Object3D[] = [];
    const miniMap = new MepMiniMap();
    const flowGroup = new THREE.Group(); flowGroup.name = 'Flux MEP'; flowGroup.visible = flowsVisibleRef.current; overlayScene.add(flowGroup); flowGroupRef.current = flowGroup;
    const valveGroup = new THREE.Group(); valveGroup.name = 'Vannes MEP'; valveGroup.visible = valveMarkersVisibleRef.current; overlayScene.add(valveGroup); valveGroupRef.current = valveGroup;
    const flowLineMaterials: LineMaterial[] = [];
    const animatedFlowArrows: AnimatedFlowArrow[] = [];
    const flowVisuals: FlowVisual[] = [];
    const valveVisuals: ValveVisual[] = [];
    let traceRoutes = new Map<string, { start: number; length: number; forward: boolean }>();
    let cameraRoutes = new Map<string, { start: number; length: number; forward: boolean }>();
    let pulseArrivals = new Map<string, number[]>();
    let followBranchKey: string | undefined;
    let pulseEnd = 0, lastPulseRequest = -1;
    const refreshPulseEnd = () => {
      pulseEnd = 0;
      for (const [key, route] of traceRoutes) for (const start of pulseArrivals.get(key) || [route.start]) pulseEnd = Math.max(pulseEnd, start + route.length + 3);
      for (const route of cameraRoutes.values()) pulseEnd = Math.max(pulseEnd, route.start + route.length + 3);
    };
    let pulseTravel = 0, lastPulseTick = performance.now(), lastFollowStep = '';
    let followHistory: string[] = [], lastHistoryNetwork = '';
    const visitedEquipment = new Set<string>();
    let equipmentSlowUntil = 0, equipmentSlowEnd = 0;
    let followRoute: FlowVisual[] = [], followOffset = new THREE.Vector3();
    let traceSeed: string | null = null, traceReverse = false, lastFollowInput = 0, lastClearView = 0;
    let previousLocalSections: FaceSection[] | null = null;
    const stopFollowing = () => { followRoute = []; followingSystemKeyRef.current = null; followPausedRef.current = false; setFollowPaused(false); setFollowingFlow(false); setFollowStep(null); setFollowEnd(null); };
    const traceSelection = (elementKey?: string, reverse = false) => {
      const property = propertiesByIndex.get(selectedIndexRef.current ?? -1);
      traceSeed = elementKey || property?.key || null; traceReverse = reverse;
      traceRoutes = traceSeed && graphStateRef.current ? flowTrace(graphStateRef.current, traceSeed, reverse) : new Map();
      pulseArrivals = traceSeed && graphStateRef.current ? flowPulseArrivals(graphStateRef.current, traceSeed, reverse) : new Map();
      cameraRoutes.clear();
      pulseTravel = 0; lastPulseTick = performance.now();
      refreshPulseEnd();
      if (traceRoutes.size) setFlowsVisible(true);
    };
    const edgeMaterials: THREE.LineBasicMaterial[] = [];
    let waterVolumeCount = 0;
    const applyFlowPresentation = () => {
      const focusedSystem = followingSystemKeyRef.current || selectedSystemKeyRef.current || hoveredSystemKeyRef.current;
      const waterMode = waterViewRef.current;
      let waterBuildBudget = 24;
      for (const visual of flowVisuals) {
        const focused = !focusedSystem || visual.systemKey === focusedSystem;
        const distance = camera.position.distanceTo(visual.center);
        const distanceFade = THREE.MathUtils.clamp(1.05 - distance / Math.max(modelSphere.radius * 2.35, 30), .42, 1);
        const emphasis = focusedSystem ? (focused ? 1 : .4) : .85;
        visual.material.opacity = visual.stateOpacity * emphasis * distanceFade;
        visual.material.linewidth = focusedSystem && focused ? 4.8 : focusedSystem ? 1.8 : 3.4;
        visual.haloMaterial.color.set('#102820');
        visual.haloMaterial.opacity = (focusedSystem && focused ? .48 : focusedSystem ? .12 : .32) * distanceFade;
        visual.haloMaterial.linewidth = visual.material.linewidth + 2.4;
        if (waterMode && visual.objects[0].visible && visual.radii && !visual.water && waterBuildBudget > 0 && waterVolumeCount < 1600 && visual.points.some(point => point.distanceToSquared(visual.points[0]) > .0004)) {
          visual.water = createWaterVolume(visual.points, visual.color, visual.radii.start, visual.radii.end);
          flowGroup.add(visual.water.mesh); waterBuildBudget--; waterVolumeCount++;
        }
        if (visual.water) {
          visual.water.mesh.visible = waterMode && visual.objects[0].visible && visual.current.active.value > .01;
          visual.water.uniforms.opacity.value = emphasis * distanceFade * visual.current.active.value;
          visual.water.uniforms.active.value = visual.current.active.value;
          visual.water.uniforms.direction.value = visual.current.direction.value;
          visual.water.uniforms.origin.value = visual.current.origin.value;
          visual.water.uniforms.spacing.value = visual.current.spacing.value;
        }
        visual.material.needsUpdate = true;
        visual.haloMaterial.needsUpdate = true;
      }
      for (const animation of animatedFlowArrows) {
        const focused = !focusedSystem || animation.systemKey === focusedSystem;
        const material = animation.mesh.material as THREE.MeshBasicMaterial;
        material.opacity = focusedSystem ? (focused ? 1 : .4) : .95;
        for (const child of animation.mesh.children) (child as THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial>).material.opacity = material.opacity * .85;
        material.transparent = true;
        material.needsUpdate = true;
      }
    };
    const collisionGeometries = new Set<THREE.BufferGeometry>();
    let modelSphere = new THREE.Sphere(new THREE.Vector3(0, 2, 0), 8);
    const walkSpeed = 7.2, sprintSpeed = 18, crouchSpeed = 4.2, flySpeed = 15, flySprintSpeed = 26.24672;
    let lastRoomCheck = -Infinity, roomCandidateSince = 0, roomCandidateKey = '', shownRoomKey = '';
    let currentRoom: RoomLocation | null = null;
    setRoomLocation(null);
    let playerFootY = 0, currentEyeHeight = 5.28, verticalVelocity = 0, wheelMoveRemaining = 0;
    const lastSafeFoot = new THREE.Vector3();
    let unsupportedSince: number | null = null;
    let grounded = false, doubleTapSprint = false, lastForwardTap = -Infinity;

    let webFlightMode = false;
    type DoorAnimation = { center: THREE.Vector3; meshes: THREE.Mesh[]; open: boolean; angle: number };
    const doors = new Map<string, DoorAnimation>();
    const frameModel = () => {
      const radius = Math.max(modelSphere.radius, .5);
      const distance = (radius / Math.sin(THREE.MathUtils.degToRad(camera.fov * .5))) * 1.18;
      camera.position.copy(modelSphere.center).add(new THREE.Vector3(1, .78, 1).normalize().multiplyScalar(distance));
      camera.up.set(0, 1, 0); camera.lookAt(modelSphere.center); controls.target.copy(modelSphere.center); controls.update();
    };
    const placeModelLights = () => {
      const distance = Math.max(modelSphere.radius * 2, 30);
      const sunDirection = new THREE.Vector3(-.42, -1, .58).normalize();
      const fillDirection = new THREE.Vector3(.52, .72, -.34).normalize();
      sunTarget.position.copy(modelSphere.center); fillTarget.position.copy(modelSphere.center);
      sun.position.copy(modelSphere.center).addScaledVector(sunDirection, -distance);
      fill.position.copy(modelSphere.center).addScaledVector(fillDirection, -distance);
      const shadowExtent = Math.max(modelSphere.radius * 1.15, 12);
      sun.shadow.camera.left = -shadowExtent; sun.shadow.camera.right = shadowExtent;
      sun.shadow.camera.top = shadowExtent; sun.shadow.camera.bottom = -shadowExtent;
      sun.shadow.camera.near = Math.max(.1, distance * .05); sun.shadow.camera.far = distance * 3;
      sun.shadow.camera.updateProjectionMatrix();
      sunTarget.updateMatrixWorld(); fillTarget.updateMatrixWorld();
    };
    const enterSpectator = () => {
      const configured = viewerPackage?.viewer;
      const eyeHeight = configured?.eyeHeight || 5.28; currentEyeHeight = eyeHeight;
      if (configured?.spawn?.length === 3) { playerFootY = configured.spawn[1]; camera.position.set(configured.spawn[0], playerFootY + eyeHeight, configured.spawn[2]); }
      else {
        const ray = new THREE.Raycaster(new THREE.Vector3(modelSphere.center.x, modelSphere.center.y + modelSphere.radius * 2, modelSphere.center.z), new THREE.Vector3(0, -1, 0));
        const floor = ray.intersectObjects(pickables, false)[0]; playerFootY = floor?.point.y ?? modelSphere.center.y; camera.position.set(modelSphere.center.x, playerFootY + eyeHeight, modelSphere.center.z);
      }
      const yaw = configured?.initialYaw || 0; camera.lookAt(camera.position.clone().add(new THREE.Vector3(Math.cos(yaw), 0, -Math.sin(yaw))));
      lastSafeFoot.set(camera.position.x, playerFootY, camera.position.z); unsupportedSince = null;
      webFlightMode = false; grounded = true; verticalVelocity = 0; wheelMoveRemaining = 0; doubleTapSprint = false; setFlightMode(false);
    };
    const pressed = new Set<string>();
    const setFlight = (enabled: boolean) => {
      webFlightMode = enabled; setFlightMode(enabled); grounded = false; verticalVelocity = 0; doubleTapSprint = false;
    };
    const applyAppearance = () => {
      ambient.color.set('#ffffff');
      ambient.intensity = realisticLightingRef.current ? .42 : 1.15;
      hemisphere.intensity = realisticLightingRef.current ? 1.05 : 0;
      sun.intensity = realisticLightingRef.current ? 1.55 : 0;
      fill.intensity = realisticLightingRef.current ? .65 : 0;
      head.intensity = realisticLightingRef.current ? .3 : 0;
      renderer.toneMappingExposure = highContrastRef.current ? 1.32 : 1.16;
      scene.background = new THREE.Color(highContrastRef.current ? '#dfe8ee' : '#edf2f5');
      edgeMaterials.forEach((material) => { material.visible = highContrastRef.current; });
      applyFlowFocus(flowsVisibleRef.current, true);
    };
    const updateMepState = (nextGraph: MepGraph | null, nextHiddenSystems: Set<string>) => {
      if (!nextGraph) return;
      const phases = flowPhases(nextGraph);
      const closedValves = new Set(nextGraph.valves.filter(valve => valve.isEnabledAsValve && valve.isClosed).map(valve => valve.elementKey));
      const elements = new Map(nextGraph.elements.map((element) => [element.key, element]));
      for (const visual of flowVisuals) {
        const path = elements.get(visual.elementKey)?.paths?.[visual.pathOrdinal];
        const state = typeof path?.flowState === 'string' ? path.flowState.toLowerCase() : path?.flowState;
        const supplied = state === 2 || state === 'supplied';
        const isolated = state === 1 || state === 'isolated';
        visual.stateOpacity = supplied && path?.hasCirculation ? .94 : isolated ? .58 : .72;
        visual.targetActive = canAnimate(path) && !closedValves.has(visual.elementKey) ? 1 : 0;
        visual.current.direction.value = path?.flowForward === false ? -1 : 1;
        const phase = phases.get(flowPathKey(visual.elementKey, visual.pathOrdinal));
        if (phase) { visual.current.origin.value = phase.origin; visual.current.spacing.value = phase.spacing; }
        const visible = !nextHiddenSystems.has(visual.systemKey) && (!stream || stream.complete(propertiesByKey.get(visual.elementKey)?.index ?? -1));
        visual.objects.forEach((object) => { object.visible = visible; });
      }
      applyFlowPresentation();
      for (const animation of animatedFlowArrows) {
        const path = elements.get(animation.elementKey)?.paths?.[animation.pathOrdinal];
        animation.enabled = !nextHiddenSystems.has(animation.systemKey) && (!stream || stream.complete(propertiesByKey.get(animation.elementKey)?.index ?? -1)) &&
          canAnimate(path) && !closedValves.has(animation.elementKey);
        animation.mesh.visible = animation.enabled;
        animation.forward = path?.flowForward !== false;
        const phase = phases.get(flowPathKey(animation.elementKey, animation.pathOrdinal));
        animation.flowStart = phase?.start ?? 0; animation.spacing = phase?.spacing ?? 6;
      }
      const valves = new Map(nextGraph.valves.map((valve) => [valve.elementKey, valve]));
      for (const visual of valveVisuals) {
        const valve = valves.get(visual.elementKey);
        const material = visual.marker.material as THREE.MeshBasicMaterial;
        if (valve) {
          material.color.set(valve.isClosed ? '#ef544a' : '#20d08b');
          (visual.handle.material as THREE.MeshBasicMaterial).color.copy(material.color);
          visual.targetAngle = valve.isClosed ? Math.PI / 2 : 0;
        }
        visual.available = !nextHiddenSystems.has(visual.systemKey) && (!stream || stream.complete(propertiesByKey.get(visual.elementKey)?.index ?? -1));
      }
      // Re-reading visibility/scenario must not cancel a pulse at the next joint.
      // Rebuild its admissible route from the current calculation, preserving time.
      if (traceRoutes.size && traceSeed) {
        traceRoutes = flowTrace(nextGraph, traceSeed, traceReverse);
        pulseArrivals = flowPulseArrivals(nextGraph, traceSeed, traceReverse);
        if (followRoute.length) {
          cameraRoutes = flowFollowChain(nextGraph, traceSeed, traceReverse, followBranchKey).routes;
          followRoute = [...cameraRoutes.keys()].map(key => flowVisuals.find(visual => flowPathKey(visual.elementKey, visual.pathOrdinal) === key)).filter((visual): visual is FlowVisual => !!visual);
        }
        refreshPulseEnd();
      }
    };
    viewActionsRef.current = {
      traceSelectedFlow: () => { flowPulseRequestRef.current++; },
      stopFollowing,
      chooseFollowBranch: key => viewActionsRef.current?.followSelectedFlow(traceSeed || undefined, traceReverse, key),
      followSelectedFlow: (elementKey, reverse = false, branchKey) => {
        visitedEquipment.clear(); equipmentSlowUntil = 0; equipmentSlowEnd = 0; setEquipmentPause(null);
        setFollowEnd(null);
        traceSelection(elementKey, reverse);
        followPausedRef.current = false; setFollowPaused(false);
        lastFollowStep = ''; followHistory = []; lastHistoryNetwork = ''; setFollowStep(null);
        if (!traceSeed || !graphStateRef.current) return;
        const chain = flowFollowChain(graphStateRef.current, traceSeed, reverse, branchKey);
        followBranchKey = branchKey;
        cameraRoutes = chain.routes;
        refreshPulseEnd();
        followRoute = [...chain.routes.keys()].map(key => flowVisuals.find(visual => flowPathKey(visual.elementKey, visual.pathOrdinal) === key)).filter((visual): visual is FlowVisual => !!visual);
        if (!followRoute.length) return;
        const branches = chain.branches.map(key => {
          const candidate = flowVisuals.find(visual => flowPathKey(visual.elementKey, visual.pathOrdinal) === key);
          const element = graphStateRef.current?.elements.find(element => key.startsWith(element.key + '\0'));
          return { key, name: candidate ? `${graphStateRef.current?.systems.find(system => system.key === candidate.systemKey)?.name || candidate.systemKey} · ${element?.name || candidate.elementKey} #${element?.elementId || candidate.elementKey} · branche ${candidate.pathOrdinal + 1}` : element?.name || key };
        });
        setFollowBranches(branches);
        const first = followRoute[0], route = cameraRoutes.get(flowPathKey(first.elementKey, first.pathOrdinal))!;
        const start = first.points[route.forward ? 0 : first.points.length - 1];
        controls.target.copy(start);
        followOffset.copy(camera.position).sub(start);
        if (followOffset.length() > 18) followOffset.setLength(18);
        if (followOffset.length() < 4) followOffset.set(4, 3, 4);
        camera.position.copy(start).add(followOffset); controls.update();
        changeNavigationMode('orbit'); setPanelCollapsed(true); setFollowingFlow(true);
      },
      toggleLocalCut: () => {
        if (previousLocalSections) { setSection(previousLocalSections); previousLocalSections = null; setLocalCut(false); return; }
        const system = selectedSystemKeyRef.current;
        if (!system) return;
        const bounds = new THREE.Box3();
        flowVisuals.filter(visual => visual.systemKey === system).forEach(visual => visual.points.forEach(point => bounds.expandByPoint(point)));
        if (bounds.isEmpty()) return;
        bounds.expandByScalar(3);
        previousLocalSections = sectionRef.current;
        const cuts: FaceSection[] = [];
        for (const axis of ['x', 'y', 'z'] as const) for (const sign of [1, -1]) {
          const normal = new THREE.Vector3(); normal[axis] = sign;
          const anchor = bounds.getCenter(new THREE.Vector3()); anchor[axis] = sign === 1 ? bounds.min[axis] : bounds.max[axis];
          cuts.push({ id: crypto.randomUUID(), name: 'Coupe locale du réseau', enabled: true, normal: normal.toArray(), anchor: anchor.toArray(), offset: 0, inverted: false });
        }
        setSection(cuts); setLocalCut(true);
      },
      captureMarkup: async (mark) => {
        if (disposed) throw new Error('La vue 3D n’est plus disponible.');
        const width = 960, height = 720;
        const center = new THREE.Vector3(...mark.position);
        const normal = new THREE.Vector3(...mark.normal).normalize();
        const viewDirection = normal.add(new THREE.Vector3(.25, .18, .25)).normalize();
        const shotCamera = camera.clone();
        shotCamera.aspect = width / height;
        shotCamera.position.copy(center).addScaledVector(viewDirection, 12);
        shotCamera.up.set(0, 1, 0);
        shotCamera.lookAt(center);
        shotCamera.updateProjectionMatrix();
        const target = new THREE.WebGLRenderTarget(width, height);
        const pixels = new Uint8Array(width * height * 4);
        const priorTarget = renderer.getRenderTarget(), priorAutoClear = renderer.autoClear;
        try {
          renderer.setRenderTarget(target); renderer.autoClear = true;
          renderer.render(scene, shotCamera);
          renderer.readRenderTargetPixels(target, 0, 0, width, height, pixels);
        } finally {
          renderer.setRenderTarget(priorTarget); renderer.autoClear = priorAutoClear; target.dispose();
        }
        const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
        const context = canvas.getContext('2d');
        if (!context) throw new Error('Capture BCF indisponible dans ce navigateur.');
        const image = context.createImageData(width, height);
        for (let y = 0; y < height; y++) image.data.set(pixels.subarray((height - y - 1) * width * 4, (height - y) * width * 4), y * width * 4);
        context.putImageData(image, 0, 0);
        context.strokeStyle = '#ef4444'; context.lineWidth = 5; context.beginPath(); context.arc(width / 2, height / 2, 20, 0, Math.PI * 2); context.stroke();
        context.fillStyle = '#ef4444'; context.beginPath(); context.arc(width / 2, height / 2, 4, 0, Math.PI * 2); context.fill();
        const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(new Error('Capture BCF impossible.')), 'image/png'));
        return {
          image: new Uint8Array(await blob.arrayBuffer()),
          position: shotCamera.position.toArray() as [number, number, number],
          direction: shotCamera.getWorldDirection(new THREE.Vector3()).toArray() as [number, number, number],
          up: new THREE.Vector3(0, 1, 0).applyQuaternion(shotCamera.quaternion).toArray() as [number, number, number],
          fieldOfView: shotCamera.fov,
          aspectRatio: shotCamera.aspect,
        };
      },
      frameMarkup: (mark) => {
        const center = new THREE.Vector3(...mark.position);
        const direction = new THREE.Vector3(...mark.normal).normalize().add(new THREE.Vector3(.3, .3, .3)).normalize();
        camera.position.copy(center).addScaledVector(direction, Math.max(5, mark.widthCm / 30.48 * 4, mark.heightCm / 30.48 * 4));
        controls.target.copy(center); controls.update();
      },
      frameSelected: () => {
        if (selection.bounds.isEmpty()) return;
        
        const sphere = selection.bounds.getBoundingSphere(new THREE.Sphere());
        const direction = camera.position.clone().sub(sphere.center).normalize();
        camera.position.copy(sphere.center).addScaledVector(direction, Math.max(2, sphere.radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2)) * 1.3));
        controls.target.copy(sphere.center); controls.update();
      },
      isometric: () => {  frameModel(); },

      enterSpectator,
      setInput: (code, active) => { if (active) pressed.add(code); else pressed.delete(code); },
      toggleFlight: () => setFlight(!webFlightMode),
      applyAppearance,
      updateMepState,
      applyFlowPresentation,
    };
    const addDemo = () => {
      const materials = ['#28a979', '#3f7fbe'].map((color) => new THREE.MeshPhongMaterial({ color, shininess: 18, specular: new THREE.Color(.1, .1, .1) }));
      for (const material of materials) modelMaterialStatesRef.current.push({ material, opacity: material.opacity, transparent: material.transparent, depthWrite: material.depthWrite });
      const pipe = (r: number, length: number, material: THREE.Material, p: [number, number, number], rotation: [number, number, number]) => {
        const mesh = new THREE.Mesh(new THREE.CylinderGeometry(r, r, length, 32), material); mesh.position.set(...p); mesh.rotation.set(...rotation); scene.add(mesh); pickables.push(mesh);
      };
      pipe(.22, 13, materials[0], [0, 2.7, 0], [0, 0, Math.PI / 2]); pipe(.19, 13, materials[1], [0, 1.6, -1.45], [0, 0, Math.PI / 2]);
      [-4, 0, 4].forEach((x) => { pipe(.14, 4.4, materials[0], [x, 4.75, 0], [0, 0, 0]); pipe(.13, 4.4, materials[1], [x + .55, 3.7, -1.45], [0, 0, 0]); });
      const valveMaterial = new THREE.MeshPhongMaterial({ color: '#f59e0b', shininess: 18, specular: new THREE.Color(.1, .1, .1) });
      modelMaterialStatesRef.current.push({ material: valveMaterial, opacity: valveMaterial.opacity, transparent: valveMaterial.transparent, depthWrite: valveMaterial.depthWrite });
      const valve = new THREE.Mesh(new THREE.IcosahedronGeometry(.48, 2), valveMaterial); valve.position.set(0, 2.7, 0); scene.add(valve); pickables.push(valve); placeModelLights(); applyAppearance(); frameModel();
    };
    if (viewerPackage) {
      const resetGeometry = () => {
        cutoutsRef.current?.dispose(); cutoutsRef.current = null;
      };
      const finishOpening = () => { if (!disposed && geometryReady && !cutsPending) sceneReady = true; };
      const cutBusy = (busy: boolean) => { if (disposed) return; cutsPending = busy; setCutting(busy); finishOpening(); };
      const updateCuts = () => {
        if (!geometryReady) return;
        const marks = cutoutMarkupsRef.current.filter(mark => !mark.needsReview && (!stream || stream.complete(propertiesByKey.get(mark.elementKey)?.index ?? -1)));
        if (!cutoutsRef.current && marks.some(mark => mark.kind === 'reservation')) cutoutsRef.current = new AsyncWallCutouts(pickables.filter((mesh): mesh is THREE.Mesh => mesh instanceof THREE.Mesh), propertiesByIndex, cutBusy, setError, (mesh, geometry) => { if (buildingVisibility) buildingVisibility.replace(mesh, geometry); else { geometry.disposeBoundsTree(); geometry.dispose(); } }, [...propertiesByIndex.keys()].filter(id => !stream || stream.complete(id)));
        cutoutsRef.current?.update(marks);
      };
      updateCutoutsRef.current = updateCuts;
      const refreshGeometry = () => {
        if (disposed) return;
        setLoadedZones(stream?.count || 0);
        if (stream?.loading || geometryReady) return;
        if (geometryTimer !== undefined) return;
        geometryTimer = window.setTimeout(() => {
          geometryTimer = undefined;
          if (disposed) return;
          if (stream?.loading || geometryReady) return;
          geometryReady = true;
          const meshes = pickables.filter((mesh): mesh is THREE.Mesh => mesh instanceof THREE.Mesh);
          if (buildingVisibility) buildingVisibility.setMeshes(meshes);
          else buildingVisibility = new BuildingVisibility(meshes, propertiesByIndex);
          try { updateCuts(); }
          catch (caught) { setError(caught instanceof Error ? caught.message : 'Découpe impossible sur ce mur.'); }
          finishOpening();
          updateMepState(graphStateRef.current, hiddenSystemsRef.current);
        }, 200);
      };
      const addModel = async (model: THREE.Group) => {
        if (disposed) { disposeModel(model); return; }
        const recordedMaterials = new Set<THREE.Material>();
        const pluginMaterials = new Map<string, THREE.Material>();
        const originalPluginMaterials = new Set<THREE.Material>();
        const adaptMaterial = (material: THREE.Material, mesh: THREE.Mesh) => {
          if (!(material instanceof THREE.MeshStandardMaterial)) return material;
          const role = visualRoleOf(mesh, material);
          const key = `${material.uuid}:${role}`;
          const cached = pluginMaterials.get(key); if (cached) return cached;
          const adapted = new THREE.MeshPhongMaterial({
            name: material.name,
            color: cleanViewerColor(material.color, role),
            vertexColors: Boolean(mesh.geometry.getAttribute('color')),
            shininess: role === 'mep' ? 38 : 14,
            specular: new THREE.Color(role === 'mep' ? .18 : .08, role === 'mep' ? .18 : .08, role === 'mep' ? .18 : .08),
            transparent: material.transparent,
            opacity: material.opacity,
            depthWrite: material.depthWrite,
            side: material.side,
            alphaTest: material.alphaTest,
          });
          pluginMaterials.set(key, adapted); originalPluginMaterials.add(material); return adapted;
        };
        const modelMeshes: THREE.Mesh[] = [];
        model.traverse(child => { if (child instanceof THREE.Mesh) modelMeshes.push(child); });
        for (const child of modelMeshes) {
          repairLegacyNormals(child.geometry);
          child.material = Array.isArray(child.material) ? child.material.map((material) => adaptMaterial(material, child)) : adaptMaterial(child.material, child);
          const positionCount = child.geometry.getAttribute('position').count;
          child.castShadow = positionCount < 320000; child.receiveShadow = true;
          const materials = Array.isArray(child.material) ? child.material : [child.material];
          materials.forEach((material) => {
            if (!recordedMaterials.has(material)) {
              recordedMaterials.add(material);
              modelMaterialStatesRef.current.push({ material, opacity: material.opacity, transparent: material.transparent, depthWrite: material.depthWrite });
            }
          });
          const doorKey = typeof child.userData.doorKey === 'string' ? child.userData.doorKey : null;
          if (doorKey) {
            const metadata = viewerPackage.viewer?.doors.find((door) => door.key === doorKey);
            if (metadata) {
              const current = doors.get(doorKey) || { center: new THREE.Vector3(...metadata.center as [number, number, number]), meshes: [], open: false, angle: 0 };
              current.meshes.push(child); doors.set(doorKey, current);
              const hinge = new THREE.Vector3(...metadata.hinge as [number, number, number]);
              child.geometry.translate(-hinge.x, -hinge.y, -hinge.z); child.position.copy(hinge);
            }
          }
          const edgeRole = visualRoleOf(child, materials[0]);
          const architectural = edgeRole === 'floor' || edgeRole === 'wall' || edgeRole === 'base' || edgeRole === 'ceiling';
          const prepared = await preparation!.prepare(child.geometry, positionCount < (architectural ? 3000000 : 1500000) ? (architectural ? 22 : 30) : null);
          if (disposed) {
            for (const geometry of [prepared.geometry, prepared.proxy, prepared.edges]) { geometry?.disposeBoundsTree(); geometry?.dispose(); }
            return;
          }
          child.geometry.dispose(); child.geometry = prepared.geometry;
          collisionGeometries.add(child.geometry); pickables.push(child);
          if (prepared.proxy) buildingVisibility!.replace(child, prepared.proxy);
          // Les arêtes architecturales marquent les jonctions sans transformer les
          // réseaux denses en traits noirs. Le seuil plus élevé supprime le bruit.
          if (prepared.edges) {
            const edgeMaterial = new THREE.LineBasicMaterial({ color: '#405158', transparent: true, opacity: architectural ? .36 : .24, depthTest: true, depthWrite: false, toneMapped: false });
            edgeMaterial.visible = highContrastRef.current; edgeMaterials.push(edgeMaterial);
            const edges = new THREE.LineSegments(prepared.edges, edgeMaterial);
            edges.renderOrder = 2; edges.raycast = () => {}; child.add(edges);
          }
        }
        originalPluginMaterials.forEach((original) => original.dispose());
        scene.add(model);
        model.updateMatrixWorld(true);
        applyFlowFocus(flowsVisibleRef.current, true);
        if (!viewerPackage.manifest.tiles) {
          sectionBounds.setFromObject(model); setSectionExtent(Math.max(1, sectionBounds.getSize(new THREE.Vector3()).length() * .3048));
          modelSphere = sectionBounds.getBoundingSphere(new THREE.Sphere());
          setupView(); refreshGeometry();
        }
        applyAppearance();
      };
      const setupView = () => {
        if (!Number.isFinite(modelSphere.radius) || modelSphere.radius <= 0) modelSphere.radius = 8;
        placeModelLights();
        camera.near = Math.max(.01, modelSphere.radius / 4000); camera.far = Math.max(1000, modelSphere.radius * 80); camera.updateProjectionMatrix(); frameModel();
        if (navigationModeRef.current === 'maquette') enterSpectator();
      };
      if (viewerPackage.manifest.tiles) {
        const bounds = new THREE.Box3(); viewerPackage.manifest.tiles.forEach(tile => bounds.union(tileBounds(tile)));
        sectionBounds.copy(bounds); setSectionExtent(Math.max(1, bounds.getSize(new THREE.Vector3()).length() * .3048));
        modelSphere = bounds.getBoundingSphere(new THREE.Sphere()); setupView();
        stream = new ModelStream(viewerPackage.manifest.tiles, tokenRef.current!, resolved!.publication.revision, addModel, model => {
          resetGeometry();
          const removed = new Set<THREE.Object3D>(); const materials = new Set<THREE.Material>();
          model.traverse(object => {
            removed.add(object);
            const mesh = object as THREE.Mesh;
            if (mesh.geometry) collisionGeometries.delete(mesh.geometry);
            if (mesh.material) (Array.isArray(mesh.material) ? mesh.material : [mesh.material]).forEach(material => materials.add(material));
          });
          for (let i = pickables.length - 1; i >= 0; i--) if (removed.has(pickables[i])) pickables.splice(i, 1);
          for (let i = edgeMaterials.length - 1; i >= 0; i--) if (materials.has(edgeMaterials[i])) edgeMaterials.splice(i, 1);
          modelMaterialStatesRef.current = modelMaterialStatesRef.current.filter(state => !materials.has(state.material));
          for (const [key, door] of doors) { door.meshes = door.meshes.filter(mesh => !removed.has(mesh)); if (!door.meshes.length) doors.delete(key); }
          scene.remove(model);
          buildingVisibility?.setMeshes(pickables.filter((mesh): mesh is THREE.Mesh => mesh instanceof THREE.Mesh));
        }, refreshGeometry, setError, useR2Storage(resolved!.publication.id, resolved!.storageBackend));
        streamRef.current = stream;
      } else new GLTFLoader().load(viewerPackage.modelUrl, ({ scene: model }) => { void addModel(model).catch(caught => { if (!disposed) setError(caught instanceof Error ? caught.message : 'Préparation impossible'); }); }, undefined, () => { if (!disposed) setError('La géométrie 3D ne peut pas être ouverte.'); });
      let renderedPathCount = 0, fallbackPathCount = 0;
      const connectorsByIndex = new Map(initialGraph?.connectors.map((connector) => [connector.index, connector]) || []);
      if (initialGraph) for (const element of initialGraph.elements) {
        const declaredPaths = (element.paths || [])
          .map((path) => ({ path, points: (path.points || []).map(toWebPoint).filter((point): point is THREE.Vector3 => point !== null) }))
          .filter((entry) => entry.points.length >= 2);
        const fallbackPoints = declaredPaths.length === 0
          ? element.connectorIndices.map((index) => toWebPoint(connectorsByIndex.get(index)?.position)).filter((point): point is THREE.Vector3 => point !== null)
          : [];
        const paths = declaredPaths.length > 0
          ? declaredPaths
          : fallbackPoints.length >= 2
            ? [{ path: { ...element.paths?.[0], flowState: element.flowState, hasCirculation: false, flowForward: true }, points: fallbackPoints }]
            : [];
        if (declaredPaths.length === 0 && paths.length > 0) fallbackPathCount += paths.length;
        for (const [pathOrdinal, { path, points }] of paths.entries()) {
          const state = typeof path.flowState === 'string' ? path.flowState.toLowerCase() : path.flowState;
          const supplied = state === 2 || state === 'supplied';
          const isolated = state === 1 || state === 'isolated';
          const normalizedSystemKey = element.systemKey.trim().toLowerCase();
          const normalizedSystemName = element.systemName.trim().toLowerCase();
          const systemIndex = initialGraph.systems.findIndex((item) =>
            item.key.trim().toLowerCase() === normalizedSystemKey ||
            (!!normalizedSystemName && item.name.trim().toLowerCase() === normalizedSystemName));
          const system = systemIndex >= 0 ? initialGraph.systems[systemIndex] : null;
          // La couleur identifie toujours le système, quel que soit son état.
          // L'état reste lisible par l'animation et une légère variation d'intensité.
          const fallbackIndex = element.systemKey.split('').reduce((total, character) => total + character.charCodeAt(0), 0);
          const color = system ? colorOf(system, systemIndex) : colorOf({}, fallbackIndex);
          const opacity = supplied && path.hasCirculation ? .98 : isolated ? .68 : .82;
          const positions = points.flatMap((point) => [point.x, point.y, point.z]);
          const haloGeometry = new LineGeometry(); haloGeometry.setPositions(positions);
          const haloMaterial = new LineMaterial({ color, linewidth: 7, transparent: true, opacity: .12, depthTest: true, depthWrite: false, toneMapped: false });
          haloMaterial.resolution.set(Math.max(mount.clientWidth, 1), Math.max(mount.clientHeight, 1)); flowLineMaterials.push(haloMaterial);
          const halo = new Line2(haloGeometry, haloMaterial); halo.computeLineDistances(); halo.renderOrder = 3000; halo.frustumCulled = false; halo.raycast = () => {}; flowGroup.add(halo);
          const geometry = new LineGeometry(); geometry.setPositions(positions);
          attachFlowDistances(geometry, points);
          const current = createFlowMaterial(color);
          current.active.value = canAnimate(path) ? 1 : 0;
          current.direction.value = path.flowForward === false ? -1 : 1;
          const material = current.material;
          material.resolution.set(Math.max(mount.clientWidth, 1), Math.max(mount.clientHeight, 1)); flowLineMaterials.push(material);
          const line = new Line2(geometry, material); line.computeLineDistances(); line.renderOrder = 3001; line.frustumCulled = false; line.raycast = () => {}; flowGroup.add(line); renderedPathCount++;
          const center = points.reduce((sum, point) => sum.add(point), new THREE.Vector3()).multiplyScalar(1 / points.length);
          const radii = waterPathRadii(path as { startConnector: number; endConnector: number }, connectorsByIndex, propertiesByKey.get(element.key));
          flowVisuals.push({ objects: [halo, line], material, haloMaterial, elementKey: element.key, pathOrdinal, systemKey: element.systemKey, center, stateOpacity: opacity, targetActive: current.active.value, current, points, color, radii });
          const cumulative = [0];
          for (let index = 1; index < points.length; index++) cumulative.push(cumulative[index - 1] + points[index].distanceTo(points[index - 1]));
          const length = cumulative[cumulative.length - 1];
          if (length > .02 && animatedFlowArrows.length < 1200) {
            const count = Math.max(1, Math.min(256, Math.ceil(length / 4.5)));
            for (let index = 0; index < count && animatedFlowArrows.length < 1200; index++) {
              const arrow = new THREE.Mesh(createFlowChevronGeometry(), new THREE.MeshBasicMaterial({ color: new THREE.Color(color).lerp(new THREE.Color('#f1ffff'), .65), side: THREE.DoubleSide, transparent: true, opacity: .95, depthTest: true, depthWrite: false, toneMapped: false }));
              const outline = new THREE.Mesh(arrow.geometry, new THREE.MeshBasicMaterial({ color: new THREE.Color(color).multiplyScalar(.3), side: THREE.DoubleSide, transparent: true, opacity: .8, depthTest: true, depthWrite: false, toneMapped: false }));
              outline.scale.setScalar(1.32); outline.renderOrder = 3001.5; arrow.add(outline);
              arrow.visible = canAnimate(path);
              arrow.renderOrder = 3002; arrow.frustumCulled = false; flowGroup.add(arrow);
              animatedFlowArrows.push({ mesh: arrow, elementKey: element.key, pathOrdinal, systemKey: element.systemKey, points, cumulative, length, slot: index, flowStart: 0, spacing: 6, enabled: canAnimate(path), forward: path.flowForward !== false, waterRadius: radii ? Math.min(radii.start, radii.end) : null });
            }
          }
        }
      }
      statsTimer = window.setTimeout(() => {
        if (!disposed) setFlowRenderStats({ paths: renderedPathCount, arrows: animatedFlowArrows.length, fallbackPaths: fallbackPathCount });
      }, 0);
      if (initialGraph) for (const valve of initialGraph.valves.filter((item) => item.isEnabledAsValve)) {
        const element = initialGraph.elements.find((item) => item.key === valve.elementKey);
        const points = (element?.paths?.[0]?.points || []).map(toWebPoint).filter((point): point is THREE.Vector3 => point !== null);
        if (points.length < 2) continue;
        const start = points[0], end = points[points.length - 1];
        const axis = end.clone().sub(start).normalize();
        if (axis.lengthSq() < .5) continue;
        // Symbolic quarter-turn handle: never rotate the exported valve body.
        const assembly = new THREE.Group(); assembly.position.copy(start).lerp(end, .5);
        assembly.quaternion.setFromUnitVectors(new THREE.Vector3(1, 0, 0), axis);
        const marker = new THREE.Mesh(new THREE.TorusGeometry(.34, .045, 8, 32), new THREE.MeshBasicMaterial({ color: valve.isClosed ? '#ef544a' : '#20d08b', depthTest: false, toneMapped: false }));
        const handle = new THREE.Mesh(new THREE.CapsuleGeometry(.065, .55, 4, 8), new THREE.MeshBasicMaterial({ color: valve.isClosed ? '#ef544a' : '#20d08b', depthTest: false, toneMapped: false }));
        handle.geometry.rotateZ(-Math.PI / 2); handle.position.z = .12;
        handle.rotation.z = valve.isClosed ? Math.PI / 2 : 0;
        marker.renderOrder = 22; handle.renderOrder = 23;
        assembly.add(marker, handle); valveGroup.add(assembly);
        valveVisuals.push({ marker, handle, targetAngle: handle.rotation.z, available: true, elementKey: valve.elementKey, systemKey: element!.systemKey });
      }
      updateMepState(initialGraph, initialHiddenSystems);
      applyFlowPresentation();
    } else addDemo();

    const raycaster = new THREE.Raycaster(); raycaster.firstHitOnly = true; const pointer = new THREE.Vector2();
    const collisionRaycaster = new THREE.Raycaster(); collisionRaycaster.firstHitOnly = true;
    let pointerStart: { x: number; y: number; dragged: boolean; time: number } | null = null; let looking = false; let suppressContextMenu = false;
    let previousFrameTime = performance.now();
    let pickedAnchor: Pick<ContextMenuState, 'position' | 'normal' | 'depthCm'> | null = null;
    let lastHoverPick = 0;
    const clearFlowHover = () => {
      if (!hoveredSystemKeyRef.current) return;
      hoveredSystemKeyRef.current = null; applyFlowPresentation();
    };
    const hoverFlowAt = (event: PointerEvent) => {
      if (!flowsVisibleRef.current || pointerStart || draggingEdit || gizmo.axis || draftRef.current || measurePickRef.current.picking) { clearFlowHover(); return; }
      const now = performance.now();
      if (now - lastHoverPick < 70) return;
      lastHoverPick = now;
      const rect = renderer.domElement.getBoundingClientRect();
      const planes = sectionPlanes(sectionRef.current);
      raycaster.firstHitOnly = planes.length === 0;
      pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
      raycaster.setFromCamera(pointer, camera);
      let system: string | null = null;
      for (const hit of raycaster.intersectObjects(pickables.filter(isObjectVisible), false)) {
        if (hit.faceIndex == null || !sectionContains(planes, hit.point)) continue;
        const geometry = (hit.object as THREE.Mesh).geometry;
        const attribute = geometry.getAttribute('_ELEMENT') || geometry.getAttribute('_element');
        if (!attribute) continue;
        const vertex = geometry.index ? geometry.index.getX(hit.faceIndex * 3) : hit.faceIndex * 3;
        const raw = propertiesByIndex.get(Math.round(attribute.getX(vertex)));
        const property = raw?.selectionTargetKey ? propertiesByKey.get(raw.selectionTargetKey) : raw;
        system = graphStateRef.current?.elements.find(element => element.key === property?.key)?.systemKey || null;
        break;
      }
      if (system !== hoveredSystemKeyRef.current) { hoveredSystemKeyRef.current = system; applyFlowPresentation(); }
    };
    const selectAt = (event: { clientX: number; clientY: number }, contextOnly = false, worldRay?: THREE.Ray) => {
      pickedAnchor = null;
      const selectable = pickables.filter(isObjectVisible);
      if (!selectable.length) return null;
      const rect = renderer.domElement.getBoundingClientRect();
      activeSection = sectionPlanes(sectionRef.current);
      raycaster.firstHitOnly = activeSection.length === 0;
      if (worldRay) raycaster.ray.copy(worldRay);
      else { pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1); raycaster.setFromCamera(pointer, camera); }
      for (const hit of raycaster.intersectObjects(selectable, false)) {
        if (!sectionContains(activeSection, hit.point)) continue;
        if (hit.faceIndex == null) continue;
        if (!contextOnly && measurePickRef.current.picking) {
          const rawPoint = hit.point.toArray() as MeasurePoint;
          const point = measurePickRef.current.start ? measureEnd(measurePickRef.current.start, rawPoint, measureModeRef.current, new THREE.Vector3(1, 0, 0).applyQuaternion(camera.quaternion)) : rawPoint;
          const start = measurePickRef.current.start;
          if (!start) { measurePickRef.current.start = point; setMeasureStart(point); }
          else {
            if (new THREE.Vector3(...start).distanceTo(new THREE.Vector3(...point)) < .0001) { setError('Choisissez un second point différent du premier.'); return null; }

            setMeasures(current => current.length < 20 ? [...current, { id: crypto.randomUUID(), start, end: point, mode: measureModeRef.current }] : current);
            cancelMeasure();
          }
          return null;
        }
        if (!contextOnly && pickingSectionRef.current && hit.face) {
          const normal = hit.face.normal.clone().applyNormalMatrix(new THREE.Matrix3().getNormalMatrix(hit.object.matrixWorld)).normalize();
          if (normal.dot(raycaster.ray.direction) > 0) normal.negate();
          if (sectionRef.current.length >= maximumSections) { setPickingSection(false); return null; }
          const serial = ++sectionSerial.current;
          const cut = sectionFromFace('section-' + serial, 'Coupe ' + serial, hit.point, normal);
          setSection(current => current.length < maximumSections ? [...current, cut] : current);
          setSelectedSection(cut.id); setPickingSection(false); pickingSectionRef.current = false;
          setSectionOpen(true);
          return null;
        }
        const geometry = (hit.object as THREE.Mesh).geometry; const attribute = geometry.getAttribute('_ELEMENT') || geometry.getAttribute('_element');
        const indices = geometry.index; const vertex = indices ? indices.getX(hit.faceIndex * 3) : hit.faceIndex * 3;
        if (!attribute) continue;
        const rawIndex = Math.round(attribute.getX(vertex));
        const rawProperty = propertiesByIndex.get(rawIndex);
        // Le plugin exporte SelectionTargetKey pour le calorifuge : son volume
        // reste visible, mais toute inspection vise directement son hôte.
        const property = rawProperty?.selectionTargetKey ? propertiesByKey.get(rawProperty.selectionTargetKey) : rawProperty;
        if (!property) continue;
        const normal = hit.face?.normal.clone().applyNormalMatrix(new THREE.Matrix3().getNormalMatrix(hit.object.matrixWorld)).normalize() || new THREE.Vector3(0, 0, 1);
        if (normal.dot(raycaster.ray.direction) > 0) normal.negate();
        pickedAnchor = { position: hit.point.toArray(), normal: normal.toArray() };
        if (contextOnly) {
          if (isReservationHost(property) && (!stream || stream.complete(property.index))) pickedAnchor.depthCm = hostDepthCm(
            pickables.filter((mesh): mesh is THREE.Mesh => mesh instanceof THREE.Mesh), property.index, hit.point, normal,
            mesh => cutoutsRef.current?.originalGeometry(mesh) || mesh.geometry);
          return property.index;
        }
        if (pickingDimensionRef.current && draftRef.current) {
          const draft = draftRef.current;
          if (!/^(sols?|floors?|murs?|walls?|ost_floors|ost_walls)$/i.test(property.category.trim())) { setMarkupError('Choisissez une face de sol ou de mur.'); return null; }
          if ((draft.dimensions?.length || 0) >= 3) return null;
          const dimension: ReservationDimension = { id: crypto.randomUUID(), elementKey: property.key, elementName: property.name, point: hit.point.toArray(), normal: normal.toArray() };
          if (dimensionDistance(draft, dimension) < 0) dimension.normal = normal.negate().toArray();
          try {
            moveToDimension(draft, dimension, dimensionDistance(draft, dimension));
            setDraftMarkup({ ...draft, dimensions: [...(draft.dimensions || []), dimension] });
            setPickingDimension(false); pickingDimensionRef.current = false; setMarkupError(null);
          } catch (caught) { setMarkupError((caught as Error).message); }
          return null;
        }
        if (relocatingRef.current) {
          setDraftMarkup(anchorMarkup({ ...relocatingRef.current, dimensions: [], ...pickedAnchor }, property, resolved!.publication.revision));
          setRelocating(null); setEditMode('translate');
        }
        setActiveMarkup(null);
        selectedIndexRef.current = property.index;
        flowPulseRequestRef.current++;
        setSelectedIndex(property.index);
        setInspectionHistory((current) => [property.index, ...current.filter((item) => item !== property.index)].slice(0, 8));
        return property.index;
      }
      if (contextOnly) return null;
      if (!measurePickRef.current.picking && !pickingDimensionRef.current) setActiveMarkup(null);
      selectedIndexRef.current = null; setSelectedIndex(null);
      return null;
    };
    const onPointerDown = (event: PointerEvent) => {
      if (followRoute.length) lastFollowInput = performance.now();
      renderer.domElement.focus({ preventScroll: true });
      if (!sceneReady || gizmo.axis || draggingEdit) return;
      pointerStart = { x: event.clientX, y: event.clientY, dragged: false, time: performance.now() };
      suppressContextMenu = false;
      if (pickingSectionRef.current) return;
      const measurePivot = measurePickRef.current.picking && measurePickRef.current.start ? new THREE.Vector3(...measurePickRef.current.start) : null;
      const pivot = followRoute.length ? controls.target.clone() : measurePickRef.current.picking ? measurePivot : selectedIndexRef.current !== null ? selectedPivot : null;
      if (!pickingDimensionRef.current && event.button === 0 && (navigationModeRef.current === 'orbit' || measurePivot) && pivot && !event.shiftKey && !event.ctrlKey && !event.metaKey) {
        if (navigationModeRef.current === 'maquette') controls.target.copy(camera.position).addScaledVector(camera.getWorldDirection(new THREE.Vector3()), Math.max(1, camera.position.distanceTo(pivot)));
        orbitPointer = { id: event.pointerId, x: event.clientX, y: event.clientY, pivot: pivot.clone() };
        renderer.domElement.setPointerCapture(event.pointerId);
        setContextMenu(null); event.preventDefault(); event.stopImmediatePropagation(); return;
      }
      if (event.button === 0) setContextMenu(null);
      if (navigationModeRef.current === 'maquette' && event.button === 0) { looking = true; renderer.domElement.setPointerCapture(event.pointerId); }
    };
    const onPointerMove = (event: PointerEvent) => {
      if (!sceneReady) return;
      hoverFlowAt(event);
      if (pointerStart && Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y) >= 5) pointerStart.dragged = true;
      if (pointerStart && !pointerStart.dragged) { event.stopImmediatePropagation(); return; }
      if (orbitPointer && event.pointerId === orbitPointer.id) {
        const scale = 2 * Math.PI * 1.15 / Math.max(1, renderer.domElement.clientHeight);
        orbitSelection(camera, controls.target, orbitPointer.pivot, (event.clientX - orbitPointer.x) * scale, (event.clientY - orbitPointer.y) * scale);
        orbitPointer.x = event.clientX; orbitPointer.y = event.clientY;
        event.preventDefault(); event.stopImmediatePropagation(); return;
      }
      if (draggingEdit || !looking || navigationModeRef.current === 'orbit') return;
      const euler = new THREE.Euler().setFromQuaternion(camera.quaternion, 'YXZ');
      euler.y += event.movementX * .00245; euler.x = THREE.MathUtils.clamp(euler.x + event.movementY * .00225, -1.48, 1.48); camera.quaternion.setFromEuler(euler);
    };
    const onPointerUp = (event: PointerEvent) => {
      if (!sceneReady) return;
      if (draggingEdit || gizmo.axis || !pointerStart) return;
      const dragged = pointerStart.dragged || Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y) >= 5;
      const simpleClick = !dragged && performance.now() - pointerStart.time < 600;
      looking = false; orbitPointer = null; pointerStart = null;
      if (renderer.domElement.hasPointerCapture(event.pointerId)) renderer.domElement.releasePointerCapture(event.pointerId);
      if (event.button === 2 && dragged) suppressContextMenu = true;
      if (simpleClick && event.button === 0 && !followRoute.length) selectAt(event);
    };

    const onContextMenu = (event: MouseEvent) => {
      if (!sceneReady) { event.preventDefault(); return; }
      event.preventDefault();
      if (measurePickRef.current.picking || pickingDimensionRef.current || pickingSectionRef.current) return;
      if (suppressContextMenu) { suppressContextMenu = false; return; }
      const index = selectAt(event, true);
      if (index === null || !pickedAnchor) { setContextMenu(null); return; }
      const rect = renderer.domElement.getBoundingClientRect();
      setContextMenu({ x: Math.min(event.clientX - rect.left, Math.max(8, rect.width - 236)), y: Math.min(event.clientY - rect.top, Math.max(8, rect.height - 330)), index, ...pickedAnchor });
    };
    const interactDoor = () => {
      let nearest: DoorAnimation | null = null; let nearestDistance = 8;
      doors.forEach(door => { const distance = door.center.distanceTo(camera.position); if (distance < nearestDistance) { nearest = door; nearestDistance = distance; } });
      if (nearest) (nearest as DoorAnimation).open = !(nearest as DoorAnimation).open;
      else setError(viewerPackage?.viewer ? 'Aucune porte assez proche.' : 'Republiez cette maquette avec la nouvelle version du plugin pour rendre les portes interactives.');
    };
    const gamepad = new MepGamepad();
    const walkGamepad = new MepWalkGamepad();
    const menuRepeat = new GamepadMenuRepeat(), editRepeat = new GamepadMenuRepeat();
    let padName: string | null = null, menuMode = false, menuActivationPending = false;
    let menuWasPicking = false, menuRequestedEdit = false;
    let managedUiFocus = false;
    let globalMenuFocus = false;
    const setDevice = (device: 'keyboard' | 'gamepad') => {
      if (inputDeviceRef.current !== device) { inputDeviceRef.current = device; setInputDevice(device); }
    };
    const leavePadMenu = () => {
      menuMode = false; setGamepadMenu(false); gamepad.read(readBrowserPads(), false);
      managedUiFocus = false;
      const focused = document.activeElement;
      if (focused instanceof HTMLElement && mount.closest('main')?.contains(focused)) focused.blur();
    };
    const keyboardDevice = (event?: Event) => {
      if (event instanceof KeyboardEvent && !event.isTrusted) return;
      managedUiFocus = false;
      setDevice('keyboard'); gamepad.read(readBrowserPads(), false);
      if (menuMode) { menuMode = false; setGamepadMenu(false); }
    };
    const pointerDevice = (event: PointerEvent) => {
      if (event.type === 'pointermove' && !event.movementX && !event.movementY) return;
      keyboardDevice();
    };
    const focusPadButton = (direction: number) => {
      const root = mount.closest('main');
      if (root) { managedUiFocus = true; focusGamepadControl(globalMenuFocus && !gamepadFieldRef.current ? root : gamepadScope(root), direction); }
    };
    const centerPoint = () => { const rect = renderer.domElement.getBoundingClientRect(); return { clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 }; };
    const framePadSelected = () => {
      if (selection.bounds.isEmpty()) return;
      if (navigationModeRef.current === 'maquette') setFlight(true);
      viewActionsRef.current?.frameSelected();
    };
    const clearPadSelection = () => {
      if (pickingDimensionRef.current) { setPickingDimension(false); pickingDimensionRef.current = false; return; }
      if (measurePickRef.current.picking) { cancelMeasure(); return; }
      if (pickingSectionRef.current) { setPickingSection(false); pickingSectionRef.current = false; return; }
      if (relocatingRef.current) { setRelocating(null); return; }
      cancelMeasure(); setPickingDimension(false); pickingDimensionRef.current = false;
      setPickingSection(false); pickingSectionRef.current = false;
      setActiveMarkup(null); setRelocating(null); setDraftMarkup(null); setContextMenu(null);
      selectedIndexRef.current = null; setSelectedIndex(null);
    };
    const resetInput = () => { orbitPointer = null; pointerStart = null; looking = false; pressed.clear(); wheelMoveRemaining = 0; gamepad.read(readBrowserPads(), false); };
    const onVisibilityChange = () => { if (document.hidden) resetInput(); };
    const onKeyDown = (event: KeyboardEvent) => {
      if (!event.isTrusted) return;
      if (['Escape', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyZ', 'KeyQ', 'KeyF'].includes(event.code)) stopFollowing();
      if (!sceneReady) return;
      if (event.code === 'Escape' && measurePickRef.current.picking) { cancelMeasure(); event.preventDefault(); return; }
      if (event.code === 'Escape' && pickingDimensionRef.current) { setPickingDimension(false); pickingDimensionRef.current = false; event.preventDefault(); return; }
      if (event.code === 'Escape' && pickingSectionRef.current) { setPickingSection(false); event.preventDefault(); return; }
      if (event.code === 'Escape') { setActiveMarkup(null); setRelocating(null); setDraftMarkup(null); setContextMenu(null); selectedIndexRef.current = null; setSelectedIndex(null); }
      if (draftOpenRef.current || (event.target instanceof HTMLElement && event.target.closest('input,textarea,select,button,[contenteditable=true],.section-controls,.lots-panel,.measure-controls'))) { pressed.clear(); return; }
      if (navigationModeRef.current === 'maquette' && !event.ctrlKey && !event.altKey && !event.metaKey && event.code === 'KeyM') {
        if (!event.repeat) setMiniMapVisible(value => !value);
        event.preventDefault(); return;
      }
      if (event.ctrlKey || event.altKey || event.metaKey) return;
      if (navigationModeRef.current !== 'maquette') { if (event.code === 'KeyF') viewActionsRef.current?.frameSelected(); return; }
      if (!event.repeat && event.code === 'KeyF') {
        setFlight(!webFlightMode); event.preventDefault();
      }
      if (!event.repeat && event.code === 'KeyP') { setFlowsVisible((value) => !value); event.preventDefault(); }
      if (!event.repeat && event.code === 'KeyR') { enterSpectator(); event.preventDefault(); }
      if (!event.repeat && event.code === 'Space' && !webFlightMode && grounded) { verticalVelocity = 11.2; grounded = false; event.preventDefault(); }
      if (!event.repeat && ['KeyW', 'KeyZ', 'ArrowUp'].includes(event.code)) {
        const now = performance.now() / 1000; doubleTapSprint = now - lastForwardTap <= .34; lastForwardTap = doubleTapSprint ? -Infinity : now;
      }
      if (!event.repeat && event.code === 'KeyE') { interactDoor(); event.preventDefault(); }
      pressed.add(event.code);
      if (['KeyW', 'KeyZ', 'KeyA', 'KeyQ', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.code)) event.preventDefault();
    };
    const onKeyUp = (event: KeyboardEvent) => { pressed.delete(event.code); if (['KeyW', 'KeyZ', 'ArrowUp'].includes(event.code)) doubleTapSprint = false; };
    const onWheel = (event: WheelEvent) => {
      if (!sceneReady) { event.preventDefault(); return; }
      if (followRoute.length && !event.ctrlKey) {
        event.preventDefault(); event.stopImmediatePropagation(); lastFollowInput = performance.now();
        const offset = camera.position.clone().sub(controls.target);
        offset.setLength(THREE.MathUtils.clamp(offset.length() * Math.exp(Math.sign(event.deltaY) * .12), 2, 80));
        camera.position.copy(controls.target).add(offset); controls.update(); return;
      }
      if (event.ctrlKey && selectedSectionRef.current) {
        event.preventDefault(); event.stopImmediatePropagation();
        setSection(current => current.map(cut => cut.id === selectedSectionRef.current
          ? { ...cut, offset: THREE.MathUtils.clamp(cut.offset - Math.sign(event.deltaY) * .1 / .3048, -sectionBounds.getSize(new THREE.Vector3()).length(), sectionBounds.getSize(new THREE.Vector3()).length()) } : cut));
        return;
      }
      if (navigationModeRef.current === 'orbit') {
        event.preventDefault(); event.stopImmediatePropagation();
        if (draggingEdit) return;
        const rect = renderer.domElement.getBoundingClientRect();
        pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1); raycaster.setFromCamera(pointer, camera);
        raycaster.firstHitOnly = activeSection.length === 0;
        const hit = raycaster.intersectObjects(pickables.filter(isObjectVisible), false).find(item => sectionContains(activeSection, item.point));
        const distance = hit?.distance ?? Math.max(camera.position.distanceTo(controls.target), modelSphere.radius * .1);
        const step = raycaster.ray.direction.clone().multiplyScalar(zoomStep(distance, event.deltaY, event.deltaMode));
        camera.position.add(step);
        // La cible suit le travelling, même lorsqu'un élément est sélectionné :
        // la caméra peut ainsi dépasser l'objet sans rester prisonnière de son pivot.
        controls.target.add(step);
        controls.update();
        return;
      }
      event.preventDefault(); wheelMoveRemaining = THREE.MathUtils.clamp(wheelMoveRemaining - Math.sign(event.deltaY) * 3.2, -12, 12);
    };
    const onDoubleClick = (event: MouseEvent) => { if (draftRef.current || !sceneReady || measureStateRef.current.open) return; const id = selectAt(event); if (id !== null) { selection.set(pickables.filter((m): m is THREE.Mesh => m instanceof THREE.Mesh), new Set([id])); viewActionsRef.current?.frameSelected(); } };
    renderer.domElement.addEventListener('pointerleave', clearFlowHover);
    renderer.domElement.addEventListener('dblclick', onDoubleClick);
    renderer.domElement.addEventListener('pointerdown', onPointerDown, true); renderer.domElement.addEventListener('pointermove', onPointerMove, true); renderer.domElement.addEventListener('pointerup', onPointerUp); renderer.domElement.addEventListener('contextmenu', onContextMenu); renderer.domElement.addEventListener('wheel', onWheel, { passive: false, capture: true });
    const labelLayer = markupLabelsRef.current;
    labelLayer?.addEventListener('wheel', onWheel, { passive: false, capture: true });
    window.addEventListener('keydown', onKeyDown, { passive: false }); window.addEventListener('keyup', onKeyUp);
    window.addEventListener('keydown', keyboardDevice, true);
    window.addEventListener('pointerdown', pointerDevice, true);
    window.addEventListener('pointermove', pointerDevice, true);
    window.addEventListener('wheel', keyboardDevice, { passive: true });
    document.addEventListener('visibilitychange', onVisibilityChange);
    window.addEventListener('blur', resetInput); renderer.domElement.addEventListener('pointercancel', resetInput);
    const resize = () => {
      if (renderer.xr.isPresenting) return;
      const width = mount.clientWidth, height = mount.clientHeight;
      camera.aspect = width / Math.max(height, 1); camera.updateProjectionMatrix(); renderer.setSize(width, height, false);
      flowLineMaterials.forEach((material) => material.resolution.set(Math.max(width, 1), Math.max(height, 1)));
    };
    const observer = new ResizeObserver(resize); observer.observe(mount); resize();
    let vrDesktopState: { footY: number; eyeHeight: number; flight: boolean; target: THREE.Vector3 } | null = null;
    const vrVisit = new MepVrVisit({
      renderer, scene, camera,
      prepare: () => {
        vrDesktopState = { footY: playerFootY, eyeHeight: currentEyeHeight, flight: webFlightMode, target: controls.target.clone() };
        stopFollowing(); resetInput();
        cancelMeasure(); setPickingSection(false); pickingSectionRef.current = false;
        setPickingDimension(false); pickingDimensionRef.current = false;
        setContextMenu(null); setActiveMarkup(null);
        if (navigationModeRef.current !== 'maquette' || webFlightMode) enterSpectator();
        controls.enabled = false;
        return { footY: playerFootY };
      },
      restore: () => {
        if (vrDesktopState) {
          playerFootY = vrDesktopState.footY; currentEyeHeight = vrDesktopState.eyeHeight;
          webFlightMode = vrDesktopState.flight; setFlightMode(webFlightMode); controls.target.copy(vrDesktopState.target);
          vrDesktopState = null;
        }
        controls.enabled = navigationModeRef.current === 'orbit'; resetInput(); resize();
      },
      objects: () => pickables.filter(isObjectVisible),
      ready: point => !stream || stream.readyAt(point),
      pick: ray => {
        const index = selectAt(centerPoint(), false, ray);
        selection.set(pickables.filter((m): m is THREE.Mesh => m instanceof THREE.Mesh), new Set(index === null ? [] : [index]));
        const property = index === null ? null : propertiesByIndex.get(index);
        return property ? `${property.name} · ${property.category || ''} · #${property.elementId}` : 'Aucun objet sélectionné';
      },
      state: setVrActive,
    });
    vrVisitRef.current = vrVisit;
    let presentationFrame = 0, lastFlowUpdate = 0;
    let lastLabelVisibility = -Infinity;
    let qualityFrames = 0, qualityStart = performance.now();
    const animate = () => {
      if (draftOpenRef.current) { pressed.clear(); wheelMoveRemaining = 0; }
      const now = performance.now(); const delta = Math.min((now - previousFrameTime) / 1000, .05); previousFrameTime = now;
      if (vrVisit.active) {
        stream?.update(now); vrVisit.step(delta); selection.sync();
        for (const visual of flowVisuals) {
          visual.current.time.value = now / 1000;
          visual.current.active.value = THREE.MathUtils.damp(visual.current.active.value, visual.targetActive, 8, delta);
          if (visual.water) visual.water.uniforms.time.value = now / 1000;
        }
        camera.getWorldPosition(head.position);
        headTarget.position.copy(head.position).addScaledVector(camera.getWorldDirection(new THREE.Vector3()), 10); headTarget.updateMatrixWorld();
        renderer.autoClear = false; renderer.clear(); renderer.render(scene, camera);
        if (flowGroup.visible || valveGroup.visible) {
          renderer.clearDepth();
          if (buildingVisibility && !waterViewRef.current && modelOpacityRef.current > .3) renderer.render(buildingVisibility.scene, camera);
          renderer.render(overlayScene, camera);
        }
        vrVisit.renderOverlay(); return;
      }
      const pulseElapsed = Math.max(0, (now - lastPulseTick) / 1000); lastPulseTick = now;
      if (!document.hidden && ++qualityFrames >= 120) {
        const frameMs = (now - qualityStart) / qualityFrames;
        const ratio = renderer.getPixelRatio();
        if (frameMs > 28 && ratio > .8) {
          renderer.setPixelRatio(Math.max(.8, ratio * .8));
          renderer.shadowMap.enabled = false;
          resize();
        }
        qualityFrames = 0; qualityStart = now;
      }
      stream?.update(now);
      // React commits the clicked tool between frames. Return to aiming after
      // selecting a measure, section face, relocation or dimension target.
      if (menuActivationPending) {
        menuActivationPending = false;
        if ((!menuWasPicking && (measurePickRef.current.picking || pickingSectionRef.current || pickingDimensionRef.current || relocatingRef.current)) || menuRequestedEdit) leavePadMenu();
      }
      const focused = document.activeElement;
      const editingText = !managedUiFocus && !gamepadFieldRef.current && focused instanceof HTMLElement && !!focused.closest('input,textarea,select,[contenteditable]:not([contenteditable=false])');
      let pad = gamepad.read(readBrowserPads(), sceneReady && !document.hidden && document.hasFocus() && !editingText && !draggingEdit && !looking && !orbitPointer);
      if (pad.name !== padName) {
        padName = pad.name; setConnectedPad(padName);
        if (!padName) { setDevice('keyboard'); if (gamepadFieldRef.current) closeGamepadField(); if (menuMode) leavePadMenu(); }
      }
      if (pad.active) {
        setDevice('gamepad'); pressed.clear(); wheelMoveRemaining = 0; doubleTapSprint = false;
        if (gamepadFieldRef.current) { menuMode = true; managedUiFocus = true; setGamepadMenu(true); }
      }
      // Menu and Back remain edge-triggered; holding a button cannot repeat edits.
      if (pad.tapped[xbox.menu]) {
        if (gamepadFieldRef.current) closeGamepadField();
        menuMode = !menuMode; setGamepadMenu(menuMode);
        if (menuMode) { globalMenuFocus = false; focusPadButton(1); } else leavePadMenu();
      }
      const maquettePad = navigationModeRef.current === 'maquette';
      const menuBack = menuMode && pad.tapped[xbox.b];
      const cancelAiming = !menuMode && pad.tapped[maquettePad ? maquetteXbox.cancel : xbox.b];
      if (menuBack) {
        const root = mount.closest('main'), scope = root ? gamepadScope(root) : null;
        if (gamepadFieldRef.current) closeGamepadField();
        else if (scope?.matches('.lots-panel,.mep-impact-panel,.markup-card')) {
          const close = gamepadControls(scope).find(element => /^(Fermer|Retour)/.test(element.getAttribute('aria-label') || element.textContent || ''));
          close?.click();
        } else { setContextMenu(null); leavePadMenu(); }
      }
      else if (cancelAiming) clearPadSelection();
      if (menuMode) {
        if (pad.tapped[xbox.view] && !gamepadFieldRef.current) { globalMenuFocus = !globalMenuFocus; focusPadButton(1); }
        const root = mount.closest('main'), scope = root ? globalMenuFocus && !gamepadFieldRef.current ? root : gamepadScope(root) : null;
        if (scope && !gamepadControls(scope).includes(document.activeElement as HTMLElement)) focusPadButton(1);
        for (const key of menuRepeat.read(pad, now, true)) {
          if (scope?.matches('.gamepad-field-editor')) moveGamepadKey(scope, Number(key === xbox.right) - Number(key === xbox.left), Number(key === xbox.down) - Number(key === xbox.up));
          else if (key === xbox.up || key === xbox.down) focusPadButton(key === xbox.up ? -1 : 1);
          else if (!(document.activeElement instanceof HTMLElement) || !adjustGamepadControl(document.activeElement, key === xbox.left ? -1 : 1, !!pad.held[xbox.lb])) focusPadButton(key === xbox.left ? -1 : 1);
        }
        if (scope?.matches('.gamepad-field-editor')) {
          if (pad.tapped[xbox.lb]) focusPadButton(-1);
          if (pad.tapped[xbox.rb]) focusPadButton(1);
          if (pad.tapped[xbox.x]) scope.querySelector<HTMLElement>('[data-editor-action=delete]')?.click();
          if (pad.tapped[xbox.y]) scope.querySelector<HTMLElement>('[data-editor-action=space]')?.click();
        }
        if (pad.tapped[xbox.a] && document.activeElement instanceof HTMLElement && root?.contains(document.activeElement)) {
          const control = document.activeElement;
          managedUiFocus = true;
          if ((control instanceof HTMLInputElement && !['checkbox', 'radio', 'range', 'button', 'submit'].includes(control.type)) || control instanceof HTMLTextAreaElement) {
            if (!control.readOnly) { gamepadFieldRef.current = control; setGamepadField(control); }
          } else if (control instanceof HTMLSelectElement) adjustGamepadControl(control, 1);
          else {
            menuWasPicking = !!(measurePickRef.current.picking || pickingSectionRef.current || pickingDimensionRef.current || relocatingRef.current);
            menuRequestedEdit = !!control.dataset.gamepadEdit;
            menuActivationPending = true; control.click();
          }
        }
      } else if (pad.active) {
        menuRepeat.read(pad, now, false);
        if (pad.tapped[maquettePad ? maquetteXbox.select : xbox.a] && !(maquettePad && pad.held[xbox.lb]) && (!draftOpenRef.current || pickingDimensionRef.current)) selectAt(centerPoint());
        if (!draftOpenRef.current) {
        if (pad.tapped[xbox.view]) { changeNavigationMode(navigationModeRef.current === 'orbit' ? 'maquette' : 'orbit'); resetInput(); }
        if (pad.tapped[xbox.rb] && !measurePickRef.current.picking && !pickingSectionRef.current && !draftOpenRef.current) {
          const point = centerPoint(); const index = selectAt(point, true);
          if (index !== null && pickedAnchor) {
            const rect = renderer.domElement.getBoundingClientRect();
            setContextMenu({ x: Math.max(8, Math.min(rect.width / 2, rect.width - 236)), y: Math.max(8, Math.min(rect.height / 2, rect.height - 330)), index, ...pickedAnchor });
            menuMode = true; globalMenuFocus = false; setGamepadMenu(true);
            // Focus after React commits the menu, without creating an uncancelled timer.
          }
        }
        if (pad.tapped[xbox.right] || (pad.tapped[xbox.x] && navigationModeRef.current === 'orbit')) framePadSelected();
        if (pad.tapped[xbox.down]) setFlowsVisible(value => !value);
        if (navigationModeRef.current === 'maquette') {
          if (pad.tapped[xbox.y]) setFlight(!webFlightMode);
          if (pad.tapped[maquetteXbox.jump] && !webFlightMode && grounded) { verticalVelocity = 11.2; grounded = false; }
          if (pad.tapped[maquetteXbox.interact]) interactDoor();
          if (pad.tapped[xbox.up]) setMiniMapVisible(value => !value);
          if (pad.tapped[xbox.left]) enterSpectator();
        } else if (pad.tapped[xbox.y] || pad.tapped[xbox.left]) frameModel();
        }
      }
      const editDraft = !menuMode && !!draftRef.current && !pickingDimensionRef.current && !cancelAiming && !pad.tapped[xbox.menu];
      const editSteps = editRepeat.read(pad, now, editDraft);
      if (editDraft && draftRef.current) {
        if (pad.tapped[xbox.x] && draftRef.current.kind === 'reservation') {
          editModeRef.current = editModeRef.current === 'translate' ? 'scale' : 'translate'; setEditMode(editModeRef.current);
        }
        const precision = pad.held[xbox.lt] ? .1 : 1;
        const x = pad.moveX * 40 * delta * precision + (Number(editSteps.includes(xbox.right)) - Number(editSteps.includes(xbox.left))) * precision;
        const y = -pad.moveY * 40 * delta * precision + (Number(editSteps.includes(xbox.up)) - Number(editSteps.includes(xbox.down))) * precision;
        if (x || y) { const next = editMarkupOnFace(draftRef.current, x, y, editModeRef.current); draftRef.current = next; setDraftMarkup(next); }
        if (pad.lookX || pad.lookY) orbitSelection(camera, controls.target, new THREE.Vector3(...draftRef.current.position), pad.lookX * 1.5 * delta, pad.lookY * 1.2 * delta);
      }
      const menuVertical = menuMode && pad.held[xbox.a] && focused instanceof HTMLElement ? Number(focused.dataset.gamepadVertical || 0) : 0;
      if (menuMode || (draftOpenRef.current && !pickingDimensionRef.current) || pad.tapped[xbox.menu] || menuBack || cancelAiming) {
        pad = { ...pad, moveX: 0, moveY: 0, lookX: 0, lookY: 0, vertical: menuVertical, held: [] };
        pressed.clear(); wheelMoveRemaining = 0;
      }
      // The selected cut uses the same precision offset as Ctrl + wheel.
      if (!menuMode && !draftOpenRef.current && pad.held[xbox.lb] && pad.vertical && selectedSectionRef.current) {
        const sectionStep = pad.vertical * .6 * delta / .3048;
        const sectionLimit = sectionBounds.getSize(new THREE.Vector3()).length();
        setSection(current => current.map(cut => cut.id === selectedSectionRef.current
          ? { ...cut, offset: THREE.MathUtils.clamp(cut.offset + sectionStep, -sectionLimit, sectionLimit) } : cut));
        pad = { ...pad, vertical: 0 };
      }
      const walkingPad = walkGamepad.read(pad, navigationModeRef.current === 'maquette' && !menuMode, webFlightMode);
      const padCrouch = walkingPad.crouch;
      if (navigationModeRef.current === 'maquette' && (pad.lookX || pad.lookY)) {
        const precision = pad.held[xbox.lt] ? .35 : 1;
        const euler = new THREE.Euler().setFromQuaternion(camera.quaternion, 'YXZ');
        euler.y -= pad.lookX * 2.2 * delta * precision;
        euler.x = THREE.MathUtils.clamp(euler.x - pad.lookY * 1.7 * delta * precision, -1.48, 1.48);
        camera.quaternion.setFromEuler(euler);
      }
      if (navigationModeRef.current === 'orbit' && (pad.moveX || pad.moveY || pad.lookX || pad.lookY || pad.vertical)) {
        const precision = pad.held[xbox.lb] ? .3 : 1;
        if (pad.lookX || pad.lookY) orbitSelection(camera, controls.target, selectedPivot || controls.target, pad.lookX * 1.8 * delta * precision, pad.lookY * 1.5 * delta * precision);
        const distance = Math.max(.5, camera.position.distanceTo(controls.target));
        const right = new THREE.Vector3(1, 0, 0).applyQuaternion(camera.quaternion);
        const up = new THREE.Vector3(0, 1, 0).applyQuaternion(camera.quaternion);
        const move = right.multiplyScalar(pad.moveX).addScaledVector(up, -pad.moveY).multiplyScalar(distance * .65 * delta * precision);
        move.addScaledVector(camera.getWorldDirection(new THREE.Vector3()), pad.vertical * distance * delta * precision);
        camera.position.add(move); controls.target.add(move);
      }
      controls.enabled = sceneReady && !draggingEdit && !pickingSectionRef.current && navigationModeRef.current === 'orbit';
      if (!sceneReady) { pressed.clear(); wheelMoveRemaining = 0; return; }
      activeSection = sectionPlanes(sectionRef.current);
      renderer.clippingPlanes = activeSection;
      renderer.domElement.style.cursor = pickingSectionRef.current ? 'crosshair' : '';
      // Global planes do not clip shadow passes; removed roofs must not cast ghost shadows.
      if ((activeSection.length > 0) !== sectionWasEnabled) {
        if (activeSection.length > 0) { shadowsBeforeSection = renderer.shadowMap.enabled; renderer.shadowMap.enabled = false; }
        else { renderer.shadowMap.enabled = shadowsBeforeSection; renderer.shadowMap.needsUpdate = true; }
        sectionWasEnabled = activeSection.length > 0;
      }
      raycaster.firstHitOnly = activeSection.length === 0;
      controls.enableRotate = true;
      controls.enableDamping = !measurePickRef.current.picking;
      if (lastSelection !== selectedIndexRef.current) {
        stopFollowing();
        lastSelection = selectedIndexRef.current;
        const property = lastSelection === null ? null : propertiesByIndex.get(lastSelection);
        const ids = new Set<number>(lastSelection === null ? [] : [lastSelection]);
        if (property) for (const p of propertiesByIndex.values()) if (p.selectionTargetKey === property.key) ids.add(p.index);
        selection.set(pickables.filter((m): m is THREE.Mesh => m instanceof THREE.Mesh), ids);
        selectedPivot = lastSelection === null || selection.bounds.isEmpty() ? null : selection.bounds.getCenter(new THREE.Vector3());
        traceSelection();
        lastPulseRequest = flowPulseRequestRef.current;

      }
      selection.sync();
      if (lastPulseRequest !== flowPulseRequestRef.current) { lastPulseRequest = flowPulseRequestRef.current; traceSelection(); }
      const draftColor = draftRef.current ? lotDetails(reservationLot(draftRef.current), lotSettingsRef.current).color : '';
      if (!draggingEdit && (previewMark !== draftRef.current || previewColor !== draftColor)) {
        previewColor = draftColor;
        previewMark = draftRef.current; disposeMarkups(editRoot); editRoot.scale.set(1, 1, 1);
        if (previewMark) {
          const preview = markupObject(previewMark, { color: draftColor, showDimensions: true }); editRoot.position.copy(preview.position); editRoot.quaternion.copy(preview.quaternion);
          preview.position.set(0, 0, 0); preview.quaternion.identity(); preview.children.forEach(child => { child.visible = true; }); editRoot.add(preview);
          if (!pickingDimensionRef.current) gizmo.attach(editRoot);
        } else gizmo.detach();
      }
      if (pickingDimensionRef.current) gizmo.detach();
      else if (previewMark && !gizmo.object) gizmo.attach(editRoot);
      if (gizmo.mode !== editModeRef.current) gizmo.setMode(editModeRef.current);
      if (navigationModeRef.current === 'orbit') controls.update();
      else if (!draftOpenRef.current && (!stream || webFlightMode || stream.readyAt(camera.position))) {
        let forwardInput = -pad.moveY + Number(pressed.has('KeyW') || pressed.has('KeyZ') || pressed.has('ArrowUp')) - Number(pressed.has('KeyS') || pressed.has('ArrowDown'));
        if (Math.abs(wheelMoveRemaining) > .000001) { const wheelStep = THREE.MathUtils.clamp(wheelMoveRemaining, -walkSpeed * delta, walkSpeed * delta); forwardInput = THREE.MathUtils.clamp(forwardInput + wheelStep / Math.max(.000001, walkSpeed * delta), -1, 1); wheelMoveRemaining -= wheelStep; }
        const rightInput = pad.moveX + Number(pressed.has('KeyD') || pressed.has('ArrowRight')) - Number(pressed.has('KeyA') || pressed.has('KeyQ') || pressed.has('ArrowLeft'));
        const shift = pressed.has('ShiftLeft') || pressed.has('ShiftRight');
        const verticalInput = webFlightMode ? THREE.MathUtils.clamp(walkingPad.vertical + pad.vertical * Number(menuMode) + Number(pressed.has('Space')) - Number(shift), -1, 1) : 0;
        if (forwardInput || rightInput || verticalInput) {
          // Comme dans le repère du plugin, avancer/reculer ne modifie jamais
          // l'altimétrie, même lorsque la caméra regarde vers le haut ou le bas.
          // La hauteur en vol reste exclusivement pilotée par Espace / Maj ou A / B.
          const forward = new THREE.Vector3(); camera.getWorldDirection(forward); forward.y = 0; if (forward.lengthSq() < .001) forward.set(0, 0, -1); else forward.normalize();
          const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize(); const movement = forward.multiplyScalar(forwardInput).add(right.multiplyScalar(rightInput)); if (movement.lengthSq() > 1) movement.normalize();
          const crouching = !webFlightMode && (padCrouch || pressed.has('ControlLeft') || pressed.has('ControlRight'));
          const sprint = walkingPad.sprint || (!webFlightMode && shift) || doubleTapSprint; const speed = crouching ? crouchSpeed : webFlightMode ? (sprint ? flySprintSpeed : flySpeed) : (sprint ? sprintSpeed : walkSpeed);
          movement.y += verticalInput;
          if (movement.lengthSq() > 1) movement.normalize(); const step = movement.multiplyScalar(speed * delta);
          if (webFlightMode) camera.position.add(step);
          else {
            const canMove = (candidateStep: THREE.Vector3) => {
              if (stream && !stream.readyAt(camera.position.clone().add(candidateStep))) return false;
              const distance = candidateStep.length(); if (distance < .000001) return true;
              const direction = candidateStep.clone().normalize();
              const rightOffset = new THREE.Vector3(-direction.z, 0, direction.x).multiplyScalar(.56);
              const foot = camera.position.y - currentEyeHeight;
              const origins = [
                new THREE.Vector3(camera.position.x, foot + .3, camera.position.z),
                new THREE.Vector3(camera.position.x, foot + currentEyeHeight * .52, camera.position.z),
                new THREE.Vector3(camera.position.x, foot + currentEyeHeight - .18, camera.position.z),
              ];
              return origins.every((origin) => [-1, 0, 1].every((side) => {
                collisionRaycaster.set(origin.clone().addScaledVector(rightOffset, side), direction);
                collisionRaycaster.near = 0; collisionRaycaster.far = distance + .2;
                return collisionRaycaster.intersectObjects(pickables, false).length === 0;
              }));
            };
            const horizontalStep = new THREE.Vector3(step.x, 0, step.z);
            const segments = Math.max(1, Math.ceil(horizontalStep.length() / .34));
            const slice = horizontalStep.multiplyScalar(1 / segments);
            for (let segment = 0; segment < segments; segment++) {
              if (canMove(slice)) camera.position.add(slice);
              else {
                const xStep = new THREE.Vector3(slice.x, 0, 0); const zStep = new THREE.Vector3(0, 0, slice.z);
                if (canMove(xStep)) camera.position.add(xStep);
                if (canMove(zStep)) camera.position.add(zStep);
              }
            }
          }
        }
        if (webFlightMode) playerFootY = camera.position.y - currentEyeHeight;
        else {
          const crouching = padCrouch || pressed.has('ControlLeft') || pressed.has('ControlRight'); const targetEyeHeight = crouching ? 2.72 : 5.28; currentEyeHeight = THREE.MathUtils.damp(currentEyeHeight, targetEyeHeight, 10, delta);
          if (!grounded) { verticalVelocity -= 28 * delta; playerFootY += verticalVelocity * delta; }
          collisionRaycaster.set(new THREE.Vector3(camera.position.x, playerFootY + .86, camera.position.z), new THREE.Vector3(0, -1, 0));
          collisionRaycaster.near = 0; collisionRaycaster.far = 3.26;
          const ground = collisionRaycaster.intersectObjects(pickables, false).find((hit) => {
            if (!hit.face) return false; const normal = hit.face.normal.clone().transformDirection(hit.object.matrixWorld); return normal.y > .45;
          });
          if (ground && verticalVelocity <= 0 && playerFootY <= ground.point.y + .12) { playerFootY = ground.point.y + .04; verticalVelocity = 0; grounded = true; }
          else grounded = false;
          if (grounded && ground) {
            lastSafeFoot.set(camera.position.x, playerFootY, camera.position.z); unsupportedSince = null;
          } else {
            unsupportedSince ??= now;
            if (playerFootY < lastSafeFoot.y - 12 || now - unsupportedSince > 2500) {
              camera.position.copy(lastSafeFoot); playerFootY = lastSafeFoot.y;
              verticalVelocity = 0; grounded = false; unsupportedSince = null; wheelMoveRemaining = 0;
            }
          }
          camera.position.y = playerFootY + currentEyeHeight;
        }
      }
      if (!flowGroup.visible && followRoute.length) stopFollowing();
      if (flowGroup.visible) {
        const previousPulseTravel = pulseTravel;
        const equipmentSlow = equipmentStopsRef.current && (now < equipmentSlowUntil || pulseTravel < equipmentSlowEnd);
        if (!equipmentSlow && equipmentSlowUntil > 0) { equipmentSlowUntil = 0; equipmentSlowEnd = 0; setEquipmentPause(null); }
        pulseTravel += pulseElapsed * 8 * (followRoute.length ? followPausedRef.current ? 0 : followSpeedRef.current * (equipmentSlow ? .3 : 1) : 1);
        if (followRoute.length && !followPausedRef.current) {
          if (equipmentStopsRef.current) for (const candidate of followRoute) {
            const route = cameraRoutes.get(flowPathKey(candidate.elementKey, candidate.pathOrdinal));
            if (!route || visitedEquipment.has(candidate.elementKey) || candidate.targetActive <= 0 || route.start > pulseTravel || route.start + route.length < previousPulseTravel) continue;
            const element = graphStateRef.current?.elements.find(element => element.key === candidate.elementKey);
            const kind = equipmentStopKind(element);
            if (route && kind && !visitedEquipment.has(candidate.elementKey) && candidate.targetActive > 0 && route.start <= pulseTravel && route.start + route.length >= previousPulseTravel) {
              visitedEquipment.add(candidate.elementKey);
              pulseTravel = Math.max(previousPulseTravel, route.start);
              equipmentSlowUntil = now + 3000; equipmentSlowEnd = route.start + route.length;
              setEquipmentPause(`${kind} · ${element!.name} #${element!.elementId}`); break;
            }
          }
        }
        const pulseDistance = pulseTravel;
        if (followRoute.length) {
          const visual = followRoute.find(visual => {
            const route = cameraRoutes.get(flowPathKey(visual.elementKey, visual.pathOrdinal));
            return route && pulseDistance >= route.start && pulseDistance <= route.start + route.length;
          });
          const route = visual && cameraRoutes.get(flowPathKey(visual.elementKey, visual.pathOrdinal));
          if (visual && route && visual.targetActive > 0) {
            const progress = THREE.MathUtils.clamp((pulseDistance - route.start) / Math.max(route.length, .001), 0, 1);
            const cumulative = [0];
            for (let i = 1; i < visual.points.length; i++) cumulative.push(cumulative[i - 1] + visual.points[i].distanceTo(visual.points[i - 1]));
            const sample = sampleFlowPath({ points: visual.points, cumulative, length: route.length }, route.forward ? progress : 1 - progress);
            const movement = sample.position.clone().sub(controls.target).multiplyScalar(1 - Math.exp(-6 * delta));
            if (!followPausedRef.current) { controls.target.add(movement); camera.position.add(movement); }
            if (orbitPointer) orbitPointer.pivot.copy(controls.target);
            if (followingSystemKeyRef.current !== visual.systemKey) { followingSystemKeyRef.current = visual.systemKey; applyFlowPresentation(); }
            const stepKey = flowPathKey(visual.elementKey, visual.pathOrdinal);
            if (stepKey !== lastFollowStep) {
              lastFollowStep = stepKey;
              const element = graphStateRef.current?.elements.find(element => element.key === visual.elementKey);
              const network = graphStateRef.current?.systems.find(system => system.key === visual.systemKey)?.name || element?.systemName || visual.systemKey;
              const name = propertiesByKey.get(visual.elementKey)?.name || element?.name || visual.elementKey;
              if (visual.systemKey !== lastHistoryNetwork) { followHistory.push(network); lastHistoryNetwork = visual.systemKey; }
              if (/pompe|pump/i.test(`${name} ${element?.category || ''}`) && followHistory[followHistory.length - 1] !== name) followHistory.push(name);
              followHistory = followHistory.slice(-5);
              setFollowStep({ network, element: `${name} #${element?.elementId || visual.elementKey}`, color: visual.color, history: [...followHistory], reverse: traceReverse });
            }
            if (!followPausedRef.current && clearFollowViewRef.current && now - lastFollowInput > 3000 && now - lastClearView > 650 && !orbitPointer) {
              lastClearView = now;
              const offset = camera.position.clone().sub(controls.target);
              const obstacles = pickables.filter(isObjectVisible);
              const score = (candidate: THREE.Vector3) => {
                const origin = controls.target.clone().add(candidate), aim = controls.target.clone().sub(origin);
                collisionRaycaster.set(origin, aim.clone().normalize()); collisionRaycaster.far = Math.max(0, aim.length() - .7);
                return collisionRaycaster.intersectObjects(obstacles, false).filter(hit => sectionContains(activeSection, hit.point)).length;
              };
              const initial = score(offset);
              if (initial > 0) {
                let best = offset, bestScore = initial;
                for (const angle of [-Math.PI / 4, Math.PI / 4, -Math.PI / 2, Math.PI / 2]) {
                  const candidate = offset.clone().applyAxisAngle(new THREE.Vector3(0, 1, 0), angle), value = score(candidate);
                  if (value < bestScore) { best = candidate; bestScore = value; }
                }
                followOffset.copy(best);
              } else followOffset.copy(offset);
            }
            if (!followPausedRef.current && clearFollowViewRef.current && now - lastFollowInput > 3000 && !orbitPointer) camera.position.lerp(controls.target.clone().add(followOffset), 1 - Math.exp(-1.5 * delta));
          } else {
            const terminal = visual || followRoute[followRoute.length - 1];
            const result = graphStateRef.current && terminal ? explainFlowEnd(graphStateRef.current, terminal.elementKey, terminal.pathOrdinal, traceReverse) : null;
            stopFollowing();
            if (result) setFollowEnd(result);
          }
        }
        for (const visual of flowVisuals) {
          visual.current.active.value = THREE.MathUtils.damp(visual.current.active.value, visual.targetActive, 8, delta);
          if (Math.abs(visual.current.active.value - visual.targetActive) < .01) visual.current.active.value = visual.targetActive;
          if (visual.targetActive > 0) visual.current.time.value = now / 1000;
          const route = traceRoutes.get(flowPathKey(visual.elementKey, visual.pathOrdinal));
          visual.current.pulseDirection.value = route?.forward === false ? -1 : 1;
          visual.current.pulseStart.value = route ? route.start + (route.forward ? 0 : route.length) : 0;
          visual.current.pulseDistance.value = pulseDistance;
          visual.current.pulseEnabled.value = route && pulseDistance >= 0 && pulseDistance <= pulseEnd && visual.targetActive > 0 ? 1 : 0;
          const pathKey = flowPathKey(visual.elementKey, visual.pathOrdinal);
          const cameraRoute = cameraRoutes.get(pathKey);
          const arrivals = pulseArrivals.get(pathKey) || [];
          const offset = route?.forward === false ? route.length : 0;
          const extras = visual.current.extraPulseStarts.value;
          let extraCount = 0;
          for (const start of arrivals) {
            if (route && Math.abs(start - route.start) >= .05 && extraCount < 7) extras[extraCount++] = start + offset;
          }
          // Always preserve the camera's own front, even beyond the bounded arrival slots.
          if (route && cameraRoute && Math.abs(cameraRoute.start - route.start) >= .05 && !arrivals.some(start => Math.abs(start - cameraRoute.start) < .05)) {
            extras[Math.min(extraCount, 6)] = cameraRoute.start + offset;
            extraCount = Math.min(extraCount + 1, 7);
          }
          visual.current.extraPulseCount.value = extraCount;
          if (visual.water?.mesh.visible) {
            visual.water.uniforms.time.value = visual.current.time.value;
            visual.water.uniforms.active.value = visual.current.active.value;
            visual.water.uniforms.pulseStart.value = visual.current.pulseStart.value;
            visual.water.uniforms.pulseDirection.value = visual.current.pulseDirection.value;
            visual.water.uniforms.pulseDistance.value = pulseDistance;
            visual.water.uniforms.pulseEnabled.value = visual.current.pulseEnabled.value;
            visual.water.uniforms.extraPulseStarts.value.set(extras);
            visual.water.uniforms.extraPulseCount.value = extraCount;
            if (visual.current.active.value <= .01) visual.water.mesh.visible = false;
          }
        }
      }
      let animatedValveVisible = false;
      for (const visual of valveVisuals) {
        const moving = Math.abs(visual.handle.rotation.z - visual.targetAngle) > .02;
        visual.handle.rotation.z = THREE.MathUtils.damp(visual.handle.rotation.z, visual.targetAngle, 9, delta);
        const selected = selectedIndexRef.current === propertiesByKey.get(visual.elementKey)?.index;
        visual.marker.parent!.visible = visual.available && (valveMarkersVisibleRef.current || moving || (selected && waterViewRef.current));
        animatedValveVisible ||= visual.marker.parent!.visible;
      }
      valveGroup.visible = valveMarkersVisibleRef.current || animatedValveVisible;
      if (flowGroup.visible && now - lastFlowUpdate >= 1000 / 60) {
        lastFlowUpdate = now;
        const seconds = now / 1000;
        for (const animation of animatedFlowArrows) {
          const mid = animation.points[Math.floor(animation.points.length / 2)];
          const pixelsPerFoot = Math.max(mount.clientHeight, 1) / Math.max(.01, camera.position.distanceTo(mid) * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)));
          const stride = pixelsPerFoot < 4 ? 8 : pixelsPerFoot < 9 ? 4 : pixelsPerFoot < 18 ? 2 : 1;
          const distance = animation.enabled ? flowArrowDistance(animation.flowStart, animation.length, animation.spacing * stride, animation.slot, seconds) : null;
          animation.mesh.visible = distance !== null;
          if (distance === null) continue;
          const progress = animation.forward ? distance / animation.length : 1 - distance / animation.length;
          const sample = sampleFlowPath(animation, progress);
          animation.mesh.position.copy(sample.position);
          const direction = animation.forward ? sample.direction : sample.direction.multiplyScalar(-1);
          // Face the camera around the pipe axis: a flat chevron must never
          // disappear edge-on when orbiting around a vertical or horizontal pipe.
          const view = camera.position.clone().sub(sample.position).normalize();
          const right = new THREE.Vector3().crossVectors(direction, view);
          if (right.lengthSq() < .0001) right.set(1, 0, 0).applyQuaternion(camera.quaternion).addScaledVector(direction, -new THREE.Vector3(1, 0, 0).applyQuaternion(camera.quaternion).dot(direction));
          right.normalize();
          const normal = new THREE.Vector3().crossVectors(right, direction).normalize();
          animation.mesh.quaternion.setFromRotationMatrix(new THREE.Matrix4().makeBasis(right, direction, normal));
          // About 22 screen pixels at any zoom, capped on short fittings.
          const pixelsToWorld = camera.position.distanceTo(sample.position) * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) / Math.max(mount.clientHeight, 1);
          const diameterLimit = waterViewRef.current && animation.waterRadius ? animation.waterRadius * 2 / (.48 * 1.32) * .85 : Infinity;
          const scale = Math.min(diameterLimit, animation.length * .65, THREE.MathUtils.clamp(pixelsToWorld * 22 / .655, .18, 5));
          animation.mesh.scale.setScalar(scale);
        }
      }
      if (flowGroup.visible && ++presentationFrame % 8 === 0) applyFlowPresentation();
      doors.forEach((door) => {
        const target = door.open ? Math.PI * .51 : 0; door.angle = THREE.MathUtils.damp(door.angle, target, 8, delta); door.meshes.forEach((mesh) => { mesh.rotation.y = door.angle; });
      });
      if (realisticLightingRef.current) {
        const look = new THREE.Vector3(); camera.getWorldDirection(look);
        head.position.copy(camera.position); headTarget.position.copy(camera.position).addScaledVector(look, 10); headTarget.updateMatrixWorld();
      }
      applyFlowFocus(flowGroup.visible);
      buildingVisibility?.sync();
      const refreshVisibility = now - lastLabelVisibility >= 100;
      if (refreshVisibility) lastLabelVisibility = now;
      for (const label of Array.from(markupLabelsRef.current?.children || [])) {
        const element = label as HTMLElement;
        const mark = draftRef.current?.id === element.dataset.markup ? draftRef.current : markupsRef.current.find(item => item.id === element.dataset.markup);
        if (!mark) continue;
        const dimension = mark.dimensions?.find(item => item.id === element.dataset.dimension);
        const labelPoint = dimension ? dimensionFoot(mark, dimension).lerp(new THREE.Vector3(...mark.position), .5) : new THREE.Vector3(...mark.position);
        const projected = labelPoint.clone().project(camera);
        const visible = sectionContains(activeSection, labelPoint) && projected.z >= -1 && projected.z <= 1 && Math.abs(projected.x) < 1 && Math.abs(projected.y) < 1;
        if (visible && refreshVisibility) {
          const hidden = buildingVisibility?.obscured(camera.position, new THREE.Vector3(...mark.position), activeSection);
          element.style.opacity = hidden && !mark.needsReview ? '0.2' : '1';
        }
        element.style.display = visible ? '' : 'none';
        element.style.left = `${(projected.x + 1) * mount.clientWidth / 2}px`;
        element.style.top = `${(1 - projected.y) * mount.clientHeight / 2}px`;
      }
      const measureState = measureStateRef.current;
      if (measureState !== lastMeasureState) {
        disposeMarkups(measureGroup); lastMeasureState = measureState;
        if (measureState.open) {
          measureState.measures.forEach(measure => measureGroup.add(measureObject(measure)));
          if (measureState.start) {
            const marker = new THREE.Mesh(new THREE.SphereGeometry(.06, 12, 8), new THREE.MeshBasicMaterial({ color: '#0369a1', depthTest: false }));
            marker.position.set(...measureState.start); marker.renderOrder = 101; measureGroup.add(marker);
          }
        }
      }
      for (const label of Array.from(measureLabelsRef.current?.children || [])) {
        const element = label as HTMLElement;
        const measure = measureState.measures.find(item => item.id === element.dataset.measure);
        const horizontal = measure ? horizontalDimension(measure) : null;
        const position = measure ? (measure.mode === 'horizontal' ? horizontal!.from.lerp(horizontal!.to, .5) : new THREE.Vector3(...measure.start).lerp(new THREE.Vector3(...measure.end), .5)) : measureState.start ? new THREE.Vector3(...measureState.start) : null;
        if (!position) { element.style.display = 'none'; continue; }
        const projected = position.clone().project(camera);
        const visible = measureState.open && sectionContains(activeSection, position) && projected.z >= -1 && projected.z <= 1 && Math.abs(projected.x) < 1 && Math.abs(projected.y) < 1;
        element.style.display = visible ? '' : 'none';
        element.style.left = Math.max(0, Math.min((projected.x + 1) * mount.clientWidth / 2, mount.clientWidth - element.offsetWidth - 12)) + 'px';
        element.style.top = Math.max(element.offsetHeight + 4, (1 - projected.y) * mount.clientHeight / 2) + 'px';
      }
      renderer.autoClear = false; renderer.clear(); renderer.render(scene, camera);
      if (sceneReady && now - lastRoomCheck >= 200) {
        lastRoomCheck = now;
        const walking = navigationModeRef.current === 'maquette' && !webFlightMode;
        const candidate = locateRoom(viewerPackage?.viewer?.rooms || [], camera.position.x, walking ? playerFootY : camera.position.y, camera.position.z);
        const key = candidate ? candidate.room.key + ':' + candidate.approximate : '';
        if (key !== roomCandidateKey) { roomCandidateKey = key; roomCandidateSince = now; }
        if (key !== shownRoomKey && now - roomCandidateSince >= 400) {
          shownRoomKey = key; currentRoom = candidate; setRoomLocation(candidate);
        }
      }
      if (sceneReady && navigationModeRef.current === 'maquette' && miniMapVisibleRef.current && miniMapRef.current) {
        miniMap.draw(miniMapRef.current, camera, pickables, now,
          currentRoom ? currentRoom.room.baseElevation + 4 : camera.position.y, currentRoom?.room);
      }
      if (flowGroup.visible || valveGroup.visible) {
        renderer.clearDepth();
        if (buildingVisibility && !waterViewRef.current && modelOpacityRef.current > .3) renderer.render(buildingVisibility.scene, camera);
        renderer.render(overlayScene, camera);
      }
      if (!openingReleased) { openingReleased = true; setOpening(false); }
    };
    renderer.setAnimationLoop(animate);
    return () => {
      disposed = true; renderer.setAnimationLoop(null); vrVisit.dispose();
      if (vrVisitRef.current === vrVisit) vrVisitRef.current = null;
      preparation?.dispose();
      stream?.dispose(); streamRef.current = null; updateCutoutsRef.current = null; window.clearTimeout(geometryTimer);
      buildingVisibility?.dispose();
      cutoutsRef.current?.dispose(); cutoutsRef.current = null;
      disposeMarkups(measureGroup); disposeMarkups(markupGroup); markupGroupRef.current = null;
      gizmo.dispose(); disposeMarkups(editRoot); selection.dispose(); renderer.domElement.removeEventListener('dblclick', onDoubleClick);

      disposed = true; observer.disconnect();
      hoveredSystemKeyRef.current = null;
      renderer.domElement.removeEventListener('pointerleave', clearFlowHover);
      if (statsTimer !== null) window.clearTimeout(statsTimer);
      renderer.domElement.removeEventListener('pointerdown', onPointerDown, true); renderer.domElement.removeEventListener('pointermove', onPointerMove, true); renderer.domElement.removeEventListener('pointerup', onPointerUp); renderer.domElement.removeEventListener('contextmenu', onContextMenu); renderer.domElement.removeEventListener('wheel', onWheel, true);
      labelLayer?.removeEventListener('wheel', onWheel, true);
      window.removeEventListener('keydown', onKeyDown); window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('keydown', keyboardDevice, true);
      window.removeEventListener('pointerdown', pointerDevice, true);
      window.removeEventListener('pointermove', pointerDevice, true);
      window.removeEventListener('wheel', keyboardDevice);
      setGamepadMenu(false); setConnectedPad(null); setDevice('keyboard');
      gamepadFieldRef.current = null; setGamepadField(null);
      document.removeEventListener('visibilitychange', onVisibilityChange);
      window.removeEventListener('blur', resetInput); renderer.domElement.removeEventListener('pointercancel', resetInput);
      controls.dispose(); controlsRef.current = null; viewActionsRef.current = null;
      if (flowGroupRef.current === flowGroup) flowGroupRef.current = null;
      if (valveGroupRef.current === valveGroup) valveGroupRef.current = null;
      collisionGeometries.forEach((geometry) => geometry.disposeBoundsTree());
      collisionGeometries.clear();
      modelMaterialStatesRef.current = [];
      flowFocusAppliedRef.current = null;
      renderer.dispose(); renderer.domElement.remove();
      scene.traverse((object) => {
        const renderable = object as THREE.Object3D & { geometry?: { dispose: () => void }; material?: THREE.Material | THREE.Material[] };
        renderable.geometry?.dispose();
        if (renderable.material) (Array.isArray(renderable.material) ? renderable.material : [renderable.material]).forEach((material) => material.dispose());
      });
      overlayScene.traverse((object) => {
        const renderable = object as THREE.Object3D & { geometry?: { dispose: () => void }; material?: THREE.Material | THREE.Material[] };
        renderable.geometry?.dispose();
        if (renderable.material) (Array.isArray(renderable.material) ? renderable.material : [renderable.material]).forEach((material) => material.dispose());
      });
    };
  }, [viewerPackage, propertiesByIndex, propertiesByKey, applyFlowFocus, resolved, changeNavigationMode]);

  useEffect(() => {
    viewActionsRef.current?.updateMepState(graph, hiddenSystems);
  }, [graph, hiddenSystems]);

  const toggleSystem = useCallback((key: string) => setHiddenSystems((current) => { const next = new Set(current); if (next.has(key)) next.delete(key); else next.add(key); return next; }), []);
  const runScenarioEdit = useCallback(async (element: MepGraph['elements'][number], kind: 'valve' | 'source', value: boolean | MepSourceOverride) => {
    if (!scenario || !tokenRef.current || resolved?.role !== 'editor') return;
    const id = element.persistentId || element.key; setSaving(true); setContextMenu(null);
    try {
      const result = kind === 'valve'
        ? await updateValve(tokenRef.current, scenario.revision, id, value as boolean, 'Invité web')
        : await updateSource(tokenRef.current, scenario.revision, id, value as MepSourceOverride, 'Invité web');
      setScenario(current => !current || result.scenario.revision >= current.revision ? result.scenario : current);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Modification impossible');
      if (tokenRef.current && viewerPackage) { const latest = await readScenario(tokenRef.current); setScenario(current => !current || latest.scenario.revision > current.revision ? latest.scenario : current); }
    } finally { setSaving(false); }
  }, [resolved?.role, scenario, viewerPackage]);
  const toggleValve = useCallback(async () => {
    if (!selectedValve || !selectedMepElement) return;
    await runScenarioEdit(selectedMepElement, 'valve', !selectedValve.isClosed);
  }, [runScenarioEdit, selectedMepElement, selectedValve]);

  useEffect(() => {
    const context = (document as Document & { modelContext?: { registerTool: (tool: unknown, options?: { signal?: AbortSignal }) => void | Promise<void> } }).modelContext;
    if (!context?.registerTool) return;
    const lifecycle = new AbortController();
    void Promise.resolve(context.registerTool({ name: 'inspect_selected_mep_element', title: 'Inspecter l’élément MEP sélectionné', description: 'Retourne la fiche et l’état de l’élément actuellement sélectionné dans le viewer.', inputSchema: { type: 'object', properties: {}, additionalProperties: false }, annotations: { readOnlyHint: true, untrustedContentHint: true }, execute: () => selectedProperty ? { elementId: selectedProperty.elementId, name: selectedProperty.name, properties: selectedProperty.properties, valveClosed: selectedValve?.isClosed ?? null } : { selected: false } }, { signal: lifecycle.signal })).catch(() => {});
    void Promise.resolve(context.registerTool({ name: 'set_selected_valve_state', title: 'Modifier la vanne sélectionnée', description: 'Ouvre ou ferme la vanne sélectionnée avec les mêmes contrôles de droits et de révision que le bouton visible.', inputSchema: { type: 'object', properties: { closed: { type: 'boolean' } }, required: ['closed'], additionalProperties: false }, annotations: { readOnlyHint: false, untrustedContentHint: false }, execute: async (input: unknown) => { const closed = (input as { closed?: unknown })?.closed; if (typeof closed !== 'boolean') throw new Error('closed doit être un booléen'); if (!selectedValve || !selectedMepElement || !scenario || !tokenRef.current || resolved?.role !== 'editor') throw new Error('Aucune vanne modifiable n’est sélectionnée'); if (selectedValve.isClosed !== closed) await toggleValve(); return { targetId: selectedMepElement.persistentId || selectedMepElement.key, closed }; } }, { signal: lifecycle.signal })).catch(() => {});
    return () => lifecycle.abort();
  }, [selectedProperty, selectedValve, selectedMepElement, scenario, resolved, toggleValve]);

  const details: WebProperty = selectedProperty || { index: -1, key: 'demo', elementId: 485211, name: 'Vanne papillon DN 100', category: 'Accessoires de canalisation', typeName: 'Vanne papillon', levelName: 'SS1 · Technique', documentTitle: '', properties: { Système: 'Eau glacée — Aller', Diamètre: '100 mm', Débit: demoValveClosed ? '0,00 m³/h' : '4,82 m³/h', Fabricant: 'Socla' } };
  const isClosed = selectedValve?.isClosed ?? demoValveClosed; const isEditor = resolved?.role === 'editor';
  const holdInput = (code: string, active: boolean) => viewActionsRef.current?.setInput(code, active);
  const cycleOpacity = () => setModelOpacity((current) => current > .9 ? .55 : current > .3 ? .16 : 1);
  const activeSources = graph?.sources.filter((source) => source.isActive) || [];
  const arrivalCount = activeSources.filter((source) => source.boundaryKind === 0 || String(source.boundaryKind).toLowerCase() === 'inlet').length;
  const returnCount = activeSources.filter((source) => source.boundaryKind === 1 || String(source.boundaryKind).toLowerCase() === 'outlet').length;
  const selectedPath = selectedMepElement?.paths?.[0];
  const directionExplanation = selectedPath?.directionExplanation;
  const selectedSystem = selectedMepElement ? systems.find((system) => system.key === selectedMepElement.systemKey) : null;
  const systemIsIsolated = !!selectedSystem && systems.every((system) => system.key === selectedSystem.key || hiddenSystems.has(system.key));
  const isolateSelectedSystem = () => {
    if (!selectedSystem) return;
    setHiddenSystems(systemIsIsolated ? new Set() : new Set(systems.filter((system) => system.key !== selectedSystem.key).map((system) => system.key)));
  };
  const previousInspections = inspectionHistory.filter((index) => index !== selectedIndex).map((index) => propertiesByIndex.get(index)).filter((item): item is WebProperty => !!item);
  const beginMarkup = (kind: Markup['kind']) => {
    setEditMode('translate');
    if (!contextMenu) return;
    const property = propertiesByIndex.get(contextMenu.index);
    if (!property) return;
    setMarkupError(null); setActiveMarkup(null);
    setDraftMarkup({ id: crypto.randomUUID(), kind, elementKey: property.key, elementName: property.name, position: contextMenu.position, normal: contextMenu.normal, text: '', shape: 'rectangle', diameterCm: 40, lot: currentLot, widthCm: 60, heightCm: 40, depthCm: contextMenu.depthCm ?? 30, modelRevision: resolved!.publication.revision });
    setContextMenu(null);
  };
  const persistMarkup = async (mark: Markup, remove = false) => {
    if (!tokenRef.current || saving) return;
    setSaving(true); setMarkupError(null);
    try {
      const property = viewerPackage?.properties.find(p => p.key === mark.elementKey);
      const saved = !remove && property ? anchorMarkup(mark, property, resolved!.publication.revision) : { ...mark, modelRevision: resolved!.publication.revision };
      const result = await saveMarkup(tokenRef.current, saved, remove);
      setScenario(current => !current || result.scenario.revision >= current.revision ? result.scenario : current);
      if (!remove && mark.kind === 'reservation') setCurrentLot(reservationLot(mark));
      setDraftMarkup(null); setActiveMarkup(null);
    } catch (caught) { setMarkupError(caught instanceof Error ? caught.message : 'Enregistrement impossible'); }
    finally { setSaving(false); }
  };
  const persistLot = async (id: string, lot: ReservationLot) => {
    if (!tokenRef.current || !resolved || !scenario || !isEditor) throw new Error('Utilisez un lien de modification pour gérer les lots.');
    try {
      const result = await saveReservationLot(tokenRef.current, scenario.revision, resolved.publication.revision, id, lot);
      setScenario(current => !current || result.scenario.revision >= current.revision ? result.scenario : current);
    } catch (caught) {
      try { const { scenario: next } = await readScenario(tokenRef.current); setScenario(current => !current || next.revision > current.revision ? next : current); } catch { /* Keep the original error. */ }
      throw caught;
    }
  };
  const openLots = () => { setLotsOpen(true); setMarkupListOpen(false); setSectionOpen(false); setPickingSection(false); setPickingDimension(false); cancelMeasure(); setMeasureOpen(false); };
  const startMeasure = () => { setPickingDimension(false); setPickingSection(false); setActiveMarkup(null); setContextMenu(null); setRelocating(null); measurePickRef.current = { picking: true, start: null }; setMeasureStart(null); setPickingMeasure(true); };
  const contextProperty = contextMenu ? propertiesByIndex.get(contextMenu.index) || null : null;
  const contextMepElement = contextProperty && graph ? graph.elements.find((element) => element.key === contextProperty.key) || null : null;
  const contextValve = contextMepElement && graph ? graph.valves.find((valve) => valve.elementKey === contextMepElement.key && valve.isEnabledAsValve) || null : null;
  const contextSource = contextMepElement && graph ? graph.sources.find((source) => source.elementKey === contextMepElement.key && source.isActive) || null : null;
  const contextSourceKind = contextSource && (contextSource.boundaryKind === 1 || String(contextSource.boundaryKind).toLowerCase() === 'outlet') ? 'outlet' : contextSource ? 'inlet' : null;

  const exportReservations = (ifc: boolean, annotations = false) => {
    try {
      if (!viewerPackage || !resolved || exportWorkerRef.current) return;
      const worker = new Worker(new URL('../lib/mep-exchange.worker.ts', import.meta.url), { type: 'module' });
      exportWorkerRef.current = worker; setExporting(true); setError(null);
      const finish = () => { worker.terminate(); exportWorkerRef.current = null; setExporting(false); };
      worker.onmessage = ({ data }: MessageEvent<{ content?: string; error?: string }>) => {
        try {
          if (data.error || !data.content) throw new Error(data.error || 'Export impossible');
          downloadExchange(data.content, annotations ? 'annotations.ifc' : `reservations${exportLot ? '-' + lotDetails(exportLot, lotSettings).name.replace(/[^a-z0-9_-]/gi, '_') : ''}.${ifc ? 'ifc' : 'bimaestro-reservations.json'}`);
        } catch (caught) { setError(caught instanceof Error ? caught.message : 'Export impossible'); }
        finally { finish(); }
      };
      worker.onerror = () => { setError('Export impossible. Vous pouvez réessayer.'); finish(); };
      worker.postMessage({ args: [allMarkups, viewerPackage.manifest, resolved.publication.id, exportLot || undefined, lotSettings], ifc, annotations });
    } catch (caught) { exportWorkerRef.current?.terminate(); exportWorkerRef.current = null; setExporting(false); setError(caught instanceof Error ? caught.message : 'Export impossible'); }
  };
  const exportBcf = async (marks: Markup[] = allMarkups) => {
    if (!viewerPackage || !resolved || exporting || !viewActionsRef.current) return;
    try {
      setExporting(true); setError(null);
      if (viewerPackage.manifest.tiles && loadedZones < viewerPackage.manifest.tiles.length)
        throw new Error('Attendez la fin du chargement de la maquette pour inclure les vues dans le BCF.');
      const archive = await annotationsBcf(marks, viewerPackage.manifest, resolved.publication.id, mark => viewActionsRef.current!.captureMarkup(mark), viewerPackage.properties);
      downloadBcf(archive, marks.length === 1 ? 'annotation.bcfzip' : 'annotations.bcfzip');
    } catch (caught) { setError(caught instanceof Error ? caught.message : 'Export BCF impossible'); }
    finally { setExporting(false); }
  };
  const sectionFromContext = () => {
    if (!contextMenu || section.length >= maximumSections) return;
    const serial = ++sectionSerial.current;
    const cut = sectionFromFace('section-' + serial, 'Coupe ' + serial, new THREE.Vector3(...contextMenu.position), new THREE.Vector3(...contextMenu.normal));
    setSection(current => current.length < maximumSections ? [...current, cut] : current);
    setSelectedSection(cut.id); setSectionOpen(true); setPickingSection(false);
    cancelMeasure(); setMeasureOpen(false); setPickingDimension(false); setContextMenu(null);
  };
  const draftIsHost = isReservationHost(viewerPackage?.properties.find(property => property.key === draftMarkup?.elementKey));
  const activeMark = allMarkups.find(item => item.id === activeMarkup);
  return <><main className="viewer-shell compact-ui" data-panel-collapsed={panelCollapsed} data-input-device={inputDevice} data-gamepad-aiming={inputDevice === 'gamepad' && !gamepadMenu && (pickingMeasure || pickingSection || pickingDimension || !!relocating || !!draftMarkup)} inert={opening}>
    {gamepadField && <GamepadFieldEditor key={gamepadField.name + gamepadField.type} target={gamepadField} onClose={closeGamepadField}/>}
    {draftMarkup && <section className="markup-editor" aria-label="Modifier l’annotation">
        <strong>{draftMarkup.kind === 'note' ? 'Annotation' : 'Réservation'}</strong>
        <p>{draftMarkup.elementName}</p>
        <div className="markup-actions"><Button data-gamepad-edit="translate" size="sm" variant={editMode === 'translate' ? 'default' : 'outline'} onClick={() => setEditMode('translate')}>Déplacer</Button>{draftMarkup.kind === 'reservation' && <Button data-gamepad-edit="scale" size="sm" variant={editMode === 'scale' ? 'default' : 'outline'} onClick={() => setEditMode('scale')}>Redimensionner</Button>}</div>
        <p>{inputDevice === 'gamepad' ? 'Choisissez Déplacer ou Redimensionner, puis utilisez le stick gauche. Le stick droit tourne autour de la réservation ; LT affine le mouvement. Menu revient aux champs.' : 'Glissez les poignées sur la face.'} La découpe sera calculée après « Enregistrer ».</p>
        {draftMarkup && <form onSubmit={event => { event.preventDefault(); void persistMarkup(draftMarkup); }}>
          <label>{draftMarkup.kind === 'note' ? 'Votre annotation' : 'Commentaire (facultatif)'}<textarea required={draftMarkup.kind === 'note'} maxLength={2000} value={draftMarkup.text} onChange={event => setDraftMarkup({ ...draftMarkup, text: event.target.value })}/></label>
          {draftMarkup.kind === 'reservation' && <><ReservationFields gamepad={inputDevice === 'gamepad'} gamepadMode={navigationMode} mark={draftMarkup} lots={lots} onChange={setDraftMarkup} onManageLots={openLots} picking={pickingDimension} onPick={() => { cancelMeasure(); setMeasureOpen(false); setPickingDimension(value => !value); setPickingSection(false); setMarkupError(null); }}/><p>{draftIsHost ? 'La profondeur est ajustée à l’épaisseur détectée du mur ou du sol. Vous pouvez la modifier pour une réservation partielle.' : 'La profondeur part de la face sélectionnée vers l’intérieur. Les murs et les sols peuvent être découpés.'}</p></>}
          {markupError && <p role="alert" className="markup-error">{markupError}</p>}
          <div className="markup-actions"><Button type="button" variant="outline" disabled={saving} onClick={() => setDraftMarkup(null)}>Annuler</Button><Button type="submit" disabled={saving || (draftMarkup.kind === 'note' && !draftMarkup.text.trim())}>{saving ? 'Enregistrement…' : 'Enregistrer'}</Button></div>
        </form>}
      </section>}
    {impactOpen && <MepImpactPanel graph={graph} reportText={reportText} calculating={calculating} error={calculationError} isEditor={isEditor} token={shareToken} scenario={scenario} publicationRevision={resolved?.publication.revision} selectedKey={selectedProperty?.key} onScenario={next => setScenario(current => !current || next.revision >= current.revision ? next : current)} onClose={() => setImpactOpen(false)} onReference={setReference} onSelect={key => { const property = propertiesByKey.get(key); if (property) { setSelectedIndex(property.index); requestAnimationFrame(() => viewActionsRef.current?.frameSelected()); } }}/>}
    <header className="topbar"><div className="brand-mark"><Waves size={19}/><span>BIMaestro</span><b>Maquette 3D</b></div><div className="model-title"><span>{resolved?.publication.name || 'Démonstration · Local technique'}</span><Badge variant="outline">Révision {resolved?.publication.revision || 'démo'}</Badge></div><div className={`access-badge ${isEditor ? 'editor' : 'reader'}`}>{isEditor ? <Pencil size={15}/> : <LockKeyhole size={15}/>}<span><small>MODE DU LIEN</small>{isEditor ? 'ÉDITION COLLABORATIVE' : 'CONSULTATION · LECTURE SEULE'}</span></div></header>
    <section className="viewport">{sectionOpen && <SectionControls gamepad={inputDevice === 'gamepad'} gamepadMode={navigationMode} value={section} onChange={setSection} selected={selectedSection} onSelect={setSelectedSection} picking={pickingSection} onPick={value => { cancelMeasure(); setPickingDimension(false); setPickingSection(value); }} extent={sectionExtent}/>}<div ref={mountRef} className="three-mount"/><div className="view-toolbar" aria-label="Outils de la maquette"><VrEntry active={vrActive} disabled={opening || !!draftMarkup || !!relocating || saving} enter={async () => { await vrVisitRef.current?.enter(); }} exit={async () => { await vrVisitRef.current?.exit(); }}/><Button size="sm" variant="secondary" onClick={() => viewActionsRef.current?.isometric()}><Box/> Isométrique</Button><Button size="sm" variant="secondary" aria-expanded={sectionOpen} onClick={() => { setLotsOpen(false); setMarkupListOpen(false); setMeasureOpen(false); cancelMeasure(); setPickingDimension(false); setSectionOpen(value => !value); if (!sectionOpen && section.length === 0) setPickingSection(true); else setPickingSection(false); }}><Scissors/> Coupe{section.length ? ' · ' + section.length : ''}</Button><Button size="sm" variant="secondary" disabled={!!draftMarkup} aria-expanded={measureOpen} onClick={() => { setMeasureOpen(value => !value); setSectionOpen(false); setPickingSection(false); setLotsOpen(false); setMarkupListOpen(false); if (!measureOpen) startMeasure(); else cancelMeasure(); }}><ArrowLeftRight/> Mesurer</Button><Button size="sm" variant="secondary" onClick={() => setImpactOpen(v => !v)}><ChartNoAxesCombined/> Analyser les coupes</Button><Button size="sm" variant="secondary" onClick={() => { setMarkupListOpen(v => !v); setLotsOpen(false); setSectionOpen(false); setPickingSection(false); setMeasureOpen(false); cancelMeasure(); }}><MessageSquare/> Annotation et Résa ({allMarkups.length}){reviewCount > 0 ? ` · ⚠ ${reviewCount} à vérifier` : ''}</Button></div><div className={`viewer-left-column${navigationMode === 'maquette' ? ' with-mini-map' : ''}`}>{navigationMode === 'maquette' && <div className="viewer-mini-map-tools">
      {!miniMapVisible && <button className="viewer-map-show" aria-controls="viewer-mini-map" onClick={() => setMiniMapVisible(true)} title={inputDevice === 'gamepad' ? 'Afficher la mini-carte (croix ↑)' : 'Afficher la mini-carte (M)'}>Mini-carte <kbd>{inputDevice === 'gamepad' ? '↑' : 'M'}</kbd></button>}
      <div id="viewer-mini-map" className="viewer-mini-map" hidden={!miniMapVisible}><header><strong title={roomLocation ? `${roomLocation.room.levelName} · ${roomLocation.room.number}` : undefined}>{roomLocation?.room.name || 'Plan local'}</strong><button onClick={() => setMiniMapVisible(false)} aria-label="Fermer la mini-carte" title={inputDevice === 'gamepad' ? 'Fermer la mini-carte (croix ↑)' : 'Fermer la mini-carte (M)'}>×</button></header><canvas ref={miniMapRef} width={280} height={280} aria-label="Plan local de 30 mètres, position et direction de la caméra"/></div>
      </div>}<div className="appearance-toolbar" aria-label="Réglages de rendu"><button className={realisticLighting ? 'active' : ''} onClick={() => setRealisticLighting((value) => !value)} title="Basculer entre le mode ombré du plugin et les couleurs uniformes"><SunMedium/><span>{realisticLighting ? 'Ombré' : 'Uniforme'}</span></button><button className={highContrast ? 'active' : ''} onClick={() => setHighContrast((value) => !value)} title="Renforcer les contours et le contraste"><Contrast/><span>Contraste</span></button><button className={modelOpacity < 1 ? 'active' : ''} onClick={cycleOpacity} title="Faire varier la transparence de la maquette"><Layers3/><span>{Math.round(modelOpacity * 100)} %</span></button></div></div><div className="navigation-switch" aria-label="Mode de navigation"><button aria-label="Navigation web" title="Navigation web" className={navigationMode === 'orbit' ? 'active' : ''} onClick={() => changeNavigationMode('orbit')}><Hand size={15}/><span>Navigation web</span></button><button aria-label="Maquette 3D" title="Maquette 3D" className={navigationMode === 'maquette' ? 'active' : ''} onClick={() => changeNavigationMode('maquette')}><Footprints size={15}/><span>Maquette 3D</span></button></div>{navigationMode === 'maquette' && <div className="flight-controls" aria-label="Commandes de déplacement vertical"><button className={flightMode ? 'active' : ''} onClick={() => viewActionsRef.current?.toggleFlight()} title={flightMode ? 'Revenir au mode marche' : 'Activer le vol libre'}><Plane/><span>{flightMode ? 'Quitter le vol' : 'Vol libre'}</span></button>{flightMode && <><button data-gamepad-vertical="1" aria-label="Monter" title="Monter" onPointerDown={() => holdInput('Space', true)} onPointerUp={() => holdInput('Space', false)} onPointerCancel={() => holdInput('Space', false)} onPointerLeave={() => holdInput('Space', false)}><ArrowUp/><span>Monter</span></button><button data-gamepad-vertical="-1" aria-label="Descendre" title="Descendre" onPointerDown={() => holdInput('ShiftLeft', true)} onPointerUp={() => holdInput('ShiftLeft', false)} onPointerCancel={() => holdInput('ShiftLeft', false)} onPointerLeave={() => holdInput('ShiftLeft', false)}><ArrowDown/><span>Descendre</span></button></>}<button onClick={() => viewActionsRef.current?.enterSpectator()} title="Revenir au point de départ"><RotateCcw/><span>Réapparaître</span></button></div>}{contextMenu && contextProperty && <div className="element-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} role="menu"><header><span>{contextProperty.name}</span><small>#{contextProperty.elementId}</small></header><button disabled={section.length >= maximumSections} onClick={sectionFromContext}>Faire une coupe</button>{isEditor && <><button onClick={() => beginMarkup('note')}>Annoter ici</button><button onClick={() => beginMarkup('reservation')}>Créer une réservation ici</button>{contextMepElement && <>{contextValve && <button disabled={saving} onClick={() => void runScenarioEdit(contextMepElement, 'valve', !contextValve.isClosed)}>{contextValve.isClosed ? 'Ouvrir la vanne' : 'Fermer la vanne'}</button>}<button disabled={saving} className={contextSourceKind === 'inlet' ? 'active' : ''} onClick={() => void runScenarioEdit(contextMepElement, 'source', 'inlet')}>{contextSourceKind === 'inlet' ? '✓ Départ du réseau' : 'Définir comme départ'}</button><button disabled={saving} className={contextSourceKind === 'outlet' ? 'active' : ''} onClick={() => void runScenarioEdit(contextMepElement, 'source', 'outlet')}>{contextSourceKind === 'outlet' ? '✓ Retour du réseau' : 'Définir comme retour'}</button>{contextSourceKind && <button disabled={saving} className="danger" onClick={() => void runScenarioEdit(contextMepElement, 'source', 'none')}>Retirer {contextSourceKind === 'inlet' ? 'le départ' : 'le retour'}</button>}</>}</>}<button onClick={() => setContextMenu(null)}>Fermer</button></div>}<div ref={markupLabelsRef} className="markup-labels">{visibleMarkups.map(mark => <button key={mark.id} data-markup={mark.id} className={`markup-label ${mark.kind}`} style={{ borderColor: lotDetails(reservationLot(mark), lotSettings).color, color: lotDetails(reservationLot(mark), lotSettings).color, background: "white" }} onClick={() => { setActiveMarkup(mark.id); setMarkupError(null); }} title={mark.text || 'Réservation'}>{mark.kind === 'note' ? 'A.' : 'R.'} <span>{mark.kind === 'note' ? mark.text : `${lotDetails(reservationLot(mark), lotSettings).name} · ${reservationLabel(mark)}`}</span></button>)}{dimensionMarks.flatMap(mark => (mark.dimensions || []).map(dimension => <span key={mark.id + dimension.id} data-markup={mark.id} data-dimension={dimension.id} className="markup-label dimension-label">{dimensionDistance(mark, dimension).toFixed(1)} cm</span>))}</div>{activeMark && <div className="markup-card"><strong>{activeMark.kind === 'note' ? 'Annotation' : 'Réservation'}</strong><small>{activeMark.elementName}</small>{activeMark.needsReview && <p aria-live="polite">{activeMark.reviewCandidate ? 'Position proposée sur le support retrouvé. Vérifiez la position et les cotations, puis confirmez. Les autres réservations sont temporairement masquées.' : 'Ancienne position affichée. Le support n’a pas été retrouvé avec certitude : choisissez une nouvelle face. Les autres réservations sont temporairement masquées.'}</p>}<p>{activeMark.text}</p>{activeMark.kind === 'reservation' && <p>{lotDetails(reservationLot(activeMark), lotSettings).name} · {reservationLabel(activeMark)} · Profondeur {activeMark.depthCm} cm</p>}{activeMark.dimensions?.map(dimension => <p key={dimension.id}>{dimension.elementName} · Axe : {dimensionDistance(activeMark, dimension).toFixed(1)} cm</p>)}{markupError && <p role="alert">{markupError}</p>}{isEditor && <>{activeMark.needsReview && activeMark.reviewCandidate && <Button size="sm" disabled={saving} onClick={() => void persistMarkup(activeMark)}>Confirmer cette position et les cotations</Button>}{activeMark.kind === 'note' && !activeMark.needsReview && <Button size="sm" variant="outline" disabled={exporting || !!(viewerPackage?.manifest.tiles && loadedZones < viewerPackage.manifest.tiles.length)} onClick={() => void exportBcf([activeMark])}>Exporter BCF</Button>}<Button size="sm" onClick={() => { setEditMode("translate"); if (activeMark.needsReview) setRelocating(activeMark); else setDraftMarkup({ ...activeMark }); setActiveMarkup(null); }}>Modifier / déplacer</Button>{activeMark.kind === 'note' && <Button size="sm" onClick={() => { setRelocating(activeMark); setActiveMarkup(null); }}>Replacer sur une face</Button>}<Button size="sm" variant="outline" disabled={saving} onClick={() => void persistMarkup(activeMark, true)}>Supprimer</Button></>}<Button size="sm" variant="ghost" onClick={() => setActiveMarkup(null)}>Fermer</Button></div>}{relocating && <div className="relocation-hint">{inputDevice === 'gamepad' ? `Visez la nouvelle face et appuyez sur ${navigationMode === 'maquette' ? 'RT' : 'A'}` : 'Cliquez sur la nouvelle face'} pour replacer « {relocating.text || relocating.elementName} ». {inputDevice === 'gamepad' ? navigationMode === 'maquette' ? 'RS clic' : 'B' : 'Échap'} pour annuler.</div>}
    {markupListOpen && <aside className="markup-list" aria-label="Annotations et réservations"><strong>Annotations et réservations</strong><h2 className="export-heading">Exporter les réservations</h2>{viewerPackage && !viewerPackage.manifest.sharedCoordinates && <p role="status">Pour exporter un IFC en coordonnées partagées, republiez cette maquette depuis Revit avec le plugin corrigé.</p>}<label className="export-lot">Lots à exporter<select value={exportLot} onChange={event => setExportLot(event.target.value)}><option value="">Tous les lots</option>{lots.map(lot => <option key={lot.id} value={lot.id}>{lot.name}</option>)}</select></label><div className="markup-actions"><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'reservation' && !m.needsReview && (!exportLot || reservationLot(m) === exportLot))} onClick={() => exportReservations(true)}>{exporting ? 'Export en cours…' : 'Exporter IFC'}</Button><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'reservation' && !m.needsReview && (!exportLot || reservationLot(m) === exportLot))} onClick={() => exportReservations(false)}>Exporter vers Revit</Button></div><p>IFC : coordonnées partagées du site actif Revit. Export vers Revit : repère interne. Les points à vérifier sont exclus. Le choix des lots à exporter est indépendant des lots masqués.</p><h2 className="export-heading">Exporter les annotations</h2><div className="markup-actions"><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'note' && !m.needsReview)} onClick={() => exportReservations(true, true)}>{exporting ? 'Export en cours…' : 'Exporter annotations IFC'}</Button><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'note' && !m.needsReview) || !!(viewerPackage?.manifest.tiles && loadedZones < viewerPackage.manifest.tiles.length)} onClick={() => void exportBcf()}>{exporting ? 'Export en cours…' : 'Exporter annotations BCF'}</Button></div><p>Les annotations confirmées sont exportées en coordonnées partagées, avec leur texte et leur élément associé. Cet export est indépendant du filtre par lot des réservations.</p><Button size="sm" variant="ghost" onClick={() => setMarkupListOpen(false)}>Fermer</Button>{!allMarkups.length && <p>Aucune annotation enregistrée.</p>}{listedMarkups.map(mark => <button className="markup-list-item" style={{ color: lotDetails(reservationLot(mark), lotSettings).color, borderLeftColor: lotDetails(reservationLot(mark), lotSettings).color }} key={mark.id} onClick={() => { setActiveMarkup(mark.id); setMarkupListOpen(false); if (mark.needsReview) viewActionsRef.current?.frameMarkup(mark); }}><b>{(mark.kind === 'note' ? 'A.' : 'R.') + ' ' + lotDetails(reservationLot(mark), lotSettings).name} · {mark.elementName}</b><span>{mark.needsReview ? `⚠ À vérifier · ${mark.text || reservationLabel(mark)}` : mark.text || reservationLabel(mark)}</span></button>)}</aside>}
    <div className="viewer-context-bar" aria-label="Contexte de la maquette">{roomLocation && <span className="room-location" title={roomLocation.approximate ? 'Repérage par emprise et niveau, au-dessus de la hauteur déclarée Revit' : 'Pièce ou espace MEP exporté depuis Revit'}>{roomLocation.room.name}<small>{roomLocation.room.levelName}{roomLocation.approximate ? ' · ≈' : ''}</small></span>}<details className="context-lot"><summary aria-label="Choisir le lot actif"><i className="lot-swatch" style={{ background: lotDetails(currentLot, lotSettings).color }}/><span>{lots.find(lot => lot.id === currentLot)?.name || currentLot}</span><ChevronRight size={13}/></summary><div className="reservation-lot-toolbar"><label>Lot actif <select aria-label="Lot actif" value={currentLot} onChange={event => { setCurrentLot(event.target.value); setActiveMarkup(null); }}>{lots.map(lot => <option key={lot.id} value={lot.id}>{lot.name}</option>)}</select></label><span className="lot-swatch" style={{ background: lotDetails(currentLot, lotSettings).color }}/><label><input type="checkbox" checked={onlyCurrentLot} onChange={event => { setOnlyCurrentLot(event.target.checked); setActiveMarkup(null); }}/> Masquer les autres lots</label><button aria-expanded={lotsOpen} onClick={() => lotsOpen ? setLotsOpen(false) : openLots()}>Lots</button></div></details><details className="navigation-help"><summary aria-label="Aide de navigation">?</summary><div className={`view-hint${inputDevice === 'gamepad' ? ' gamepad-hint' : ''}`}>{inputDevice === 'gamepad' ? <>
      {gamepadMenu ? <><kbd>↑ ↓</kbd> Parcourir les champs · <kbd>← →</kbd> Régler · <kbd>LB</kbd> ×10 · <kbd className="xbox-a">A</kbd> Ouvrir / valider · <kbd>View ⧉</kbd> Tous les panneaux · <kbd className="xbox-b">B</kbd> Retour</> : draftMarkup && !pickingDimension ? <>
        <kbd>LS</kbd> {editMode === 'translate' ? 'Déplacer sur la face' : 'Largeur / hauteur'} · <kbd>↑ ↓ ← →</kbd> Ajuster de 1 cm · <kbd>LT</kbd> Précision (1 mm) · <kbd>RS</kbd> Tourner la vue · {draftMarkup.kind === 'reservation' && <><kbd>X</kbd> Déplacer / redimensionner · </>}<kbd>Menu ☰</kbd> Champs et enregistrement
      </> : <>
        {navigationMode === 'maquette' ? <>
          <kbd>LS</kbd> Marcher · <kbd>RS</kbd> Regarder · <kbd>LS clic</kbd> Courir en avançant · <kbd className="xbox-a">A</kbd> {flightMode ? 'Monter (maintenir)' : 'Sauter'} · <kbd className="xbox-b">B</kbd> {flightMode ? 'Descendre (maintenir)' : 'Accroupir (maintenir)'} · <kbd>RT</kbd> {pickingMeasure || pickingSection || pickingDimension || relocating ? 'Placer le point visé' : 'Sélectionner'} · <kbd>LT</kbd> Viser avec précision · <kbd className="xbox-x">X</kbd> Porte · <kbd className="xbox-y">Y</kbd> {flightMode ? 'Marcher' : 'Voler'} · <kbd>RS clic</kbd> Annuler · <kbd>↑</kbd> Carte · <kbd>←</kbd> Réapparaître ·
        </> : <>
          <kbd>LS</kbd> Déplacer la vue · <kbd>RS</kbd> Pivoter · <kbd>LT / RT</kbd> Zoom − / + · <kbd className="xbox-a">A</kbd> {pickingMeasure || pickingSection || pickingDimension || relocating ? 'Placer le point visé' : 'Sélectionner'} · <kbd className="xbox-b">B</kbd> Annuler · <kbd className="xbox-x">X</kbd> Cadrer · <kbd className="xbox-y">Y</kbd> Isométrique · <kbd>LB</kbd> Précision · <kbd>←</kbd> Isométrique ·
        </>} <kbd>RB</kbd> Actions · {selectedSection && <><kbd>LB + LT / RT</kbd> Déplacer la coupe · </>}<kbd>↓</kbd> Flux · <kbd>→</kbd> Cadrer · <kbd>View ⧉</kbd> Changer de mode · <kbd>Menu ☰</kbd> Interface
      </>}
    </> : navigationMode === 'orbit' ? `Clic : sélectionner · ${isEditor ? 'Clic droit : annoter / réservation · ' : ''}Double-clic / F : cadrer · Molette : zoom vers la souris · Glisser : regarder · Sélection : pivoter autour · Échap : libérer` : `Maintenir clic gauche : regarder · Clic droit : ${isEditor ? 'annoter / réservation' : 'inspecter'} · ZQSD / WASD : avancer · Espace : ${flightMode ? 'monter' : 'sauter'} · Maj : ${flightMode ? 'descendre' : 'courir'} · E : porte · F : ${flightMode ? 'revenir au sol' : 'voler'}`}</div></details>    {followingFlow && <div className="follow-banner" aria-label="Étape du parcours" style={{ borderLeftColor: followStep?.color || '#7cc8a3' }}>
      <header className="follow-card-heading"><div className="follow-step"><small>{followStep?.reverse ? 'REMONTÉE DU PARCOURS' : 'SUIVI DU FLUX'}{followPaused ? ' · EN PAUSE' : ''}</small><strong>{followStep?.network || 'Départ du parcours'}</strong></div><button className="follow-stop" onClick={() => viewActionsRef.current?.stopFollowing()}>Arrêter le suivi</button></header>
      {equipmentPause && <small className="equipment-pause-note" role="status">Ralentissement : {equipmentPause}</small>}
      <label className="equipment-stop-toggle"><input type="checkbox" checked={equipmentStops} onChange={event => setEquipmentStops(event.target.checked)}/> Ralentir aux équipements</label>
      <div className="follow-playback"><button onClick={() => { followPausedRef.current = !followPausedRef.current; setFollowPaused(followPausedRef.current); setEquipmentPause(null); }}>{followPaused ? 'Reprendre le suivi' : 'Mettre en pause'}</button><label>Vitesse<select aria-label="Vitesse du suivi" value={followSpeed} onChange={event => { const speed = Number(event.target.value); followSpeedRef.current = speed; setFollowSpeed(speed); }}><option value={.25}>0,25×</option><option value={.5}>0,5×</option><option value={1}>1×</option><option value={2}>2×</option></select></label></div>
      <details className="follow-options"><summary>Détails et options du parcours</summary><div className="follow-step"><span>{followStep?.element || 'Préparation du suivi…'}</span>{followStep && followStep.history.length > 1 && <p title={followStep.history.join(' → ')}>{followStep.history.join(' → ')}</p>}</div><label><input type="checkbox" checked={clearFollowView} onChange={event => setClearFollowView(event.target.checked)}/> Vue dégagée</label>{followBranches.length > 1 && <label>Relancer par une branche<select defaultValue="" onChange={event => { if (event.target.value) viewActionsRef.current?.chooseFollowBranch(event.target.value); }}><option value="">Branche automatique</option>{followBranches.map(branch => <option key={branch.key} value={branch.key}>{branch.name}</option>)}</select></label>}<small>Glisser : tourner · Molette : zoom · Échap : arrêter</small></details>
    </div>}
</div>
    {followEnd && <div className="flow-end-result" role="status"><strong>{followEnd.title}</strong><p>{followEnd.description}</p><small>{graph?.elements.find(element => element.key === followEnd.elementKey)?.name || followEnd.elementKey}</small><div><button disabled={!propertiesByKey.has(followEnd.elementKey)} onClick={() => { const property = propertiesByKey.get(followEnd.elementKey); if (property) { setSelectedIndex(property.index); setPanelCollapsed(false); } }}>Voir l’élément</button><button onClick={() => setFollowEnd(null)}>Fermer</button></div></div>}
    {lotsOpen && <ReservationLotsPanel lots={lots} editable={isEditor} onSave={persistLot} onClose={() => setLotsOpen(false)} onSaved={(id, created) => { if (!created) return; setCurrentLot(id); setDraftMarkup(current => current?.kind === 'reservation' ? { ...current, lot: id } : current); }}/>}
    {measureOpen && <MeasureControls gamepad={inputDevice === 'gamepad'} gamepadMode={navigationMode} mode={measureMode} onMode={mode => { setMeasureMode(mode); measureModeRef.current = mode; startMeasure(); }} measures={measures} picking={pickingMeasure} hasStart={!!measureStart} onStart={startMeasure} onCancel={cancelMeasure} onRemove={id => setMeasures(current => current.filter(item => item.id !== id))} onClear={() => { setMeasures([]); cancelMeasure(); }} onClose={() => { setMeasureOpen(false); cancelMeasure(); }}/> }
    <div ref={measureLabelsRef} className="markup-labels measure-labels">{measureOpen && measures.map((measure, index) => { const values = measureDistances(measure.start, measure.end); return <span key={measure.id} data-measure={measure.id} data-dimension="horizontal" className="measure-label"><b>Mesure {index + 1}</b><span>Directe {values.direct.toFixed(3)} m</span><span>Horiz. {values.horizontal.toFixed(3)} m · Vert. {values.vertical.toFixed(3)} m</span></span>; })}{measureOpen && measureStart && <span className="measure-label">Point A</span>}</div>
    {connectedPad && <div className="gamepad-status" role="status" title={connectedPad}>Manette connectée{inputDevice === 'keyboard' ? ' · Appuyez sur une touche de la manette' : gamepadMenu ? ' · Menu : croix pour naviguer, A pour valider, B pour revenir' : ' · Xbox'}</div>}
    {inputDevice === 'gamepad' && !gamepadMenu && <div className="gamepad-reticle" aria-hidden="true"/>}
    {loading && <div className="viewer-message"><LoaderCircle className="spin"/>Ouverture du partage…</div>}{error && <button className="viewer-message error" onClick={() => setError(null)}>{error}<small>Cliquer pour fermer</small></button>}</section>
    <aside className={`mep-panel ${panelCollapsed ? 'panel-collapsed' : ''} ${selectedProperty ? 'inspection-panel' : ''}`}>
      <button className="panel-minimize" title={panelCollapsed ? 'Développer le panneau MEP' : 'Réduire le panneau MEP'} aria-label={panelCollapsed ? 'Développer le panneau MEP' : 'Réduire le panneau MEP'} aria-expanded={!panelCollapsed} onClick={() => setPanelCollapsed(value => !value)}>{panelCollapsed ? <PanelLeft size={19} aria-hidden="true"/> : <PanelRight size={19} aria-hidden="true"/>}</button>
      {panelCollapsed && <span className="panel-rail-label">MEP</span>}
      {selectedProperty ? <>
        <div className="inspection-heading"><div><span>ÉLÉMENTS INSPECTÉS</span><small>1 élément</small></div><button onClick={() => setSelectedIndex(null)}>Retour</button></div>
        <div className="panel-separator"/>
        <article className="inspection-card">
          <header><h1>{details.name || 'Élément sans nom'}</h1><b>#{details.elementId}</b></header>
          <details open><summary>Informations Revit <ChevronRight/></summary><div className="revit-information"><p>Catégorie : {details.category || 'Non renseignée'}</p><p>Type : {details.typeName || 'Non renseigné'}</p><p>Niveau : {details.levelName || 'Non renseigné'}</p><p>Maquette : {details.documentTitle || viewerPackage?.manifest.documentTitle || 'Document actif'}</p></div></details>
          {selectedMepElement && <section className="selected-mep-information">
            <div className="panel-separator"/>
            <strong>Réseau MEP : {selectedMepElement.systemName || 'Non affecté'}{selectedMepElement.classification ? `  •  ${selectedMepElement.classification}` : ''}</strong>
            <p>État : {calculating ? 'Recalcul en cours' : connectionLabel(selectedMepElement)}{selectedPath ? <><br/>Sens : {canAnimate(selectedPath) ? selectedPath.flowForward ? 'début → fin (supposé)' : 'fin → début (supposé)' : 'non établi — flèches arrêtées'}</> : null}</p>
            {(directionExplanation || selectedPath?.directionReason) && <details className="network-understanding"><summary>Comprendre et suivre le réseau <ChevronRight/></summary><div className="direction-card"><b>Pourquoi ce sens ?</b><strong>{reliabilityLabel(directionExplanation?.reliability)}</strong><p>{selectedPath?.directionReason || directionExplanation?.rule || 'Sens déterminé à partir des connecteurs et des sources du réseau.'}</p>{directionExplanation?.primarySourceName && <p>Source principale : {directionExplanation.primarySourceName}</p>}{directionExplanation?.upstreamElementNames?.length ? <p>Amont : {directionExplanation.upstreamElementNames.slice(0, 7).join(' → ')}</p> : null}</div></details>}
            {selectedValve && <div className={`state-card ${isClosed ? 'closed' : ''}`}><span>Vanne {isClosed ? 'fermée' : 'ouverte'} · confiance {confidenceLabel(selectedValve.confidence)}</span><strong>{isClosed ? 'Fermée' : 'Ouverte'}</strong><p>Amont : {flowStateLabel(selectedValve.upstreamState)} · Aval : {flowStateLabel(selectedValve.downstreamState)}{selectedValve.detectionReason ? <><br/>Détection : {selectedValve.detectionReason}</> : null}</p><button disabled={saving || (!!viewerPackage && !isEditor)} onClick={viewerPackage ? toggleValve : () => setDemoValveClosed((value) => !value)}>{saving ? 'Synchronisation…' : !isEditor && viewerPackage ? 'Verrouillée en lecture seule' : isClosed ? 'Ouvrir la vanne' : 'Fermer la vanne'}</button></div>}
            <div className="quick-actions"><span>ACTIONS RAPIDES</span><button disabled={!selectedPath || !canAnimate(selectedPath)} onClick={() => viewActionsRef.current?.traceSelectedFlow()}>Suivre le flux</button><button disabled={!followingFlow && (!selectedPath || !canAnimate(selectedPath))} onClick={() => followingFlow ? viewActionsRef.current?.stopFollowing() : viewActionsRef.current?.followSelectedFlow()}>{followingFlow ? 'Arrêter la caméra' : 'Accompagner la vague'}</button><button onClick={() => viewActionsRef.current?.toggleLocalCut()}>{localCut ? 'Restaurer les coupes' : 'Coupe locale du réseau'}</button><button onClick={() => { setFlowsVisible(true); isolateSelectedSystem(); }}>{systemIsIsolated ? 'Afficher tous les systèmes' : 'Isoler ce système'}</button><button onClick={() => setFlowsVisible((value) => !value)}>{flowsVisible ? 'Masquer le flux' : 'Afficher le flux du réseau'}</button></div>
          </section>}
          {Object.keys(details.properties || {}).length > 0 && <details className="property-expander"><summary>Paramètres Revit <ChevronRight/></summary><dl>{Object.entries(details.properties).map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{value}</dd></div>)}</dl></details>}
        </article>
        {previousInspections.length > 0 && <details className="inspection-history"><summary>Éléments précédents ({previousInspections.length}) <ChevronRight/></summary>{previousInspections.map((item) => <button key={item.index} onClick={() => setSelectedIndex(item.index)}><span>{item.name || item.typeName}</span><small>#{item.elementId}</small></button>)}</details>}
      </> : <>
        <div className="plugin-panel-heading"><div><span>FLUIDES MEP</span><small>{graph ? `${graph.elements.length} éléments · ${systems.length} systèmes` : 'Analyse des canalisations'}</small></div><CircleGauge size={17}/></div>
        <div className="mep-tabs"><button className={flowsVisible ? 'active' : ''} onClick={() => setFlowsVisible((value) => !value)}>Flux · {flowsVisible ? 'ON' : 'OFF'}</button><button className={valveMarkersVisible ? 'active' : ''} onClick={() => setValveMarkersVisible((value) => !value)}>Vannes · {valveMarkersVisible ? 'ON' : 'OFF'}</button></div>
        <button className={`mep-water-view ${waterView ? 'active' : ''}`} aria-pressed={waterView} onClick={() => {
          setWaterView(!waterView); setModelOpacity(1);
          if (!waterView) setFlowsVisible(true);
        }} title="Afficher un volume d’eau translucide avec des vagues animées et les flèches de sens">{waterView ? 'Revenir au flux classique' : 'Voir dans les tuyaux'}</button>
        <div className="source-stats"><div><span>ARRIVÉES</span><b>{arrivalCount} active{arrivalCount > 1 ? 's' : ''}</b></div><div><span>RETOURS</span><b>{returnCount} actif{returnCount > 1 ? 's' : ''}</b></div></div>
        <details className="source-journeys"><summary>Parcourir depuis une arrivée ou un retour</summary>{activeSources.map((source, index) => {
          const reverse = source.boundaryKind === 1 || String(source.boundaryKind).toLowerCase() === 'outlet';
          const element = graph?.elements.find(element => element.key === source.elementKey);
          const systemName = graph?.systems.find(system => system.key === (source.systemKey || element?.systemKey))?.name || element?.systemName;
          return <button key={`${source.elementKey}-${index}`} disabled={!graph || flowTrace(graph, source.elementKey, reverse).size === 0} onClick={() => viewActionsRef.current?.followSelectedFlow(source.elementKey, reverse)}>{reverse ? 'Remonter depuis' : 'Partir de'} {systemName ? `${systemName} · ` : ''}{source.name || element?.name || source.elementKey} #{element?.elementId || source.elementKey}</button>;
        })}<small>Depuis un retour, le parcours remonte à rebours ; les flèches gardent le sens de circulation.</small></details>
        <p className="mep-command-status">{flowsVisible ? flowRenderStats.paths > 0 ? `${flowRenderStats.paths} parcours affiché(s) · ${flowRenderStats.arrows} flèche(s) animée(s).` : 'Aucun parcours exploitable dans cette publication.' : 'Les flux sont désactivés.'}</p>
        <button className="advanced-toggle" onClick={() => setAdvancedPanelOpen((value) => !value)}>Réglages et analyses <span>{advancedPanelOpen ? '−' : '+'}</span></button>
        {advancedPanelOpen && <section className="advanced-content"><div className="section-title"><span>SYSTÈMES AFFICHÉS</span><Eye size={14}/></div><div className="network-list">{systems.map((system, index) => <button key={system.key} onClick={() => toggleSystem(system.key)} className={hiddenSystems.has(system.key) ? 'network-hidden' : ''}><i style={{ background: colorOf(system, index) }}/><span>{system.name}<small>{system.elementCount || 0} éléments</small></span>{hiddenSystems.has(system.key) ? <EyeOff size={16}/> : <Eye size={16}/>}</button>)}</div><div className="panel-separator"/><span className="legend-title">LÉGENDE</span><p className="plugin-legend">Flux lumineux : connexion établie · Gris : isolé · Ambre : indéterminé<br/>Vannes : anneau vert ouverte · croix rouge fermée · ambre incertaine · blanc sélectionnée</p></section>}
        <p className="selection-instruction">{inputDevice === 'gamepad' ? `Vise un objet au centre de l’écran puis appuie sur ${navigationMode === 'maquette' ? 'RT' : 'A'} pour ouvrir sa fiche.` : 'Pointe un objet puis effectue un clic gauche pour ouvrir sa fiche complète.'}</p>
      </>}
    </aside>
    <footer className="statusbar"><output>{calculating ? "Recalcul des flux…" : calculationError || "Flux indicatifs · aucun débit ni pression calculés"}</output>{cutting && <output>Découpe en cours… vous pouvez continuer à naviguer</output>}<div><Waves size={14}/><span>Fluides</span><b className={flowsVisible ? '' : 'off'}>{flowsVisible ? 'ACTIFS' : 'ARRÊTÉS'}</b></div><div>{flightMode && navigationMode === 'maquette' ? <Plane size={14}/> : <Footprints size={14}/>}<span>MODE</span><b>{navigationMode === 'orbit' ? 'NAVIGATION WEB' : flightMode ? 'VOL LIBRE' : 'MARCHE'}</b></div><div><Users size={14}/><span>{viewerPackage?.manifest.tiles ? loadedZones + ' / ' + viewerPackage.manifest.tiles.length + ' zones chargées' : resolved ? 'Scénario synchronisé' : 'Aperçu local'}</span><i/></div></footer>
  </main>{opening && <div className="model-opening" aria-live="polite" aria-busy="true">
    <div className="model-opening-card"><LoaderCircle className="spin" aria-hidden="true"/>
      <h1>{loading ? 'Ouverture du partage…' : cutting ? 'Préparation des réservations…' : 'Chargement complet de la maquette…'}</h1>
      <p>La navigation sera disponible lorsque toute la maquette sera prête.</p>
      {!loading && viewerPackage?.manifest.tiles && <><progress aria-label="Géométrie préparée" value={loadedZones} max={viewerPackage.manifest.tiles.length}/><span>{loadedZones} / {viewerPackage.manifest.tiles.length} zones préparées</span></>}
      {error && <div className="opening-error" role="alert"><p>{error}</p><Button onClick={() => window.location.reload()}>Recharger la page</Button></div>}
    </div>
  </div>}</>;
}

