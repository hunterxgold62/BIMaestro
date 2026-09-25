'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowDown, ArrowUp, ArrowLeftRight, Scissors, ChartNoAxesCombined, MessageSquare, Box, ChevronRight, CircleGauge, Contrast, Eye, EyeOff, Footprints, Hand, Layers3, LoaderCircle, LockKeyhole, Pencil, Plane, RotateCcw, SunMedium, Users, Waves } from 'lucide-react';
import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { downloadExchange } from '@/lib/mep-exchange';
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
import { markupObject, disposeMarkups, reservationLot, reservationLabel, dimensionDistance, dimensionFoot, moveToDimension, type ReservationDimension, type Markup } from '@/lib/mep-markup';
import { ReservationFields } from './reservation-fields';
import { ReservationLotsPanel } from './reservation-lots';
import { MeasureControls } from './measure-controls';
import { lotDetails, reservationLots, type ReservationLot, type ReservationLots } from '@/lib/mep-lots';
import { measureDistances, measureObject, horizontalDimension, measureEnd, type MeasureMode, type TemporaryMeasure, type MeasurePoint } from '@/lib/mep-measure';
import { saveMarkup, readScenario, saveReservationLot } from '@/lib/mep-api';
import { Badge } from '@/components/ui/badge';
import type { MepGraph, MepSourceOverride, MepSystemColor, ResolvedShare, Scenario, ViewerPackage, WebProperty } from '@/lib/mep-contract';
import { canAnimate, connectionLabel } from '@/lib/mep-engine';
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
  phase: number;
  forward: boolean;
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
};
type ValveVisual = { marker: THREE.Mesh; elementKey: string };
type ModelMaterialState = {
  material: THREE.Material;
  opacity: number;
  transparent: boolean;
  depthWrite: boolean;
};
type ViewActions = {
  isometric: () => void;
  frameSelected: () => void;
  frameMarkup: (mark: Markup) => void;
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

function sampleFlowPath(animation: AnimatedFlowArrow, progress: number) {
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
  const controlsRef = useRef<OrbitControls | null>(null);
  const flowGroupRef = useRef<THREE.Group | null>(null);
  const valveGroupRef = useRef<THREE.Group | null>(null);
  const modelMaterialStatesRef = useRef<ModelMaterialState[]>([]);
  const flowFocusAppliedRef = useRef<boolean | null>(null);
  const flowsVisibleRef = useRef(false);
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
    if (controlsRef.current) controlsRef.current.enabled = mode === 'orbit';
    if (mode === 'maquette') viewActionsRef.current?.enterSpectator();
  }, []);

  useEffect(() => { modelOpacityRef.current = modelOpacity; viewActionsRef.current?.applyAppearance(); }, [modelOpacity]);
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
    const flowGroup = new THREE.Group(); flowGroup.name = 'Flux MEP'; flowGroup.visible = flowsVisibleRef.current; overlayScene.add(flowGroup); flowGroupRef.current = flowGroup;
    const valveGroup = new THREE.Group(); valveGroup.name = 'Vannes MEP'; valveGroup.visible = valveMarkersVisibleRef.current; overlayScene.add(valveGroup); valveGroupRef.current = valveGroup;
    const flowLineMaterials: LineMaterial[] = [];
    const animatedFlowArrows: AnimatedFlowArrow[] = [];
    const flowVisuals: FlowVisual[] = [];
    const valveVisuals: ValveVisual[] = [];
    const edgeMaterials: THREE.LineBasicMaterial[] = [];
    const applyFlowPresentation = () => {
      const focusedSystem = selectedSystemKeyRef.current;
      for (const visual of flowVisuals) {
        const focused = !focusedSystem || visual.systemKey === focusedSystem;
        const distance = camera.position.distanceTo(visual.center);
        const distanceFade = THREE.MathUtils.clamp(1.05 - distance / Math.max(modelSphere.radius * 2.35, 30), .42, 1);
        const emphasis = focusedSystem ? (focused ? 1 : .2) : .5;
        visual.material.opacity = visual.stateOpacity * emphasis * distanceFade;
        visual.material.linewidth = focusedSystem && focused ? 4 : focusedSystem ? 1.5 : 2.6;
        visual.haloMaterial.opacity = (focusedSystem && focused ? .34 : focusedSystem ? .05 : .14) * distanceFade;
        visual.haloMaterial.linewidth = focusedSystem && focused ? 6 : focusedSystem ? 2.5 : 4.2;
        visual.material.needsUpdate = true;
        visual.haloMaterial.needsUpdate = true;
      }
      for (const animation of animatedFlowArrows) {
        const focused = !focusedSystem || animation.systemKey === focusedSystem;
        const material = animation.mesh.material as THREE.MeshBasicMaterial;
        material.opacity = focusedSystem ? (focused ? .9 : .08) : .42;
        material.transparent = true;
        material.needsUpdate = true;
      }
    };
    const collisionGeometries = new Set<THREE.BufferGeometry>();
    let modelSphere = new THREE.Sphere(new THREE.Vector3(0, 2, 0), 8);
    const walkSpeed = 7.2, sprintSpeed = 18, crouchSpeed = 4.2, flySpeed = 15, flySprintSpeed = 26.24672;
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
      const elements = new Map(nextGraph.elements.map((element) => [element.key, element]));
      for (const visual of flowVisuals) {
        const path = elements.get(visual.elementKey)?.paths?.[visual.pathOrdinal];
        const state = typeof path?.flowState === 'string' ? path.flowState.toLowerCase() : path?.flowState;
        const supplied = state === 2 || state === 'supplied';
        const isolated = state === 1 || state === 'isolated';
        visual.stateOpacity = supplied && path?.hasCirculation ? .94 : isolated ? .58 : .72;
        const visible = !nextHiddenSystems.has(visual.systemKey) && (!stream || stream.complete(propertiesByKey.get(visual.elementKey)?.index ?? -1));
        visual.objects.forEach((object) => { object.visible = visible; });
      }
      applyFlowPresentation();
      for (const animation of animatedFlowArrows) {
        const path = elements.get(animation.elementKey)?.paths?.[animation.pathOrdinal];
        animation.mesh.visible = !nextHiddenSystems.has(animation.systemKey) && (!stream || stream.complete(propertiesByKey.get(animation.elementKey)?.index ?? -1)) &&
          canAnimate(path);
        animation.forward = path?.flowForward !== false;
      }
      const valves = new Map(nextGraph.valves.map((valve) => [valve.elementKey, valve]));
      for (const visual of valveVisuals) {
        const valve = valves.get(visual.elementKey);
        const material = visual.marker.material as THREE.MeshBasicMaterial;
        if (valve) material.color.set(valve.isClosed ? '#ef544a' : '#20d08b');
      }
    };
    viewActionsRef.current = {
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
          const haloMaterial = new LineMaterial({ color: '#07100e', linewidth: 4.2, transparent: true, opacity: .14, depthTest: true, depthWrite: false });
          haloMaterial.resolution.set(Math.max(mount.clientWidth, 1), Math.max(mount.clientHeight, 1)); flowLineMaterials.push(haloMaterial);
          const halo = new Line2(haloGeometry, haloMaterial); halo.computeLineDistances(); halo.renderOrder = 3000; halo.frustumCulled = false; halo.raycast = () => {}; flowGroup.add(halo);
          const geometry = new LineGeometry(); geometry.setPositions(positions);
          const material = new LineMaterial({ color, linewidth: 2.6, transparent: true, opacity: opacity * .5, depthTest: true, depthWrite: false });
          material.resolution.set(Math.max(mount.clientWidth, 1), Math.max(mount.clientHeight, 1)); flowLineMaterials.push(material);
          const line = new Line2(geometry, material); line.computeLineDistances(); line.renderOrder = 3001; line.frustumCulled = false; line.raycast = () => {}; flowGroup.add(line); renderedPathCount++;
          const center = points.reduce((sum, point) => sum.add(point), new THREE.Vector3()).multiplyScalar(1 / points.length);
          flowVisuals.push({ objects: [halo, line], material, haloMaterial, elementKey: element.key, pathOrdinal, systemKey: element.systemKey, center, stateOpacity: opacity });
          const cumulative = [0];
          for (let index = 1; index < points.length; index++) cumulative.push(cumulative[index - 1] + points[index].distanceTo(points[index - 1]));
          const length = cumulative[cumulative.length - 1];
          if (length > .02 && animatedFlowArrows.length < 1200) {
            const count = Math.max(1, Math.min(6, Math.ceil(length / 18)));
            for (let index = 0; index < count && animatedFlowArrows.length < 1200; index++) {
              const arrow = new THREE.Mesh(new THREE.ConeGeometry(.12, .48, 8), new THREE.MeshBasicMaterial({ color, transparent: true, opacity: .42, depthTest: true, depthWrite: false, toneMapped: false }));
              arrow.visible = canAnimate(path);
              arrow.renderOrder = 3002; arrow.frustumCulled = false; flowGroup.add(arrow);
              animatedFlowArrows.push({ mesh: arrow, elementKey: element.key, pathOrdinal, systemKey: element.systemKey, points, cumulative, length, phase: index / count, forward: path.flowForward !== false });
            }
          }
        }
      }
      statsTimer = window.setTimeout(() => {
        if (!disposed) setFlowRenderStats({ paths: renderedPathCount, arrows: animatedFlowArrows.length, fallbackPaths: fallbackPathCount });
      }, 0);
      if (initialGraph) for (const valve of initialGraph.valves.filter((item) => item.isEnabledAsValve)) {
        const element = initialGraph.elements.find((item) => item.key === valve.elementKey); const point = toWebPoint(element?.paths?.[0]?.points?.[0]); if (!point) continue;
        const marker = new THREE.Mesh(new THREE.TorusGeometry(.34, .09, 8, 24), new THREE.MeshBasicMaterial({ color: valve.isClosed ? '#ef544a' : '#20d08b', depthTest: false }));
        marker.position.copy(point); marker.renderOrder = 22; valveGroup.add(marker); valveVisuals.push({ marker, elementKey: valve.elementKey });
      }
      updateMepState(initialGraph, initialHiddenSystems);
      applyFlowPresentation();
    } else addDemo();

    const raycaster = new THREE.Raycaster(); raycaster.firstHitOnly = true; const pointer = new THREE.Vector2();
    const collisionRaycaster = new THREE.Raycaster(); collisionRaycaster.firstHitOnly = true;
    let pointerStart: { x: number; y: number; dragged: boolean; time: number } | null = null; let looking = false; let suppressContextMenu = false;
    let previousFrameTime = performance.now();
    let pickedAnchor: Pick<ContextMenuState, 'position' | 'normal' | 'depthCm'> | null = null;
    const selectAt = (event: { clientX: number; clientY: number }, contextOnly = false) => {
      pickedAnchor = null;
      const selectable = pickables.filter(isObjectVisible);
      if (!selectable.length) return null;
      const rect = renderer.domElement.getBoundingClientRect();
      activeSection = sectionPlanes(sectionRef.current);
      raycaster.firstHitOnly = activeSection.length === 0;
      pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1); raycaster.setFromCamera(pointer, camera);
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
      if (!sceneReady || gizmo.axis || draggingEdit) return;
      pointerStart = { x: event.clientX, y: event.clientY, dragged: false, time: performance.now() };
      suppressContextMenu = false;
      if (pickingSectionRef.current) return;
      const measurePivot = measurePickRef.current.picking && measurePickRef.current.start ? new THREE.Vector3(...measurePickRef.current.start) : null;
      const pivot = measurePickRef.current.picking ? measurePivot : selectedIndexRef.current !== null ? selectedPivot : null;
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
      if (simpleClick && event.button === 0) selectAt(event);
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
    const resetInput = () => { orbitPointer = null; pointerStart = null; looking = false; pressed.clear(); wheelMoveRemaining = 0; };
    const onKeyDown = (event: KeyboardEvent) => {
      if (!sceneReady) return;
      if (event.code === 'Escape' && measurePickRef.current.picking) { cancelMeasure(); event.preventDefault(); return; }
      if (event.code === 'Escape' && pickingDimensionRef.current) { setPickingDimension(false); pickingDimensionRef.current = false; event.preventDefault(); return; }
      if (event.code === 'Escape' && pickingSectionRef.current) { setPickingSection(false); event.preventDefault(); return; }
      if (event.code === 'Escape') { setActiveMarkup(null); setRelocating(null); setDraftMarkup(null); setContextMenu(null); selectedIndexRef.current = null; setSelectedIndex(null); }
      if (draftOpenRef.current || (event.target instanceof HTMLElement && event.target.closest('input,textarea,select,button,[contenteditable=true],.section-controls,.lots-panel,.measure-controls'))) { pressed.clear(); return; }
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
      if (!event.repeat && event.code === 'KeyE') {
        let nearest: DoorAnimation | null = null; let nearestDistance = 8;
        doors.forEach((door) => { const distance = door.center.distanceTo(camera.position); if (distance < nearestDistance) { nearest = door; nearestDistance = distance; } });
        if (nearest) (nearest as DoorAnimation).open = !(nearest as DoorAnimation).open;
        else setError(viewerPackage?.viewer ? 'Aucune porte assez proche.' : 'Republiez cette maquette avec la nouvelle version du plugin pour rendre les portes interactives.');
        event.preventDefault();
      }
      pressed.add(event.code);
      if (['KeyW', 'KeyZ', 'KeyA', 'KeyQ', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.code)) event.preventDefault();
    };
    const onKeyUp = (event: KeyboardEvent) => { pressed.delete(event.code); if (['KeyW', 'KeyZ', 'ArrowUp'].includes(event.code)) doubleTapSprint = false; };
    const onWheel = (event: WheelEvent) => {
      if (!sceneReady) { event.preventDefault(); return; }
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
    renderer.domElement.addEventListener('dblclick', onDoubleClick);
    renderer.domElement.addEventListener('pointerdown', onPointerDown, true); renderer.domElement.addEventListener('pointermove', onPointerMove, true); renderer.domElement.addEventListener('pointerup', onPointerUp); renderer.domElement.addEventListener('contextmenu', onContextMenu); renderer.domElement.addEventListener('wheel', onWheel, { passive: false, capture: true });
    const labelLayer = markupLabelsRef.current;
    labelLayer?.addEventListener('wheel', onWheel, { passive: false, capture: true });
    window.addEventListener('keydown', onKeyDown, { passive: false }); window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', resetInput); renderer.domElement.addEventListener('pointercancel', resetInput);
    const resize = () => {
      const width = mount.clientWidth, height = mount.clientHeight;
      camera.aspect = width / Math.max(height, 1); camera.updateProjectionMatrix(); renderer.setSize(width, height, false);
      flowLineMaterials.forEach((material) => material.resolution.set(Math.max(width, 1), Math.max(height, 1)));
    };
    const observer = new ResizeObserver(resize); observer.observe(mount); resize();
    let frame = 0, lastFlowUpdate = 0;
    let lastLabelVisibility = -Infinity;
    let qualityFrames = 0, qualityStart = performance.now();
    const animate = () => {
      frame = requestAnimationFrame(animate);
      if (draftOpenRef.current) { pressed.clear(); wheelMoveRemaining = 0; }
      const now = performance.now(); const delta = Math.min((now - previousFrameTime) / 1000, .05); previousFrameTime = now;
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
        lastSelection = selectedIndexRef.current;
        const property = lastSelection === null ? null : propertiesByIndex.get(lastSelection);
        const ids = new Set<number>(lastSelection === null ? [] : [lastSelection]);
        if (property) for (const p of propertiesByIndex.values()) if (p.selectionTargetKey === property.key) ids.add(p.index);
        selection.set(pickables.filter((m): m is THREE.Mesh => m instanceof THREE.Mesh), ids);
        selectedPivot = lastSelection === null || selection.bounds.isEmpty() ? null : selection.bounds.getCenter(new THREE.Vector3());

      }
      selection.sync();
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
      else if (!stream || webFlightMode || stream.readyAt(camera.position)) {
        let forwardInput = Number(pressed.has('KeyW') || pressed.has('KeyZ') || pressed.has('ArrowUp')) - Number(pressed.has('KeyS') || pressed.has('ArrowDown'));
        if (Math.abs(wheelMoveRemaining) > .000001) { const wheelStep = THREE.MathUtils.clamp(wheelMoveRemaining, -walkSpeed * delta, walkSpeed * delta); forwardInput = THREE.MathUtils.clamp(forwardInput + wheelStep / Math.max(.000001, walkSpeed * delta), -1, 1); wheelMoveRemaining -= wheelStep; }
        const rightInput = Number(pressed.has('KeyD') || pressed.has('ArrowRight')) - Number(pressed.has('KeyA') || pressed.has('KeyQ') || pressed.has('ArrowLeft'));
        const shift = pressed.has('ShiftLeft') || pressed.has('ShiftRight');
        const verticalInput = webFlightMode ? Number(pressed.has('Space')) - Number(shift) : 0;
        if (forwardInput || rightInput || verticalInput) {
          // Comme dans le repère du plugin, avancer/reculer ne modifie jamais
          // l'altimétrie, même lorsque la caméra regarde vers le haut ou le bas.
          // La hauteur en vol reste exclusivement pilotée par Espace et Maj.
          const forward = new THREE.Vector3(); camera.getWorldDirection(forward); forward.y = 0; if (forward.lengthSq() < .001) forward.set(0, 0, -1); else forward.normalize();
          const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize(); const movement = forward.multiplyScalar(forwardInput).add(right.multiplyScalar(rightInput)); if (movement.lengthSq() > 1) movement.normalize();
          const crouching = !webFlightMode && (pressed.has('ControlLeft') || pressed.has('ControlRight'));
          const sprint = (!webFlightMode && shift) || doubleTapSprint; const speed = crouching ? crouchSpeed : webFlightMode ? (sprint ? flySprintSpeed : flySpeed) : (sprint ? sprintSpeed : walkSpeed);
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
          const crouching = pressed.has('ControlLeft') || pressed.has('ControlRight'); const targetEyeHeight = crouching ? 2.72 : 5.28; currentEyeHeight = THREE.MathUtils.damp(currentEyeHeight, targetEyeHeight, 10, delta);
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
      if (flowGroup.visible && now - lastFlowUpdate >= 1000 / 30) {
        lastFlowUpdate = now;
        const seconds = now / 1000;
        for (const animation of animatedFlowArrows) {
          if (!animation.mesh.visible) continue;
          const rawProgress = animation.phase + seconds * 2.4 / animation.length * (animation.forward ? 1 : -1);
          const progress = rawProgress - Math.floor(rawProgress);
          const sample = sampleFlowPath(animation, progress);
          animation.mesh.position.copy(sample.position);
          const direction = animation.forward ? sample.direction : sample.direction.multiplyScalar(-1);
          animation.mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction);
          const scale = THREE.MathUtils.clamp(camera.position.distanceTo(sample.position) * .009, .65, 3.2);
          animation.mesh.scale.setScalar(scale);
        }
      }
      if (flowGroup.visible && frame % 8 === 0) applyFlowPresentation();
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
      if (flowGroup.visible || valveGroup.visible) {
        renderer.clearDepth();
        if (buildingVisibility) renderer.render(buildingVisibility.scene, camera);
        renderer.render(overlayScene, camera);
      }
      if (!openingReleased) { openingReleased = true; setOpening(false); }
    };
    animate();
    return () => {
      disposed = true; preparation?.dispose();
      stream?.dispose(); streamRef.current = null; updateCutoutsRef.current = null; window.clearTimeout(geometryTimer);
      buildingVisibility?.dispose();
      cutoutsRef.current?.dispose(); cutoutsRef.current = null;
      disposeMarkups(measureGroup); disposeMarkups(markupGroup); markupGroupRef.current = null;
      gizmo.dispose(); disposeMarkups(editRoot); selection.dispose(); renderer.domElement.removeEventListener('dblclick', onDoubleClick);

      disposed = true; cancelAnimationFrame(frame); observer.disconnect();
      if (statsTimer !== null) window.clearTimeout(statsTimer);
      renderer.domElement.removeEventListener('pointerdown', onPointerDown, true); renderer.domElement.removeEventListener('pointermove', onPointerMove, true); renderer.domElement.removeEventListener('pointerup', onPointerUp); renderer.domElement.removeEventListener('contextmenu', onContextMenu); renderer.domElement.removeEventListener('wheel', onWheel, true);
      labelLayer?.removeEventListener('wheel', onWheel, true);
      window.removeEventListener('keydown', onKeyDown); window.removeEventListener('keyup', onKeyUp);
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
  }, [viewerPackage, propertiesByIndex, propertiesByKey, applyFlowFocus, resolved]);

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
  return <><main className="viewer-shell" inert={opening}>
    {draftMarkup && <section className="markup-editor" aria-label="Modifier l’annotation">
        <strong>{draftMarkup.kind === 'note' ? 'Annotation' : 'Réservation'}</strong>
        <p>{draftMarkup.elementName}</p>
        <div className="markup-actions"><Button size="sm" variant={editMode === 'translate' ? 'default' : 'outline'} onClick={() => setEditMode('translate')}>Déplacer</Button>{draftMarkup.kind === 'reservation' && <Button size="sm" variant={editMode === 'scale' ? 'default' : 'outline'} onClick={() => setEditMode('scale')}>Redimensionner</Button>}</div>
        <p>Glissez les poignées sur la face. La découpe sera calculée après « Enregistrer ».</p>
        {draftMarkup && <form onSubmit={event => { event.preventDefault(); void persistMarkup(draftMarkup); }}>
          <label>{draftMarkup.kind === 'note' ? 'Votre annotation' : 'Commentaire (facultatif)'}<textarea required={draftMarkup.kind === 'note'} maxLength={2000} value={draftMarkup.text} onChange={event => setDraftMarkup({ ...draftMarkup, text: event.target.value })}/></label>
          {draftMarkup.kind === 'reservation' && <><ReservationFields mark={draftMarkup} lots={lots} onChange={setDraftMarkup} onManageLots={openLots} picking={pickingDimension} onPick={() => { cancelMeasure(); setMeasureOpen(false); setPickingDimension(value => !value); setPickingSection(false); setMarkupError(null); }}/><p>{draftIsHost ? 'La profondeur est ajustée à l’épaisseur détectée du mur ou du sol. Vous pouvez la modifier pour une réservation partielle.' : 'La profondeur part de la face sélectionnée vers l’intérieur. Les murs et les sols peuvent être découpés.'}</p></>}
          {markupError && <p role="alert" className="markup-error">{markupError}</p>}
          <div className="markup-actions"><Button type="button" variant="outline" disabled={saving} onClick={() => setDraftMarkup(null)}>Annuler</Button><Button type="submit" disabled={saving || (draftMarkup.kind === 'note' && !draftMarkup.text.trim())}>{saving ? 'Enregistrement…' : 'Enregistrer'}</Button></div>
        </form>}
      </section>}
    {impactOpen && <MepImpactPanel graph={graph} reportText={reportText} calculating={calculating} error={calculationError} isEditor={isEditor} token={shareToken} scenario={scenario} publicationRevision={resolved?.publication.revision} selectedKey={selectedProperty?.key} onScenario={next => setScenario(current => !current || next.revision >= current.revision ? next : current)} onClose={() => setImpactOpen(false)} onReference={setReference} onSelect={key => { const property = propertiesByKey.get(key); if (property) { setSelectedIndex(property.index); requestAnimationFrame(() => viewActionsRef.current?.frameSelected()); } }}/>}
    <header className="topbar"><div className="brand-mark"><Waves size={19}/><span>BIMaestro</span><b>Maquette 3D</b></div><div className="model-title"><span>{resolved?.publication.name || 'Démonstration · Local technique'}</span><Badge variant="outline">Révision {resolved?.publication.revision || 'démo'}</Badge></div><div className={`access-badge ${isEditor ? 'editor' : 'reader'}`}>{isEditor ? <Pencil size={15}/> : <LockKeyhole size={15}/>}<span><small>MODE DU LIEN</small>{isEditor ? 'ÉDITION COLLABORATIVE' : 'CONSULTATION · LECTURE SEULE'}</span></div></header>
    <section className="viewport">{sectionOpen && <SectionControls value={section} onChange={setSection} selected={selectedSection} onSelect={setSelectedSection} picking={pickingSection} onPick={value => { cancelMeasure(); setPickingDimension(false); setPickingSection(value); }} extent={sectionExtent}/>}<div ref={mountRef} className="three-mount"/><div className="view-toolbar"><Button size="sm" variant="secondary" onClick={() => viewActionsRef.current?.isometric()}><Box/> Isométrique</Button><Button size="sm" variant="secondary" aria-expanded={sectionOpen} onClick={() => { setLotsOpen(false); setMarkupListOpen(false); setMeasureOpen(false); cancelMeasure(); setPickingDimension(false); setSectionOpen(value => !value); if (!sectionOpen && section.length === 0) setPickingSection(true); else setPickingSection(false); }}><Scissors/> Coupe{section.length ? ' · ' + section.length : ''}</Button><Button size="sm" variant="secondary" disabled={!!draftMarkup} aria-expanded={measureOpen} onClick={() => { setMeasureOpen(value => !value); setSectionOpen(false); setPickingSection(false); setLotsOpen(false); setMarkupListOpen(false); if (!measureOpen) startMeasure(); else cancelMeasure(); }}><ArrowLeftRight/> Mesurer</Button><Button size="sm" variant="secondary" onClick={() => setImpactOpen(v => !v)}><ChartNoAxesCombined/> Analyser les coupes</Button><Button size="sm" variant="secondary" onClick={() => { setMarkupListOpen(v => !v); setLotsOpen(false); setSectionOpen(false); setPickingSection(false); setMeasureOpen(false); cancelMeasure(); }}><MessageSquare/> Annotation et Résa ({allMarkups.length}){reviewCount > 0 ? ` · ⚠ ${reviewCount} à vérifier` : ''}</Button></div><div className="reservation-lot-toolbar"><label>Lot actif <select aria-label="Lot actif" value={currentLot} onChange={event => { setCurrentLot(event.target.value); setActiveMarkup(null); }}>{lots.map(lot => <option key={lot.id} value={lot.id}>{lot.name}</option>)}</select></label><span className="lot-swatch" style={{ background: lotDetails(currentLot, lotSettings).color }}/><label><input type="checkbox" checked={onlyCurrentLot} onChange={event => { setOnlyCurrentLot(event.target.checked); setActiveMarkup(null); }}/> Masquer les autres lots</label><button aria-expanded={lotsOpen} onClick={() => lotsOpen ? setLotsOpen(false) : openLots()}>Lots</button></div><div className="appearance-toolbar" aria-label="Réglages de rendu"><button className={realisticLighting ? 'active' : ''} onClick={() => setRealisticLighting((value) => !value)} title="Basculer entre le mode ombré du plugin et les couleurs uniformes"><SunMedium/><span>{realisticLighting ? 'Ombré' : 'Uniforme'}</span></button><button className={highContrast ? 'active' : ''} onClick={() => setHighContrast((value) => !value)} title="Renforcer les contours et le contraste"><Contrast/><span>Contraste</span></button><button className={modelOpacity < 1 ? 'active' : ''} onClick={cycleOpacity} title="Faire varier la transparence de la maquette"><Layers3/><span>{Math.round(modelOpacity * 100)} %</span></button></div><div className="navigation-switch" aria-label="Mode de navigation"><button className={navigationMode === 'orbit' ? 'active' : ''} onClick={() => changeNavigationMode('orbit')}><Hand size={15}/><span>Navigation web</span></button><button className={navigationMode === 'maquette' ? 'active' : ''} onClick={() => changeNavigationMode('maquette')}><Footprints size={15}/><span>Maquette 3D</span></button></div>{navigationMode === 'maquette' && <div className="flight-controls" aria-label="Commandes de déplacement vertical"><button className={flightMode ? 'active' : ''} onClick={() => viewActionsRef.current?.toggleFlight()} title={flightMode ? 'Revenir au mode marche' : 'Activer le vol libre'}><Plane/><span>{flightMode ? 'Quitter le vol' : 'Vol libre'}</span></button>{flightMode && <><button aria-label="Monter" title="Monter" onPointerDown={() => holdInput('Space', true)} onPointerUp={() => holdInput('Space', false)} onPointerCancel={() => holdInput('Space', false)} onPointerLeave={() => holdInput('Space', false)}><ArrowUp/><span>Monter</span></button><button aria-label="Descendre" title="Descendre" onPointerDown={() => holdInput('ShiftLeft', true)} onPointerUp={() => holdInput('ShiftLeft', false)} onPointerCancel={() => holdInput('ShiftLeft', false)} onPointerLeave={() => holdInput('ShiftLeft', false)}><ArrowDown/><span>Descendre</span></button></>}<button onClick={() => viewActionsRef.current?.enterSpectator()} title="Revenir au point de départ"><RotateCcw/><span>Réapparaître</span></button></div>}{contextMenu && contextProperty && <div className="element-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} role="menu"><header><span>{contextProperty.name}</span><small>#{contextProperty.elementId}</small></header><button disabled={section.length >= maximumSections} onClick={sectionFromContext}>Faire une coupe</button>{isEditor && <><button onClick={() => beginMarkup('note')}>Annoter ici</button><button onClick={() => beginMarkup('reservation')}>Créer une réservation ici</button>{contextMepElement && <>{contextValve && <button disabled={saving} onClick={() => void runScenarioEdit(contextMepElement, 'valve', !contextValve.isClosed)}>{contextValve.isClosed ? 'Ouvrir la vanne' : 'Fermer la vanne'}</button>}<button disabled={saving} className={contextSourceKind === 'inlet' ? 'active' : ''} onClick={() => void runScenarioEdit(contextMepElement, 'source', 'inlet')}>{contextSourceKind === 'inlet' ? '✓ Départ du réseau' : 'Définir comme départ'}</button><button disabled={saving} className={contextSourceKind === 'outlet' ? 'active' : ''} onClick={() => void runScenarioEdit(contextMepElement, 'source', 'outlet')}>{contextSourceKind === 'outlet' ? '✓ Retour du réseau' : 'Définir comme retour'}</button>{contextSourceKind && <button disabled={saving} className="danger" onClick={() => void runScenarioEdit(contextMepElement, 'source', 'none')}>Retirer {contextSourceKind === 'inlet' ? 'le départ' : 'le retour'}</button>}</>}</>}<button onClick={() => setContextMenu(null)}>Fermer</button></div>}<div ref={markupLabelsRef} className="markup-labels">{visibleMarkups.map(mark => <button key={mark.id} data-markup={mark.id} className={`markup-label ${mark.kind}`} style={{ borderColor: lotDetails(reservationLot(mark), lotSettings).color, color: lotDetails(reservationLot(mark), lotSettings).color, background: "white" }} onClick={() => { setActiveMarkup(mark.id); setMarkupError(null); }} title={mark.text || 'Réservation'}>{mark.kind === 'note' ? 'A.' : 'R.'} <span>{mark.kind === 'note' ? mark.text : `${lotDetails(reservationLot(mark), lotSettings).name} · ${reservationLabel(mark)}`}</span></button>)}{dimensionMarks.flatMap(mark => (mark.dimensions || []).map(dimension => <span key={mark.id + dimension.id} data-markup={mark.id} data-dimension={dimension.id} className="markup-label dimension-label">{dimensionDistance(mark, dimension).toFixed(1)} cm</span>))}</div>{activeMark && <div className="markup-card"><strong>{activeMark.kind === 'note' ? 'Annotation' : 'Réservation'}</strong><small>{activeMark.elementName}</small>{activeMark.needsReview && <p aria-live="polite">{activeMark.reviewCandidate ? 'Position proposée sur le support retrouvé. Vérifiez la position et les cotations, puis confirmez. Les autres réservations sont temporairement masquées.' : 'Ancienne position affichée. Le support n’a pas été retrouvé avec certitude : choisissez une nouvelle face. Les autres réservations sont temporairement masquées.'}</p>}<p>{activeMark.text}</p>{activeMark.kind === 'reservation' && <p>{lotDetails(reservationLot(activeMark), lotSettings).name} · {reservationLabel(activeMark)} · Profondeur {activeMark.depthCm} cm</p>}{activeMark.dimensions?.map(dimension => <p key={dimension.id}>{dimension.elementName} · Axe : {dimensionDistance(activeMark, dimension).toFixed(1)} cm</p>)}{markupError && <p role="alert">{markupError}</p>}{isEditor && <>{activeMark.needsReview && activeMark.reviewCandidate && <Button size="sm" disabled={saving} onClick={() => void persistMarkup(activeMark)}>Confirmer cette position et les cotations</Button>}<Button size="sm" onClick={() => { setEditMode("translate"); if (activeMark.needsReview) setRelocating(activeMark); else setDraftMarkup({ ...activeMark }); setActiveMarkup(null); }}>Modifier / déplacer</Button>{activeMark.kind === 'note' && <Button size="sm" onClick={() => { setRelocating(activeMark); setActiveMarkup(null); }}>Replacer sur une face</Button>}<Button size="sm" variant="outline" disabled={saving} onClick={() => void persistMarkup(activeMark, true)}>Supprimer</Button></>}<Button size="sm" variant="ghost" onClick={() => setActiveMarkup(null)}>Fermer</Button></div>}{relocating && <div className="relocation-hint">Cliquez sur la nouvelle face pour replacer « {relocating.text || relocating.elementName} ». Échap pour annuler.</div>}
    {markupListOpen && <aside className="markup-list" aria-label="Annotations et réservations"><strong>Annotations et réservations</strong><h2 className="export-heading">Exporter les réservations</h2>{viewerPackage && !viewerPackage.manifest.sharedCoordinates && <p role="status">Pour exporter un IFC en coordonnées partagées, republiez cette maquette depuis Revit avec le plugin corrigé.</p>}<label className="export-lot">Lots à exporter<select value={exportLot} onChange={event => setExportLot(event.target.value)}><option value="">Tous les lots</option>{lots.map(lot => <option key={lot.id} value={lot.id}>{lot.name}</option>)}</select></label><div className="markup-actions"><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'reservation' && !m.needsReview && (!exportLot || reservationLot(m) === exportLot))} onClick={() => exportReservations(true)}>{exporting ? 'Export en cours…' : 'Exporter IFC'}</Button><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'reservation' && !m.needsReview && (!exportLot || reservationLot(m) === exportLot))} onClick={() => exportReservations(false)}>Exporter vers Revit</Button></div><p>IFC : coordonnées partagées du site actif Revit. Export vers Revit : repère interne. Les points à vérifier sont exclus. Le choix des lots à exporter est indépendant des lots masqués.</p><h2 className="export-heading">Exporter les annotations</h2><div className="markup-actions"><Button size="sm" variant="outline" disabled={exporting || !allMarkups.some(m => m.kind === 'note' && !m.needsReview)} onClick={() => exportReservations(true, true)}>{exporting ? 'Export en cours…' : 'Exporter annotations IFC'}</Button></div><p>Les annotations confirmées sont exportées en coordonnées partagées, avec leur texte et leur élément associé. Cet export est indépendant du filtre par lot des réservations.</p><Button size="sm" variant="ghost" onClick={() => setMarkupListOpen(false)}>Fermer</Button>{!allMarkups.length && <p>Aucune annotation enregistrée.</p>}{listedMarkups.map(mark => <button className="markup-list-item" style={{ color: lotDetails(reservationLot(mark), lotSettings).color, borderLeftColor: lotDetails(reservationLot(mark), lotSettings).color }} key={mark.id} onClick={() => { setActiveMarkup(mark.id); setMarkupListOpen(false); if (mark.needsReview) viewActionsRef.current?.frameMarkup(mark); }}><b>{(mark.kind === 'note' ? 'A.' : 'R.') + ' ' + lotDetails(reservationLot(mark), lotSettings).name} · {mark.elementName}</b><span>{mark.needsReview ? `⚠ À vérifier · ${mark.text || reservationLabel(mark)}` : mark.text || reservationLabel(mark)}</span></button>)}</aside>}
    {lotsOpen && <ReservationLotsPanel lots={lots} editable={isEditor} onSave={persistLot} onClose={() => setLotsOpen(false)} onSaved={(id, created) => { if (!created) return; setCurrentLot(id); setDraftMarkup(current => current?.kind === 'reservation' ? { ...current, lot: id } : current); }}/>}
    {measureOpen && <MeasureControls mode={measureMode} onMode={mode => { setMeasureMode(mode); measureModeRef.current = mode; startMeasure(); }} measures={measures} picking={pickingMeasure} hasStart={!!measureStart} onStart={startMeasure} onCancel={cancelMeasure} onRemove={id => setMeasures(current => current.filter(item => item.id !== id))} onClear={() => { setMeasures([]); cancelMeasure(); }} onClose={() => { setMeasureOpen(false); cancelMeasure(); }}/> }
    <div ref={measureLabelsRef} className="markup-labels measure-labels">{measureOpen && measures.map((measure, index) => { const values = measureDistances(measure.start, measure.end); return <span key={measure.id} data-measure={measure.id} data-dimension="horizontal" className="measure-label"><b>Mesure {index + 1}</b><span>Directe {values.direct.toFixed(3)} m</span><span>Horiz. {values.horizontal.toFixed(3)} m · Vert. {values.vertical.toFixed(3)} m</span></span>; })}{measureOpen && measureStart && <span className="measure-label">Point A</span>}</div>
    <div className="view-hint">{navigationMode === 'orbit' ? `Clic : sélectionner · ${isEditor ? 'Clic droit : annoter / réservation · ' : ''}Double-clic / F : cadrer · Molette : zoom vers la souris · Glisser : regarder · Sélection : pivoter autour · Échap : libérer` : `Maintenir clic gauche : regarder · Clic droit : ${isEditor ? 'annoter / réservation' : 'inspecter'} · ZQSD / WASD : avancer · Espace : ${flightMode ? 'monter' : 'sauter'} · Maj : ${flightMode ? 'descendre' : 'courir'} · E : porte · F : ${flightMode ? 'revenir au sol' : 'voler'}`}</div>{loading && <div className="viewer-message"><LoaderCircle className="spin"/>Ouverture du partage…</div>}{error && <button className="viewer-message error" onClick={() => setError(null)}>{error}<small>Cliquer pour fermer</small></button>}</section>
    <aside className={`mep-panel ${selectedProperty ? 'inspection-panel' : ''}`}>
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
            <div className="quick-actions"><span>ACTIONS RAPIDES</span><button onClick={() => { setFlowsVisible(true); isolateSelectedSystem(); }}>{systemIsIsolated ? 'Afficher tous les systèmes' : 'Isoler ce système'}</button><button onClick={() => setFlowsVisible((value) => !value)}>{flowsVisible ? 'Masquer le flux' : 'Afficher le flux du réseau'}</button></div>
          </section>}
          {Object.keys(details.properties || {}).length > 0 && <details className="property-expander"><summary>Paramètres Revit <ChevronRight/></summary><dl>{Object.entries(details.properties).map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{value}</dd></div>)}</dl></details>}
        </article>
        {previousInspections.length > 0 && <details className="inspection-history"><summary>Éléments précédents ({previousInspections.length}) <ChevronRight/></summary>{previousInspections.map((item) => <button key={item.index} onClick={() => setSelectedIndex(item.index)}><span>{item.name || item.typeName}</span><small>#{item.elementId}</small></button>)}</details>}
      </> : <>
        <div className="plugin-panel-heading"><div><span>FLUIDES MEP</span><small>{graph ? `${graph.elements.length} éléments · ${systems.length} systèmes` : 'Analyse des canalisations'}</small></div><CircleGauge size={17}/></div>
        <div className="mep-tabs"><button className={flowsVisible ? 'active' : ''} onClick={() => setFlowsVisible((value) => !value)}>Flux · {flowsVisible ? 'ON' : 'OFF'}</button><button className={valveMarkersVisible ? 'active' : ''} onClick={() => setValveMarkersVisible((value) => !value)}>Vannes · {valveMarkersVisible ? 'ON' : 'OFF'}</button></div>
        <div className="source-stats"><div><span>ARRIVÉES</span><b>{arrivalCount} active{arrivalCount > 1 ? 's' : ''}</b></div><div><span>RETOURS</span><b>{returnCount} actif{returnCount > 1 ? 's' : ''}</b></div></div>
        <p className="mep-command-status">{flowsVisible ? flowRenderStats.paths > 0 ? `${flowRenderStats.paths} parcours affiché(s) · ${flowRenderStats.arrows} flèche(s) animée(s).` : 'Aucun parcours exploitable dans cette publication.' : 'Les flux sont désactivés.'}</p>
        <button className="advanced-toggle" onClick={() => setAdvancedPanelOpen((value) => !value)}>Réglages et analyses <span>{advancedPanelOpen ? '−' : '+'}</span></button>
        {advancedPanelOpen && <section className="advanced-content"><div className="section-title"><span>SYSTÈMES AFFICHÉS</span><Eye size={14}/></div><div className="network-list">{systems.map((system, index) => <button key={system.key} onClick={() => toggleSystem(system.key)} className={hiddenSystems.has(system.key) ? 'network-hidden' : ''}><i style={{ background: colorOf(system, index) }}/><span>{system.name}<small>{system.elementCount || 0} éléments</small></span>{hiddenSystems.has(system.key) ? <EyeOff size={16}/> : <Eye size={16}/>}</button>)}</div><div className="panel-separator"/><span className="legend-title">LÉGENDE</span><p className="plugin-legend">Flux lumineux : connexion établie · Gris : isolé · Ambre : indéterminé<br/>Vannes : anneau vert ouverte · croix rouge fermée · ambre incertaine · blanc sélectionnée</p></section>}
        <p className="selection-instruction">Pointe un objet puis effectue un clic gauche pour ouvrir sa fiche complète.</p>
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
