import * as THREE from 'three';
import { Brush, Evaluator, HalfEdgeMap, SUBTRACTION } from 'three-bvh-csg';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { markupQuaternion, type Markup } from './mep-markup';
import { reservationBounds } from './mep-reservation-volume';
import type { WebProperty } from './mep-contract';

export const isWall = (property?: WebProperty) => !!property && /^(murs?|walls?|ost_walls)$/i.test(property.category.trim());

// Copy only selected triangles, decoding normalized colors and preserving element IDs.
export function triangleSubset(source: THREE.BufferGeometry, accept: (id: number) => boolean) {
  const ids = source.getAttribute('_element') || source.getAttribute('_ELEMENT');
  const output = new THREE.BufferGeometry();
  const names = Object.keys(source.attributes);
  const arrays = names.map(() => [] as number[]);
  const count = source.index?.count ?? source.getAttribute('position').count;
  const vertex = (i: number) => source.index ? source.index.getX(i) : i;
  for (let i = 0; i < count; i += 3) {
    if (!accept(Math.round(ids.getX(vertex(i))))) continue;
    for (let corner = 0; corner < 3; corner++) names.forEach((name, attributeIndex) => {
      const attr = source.getAttribute(name); const v = vertex(i + corner);
      for (let c = 0; c < attr.itemSize; c++) arrays[attributeIndex].push(attr.getComponent(v, c));
    });
  }
  names.forEach((name, i) => output.setAttribute(name, new THREE.Float32BufferAttribute(arrays[i], source.getAttribute(name).itemSize)));
  return output;
}

export function cutWall(source: THREE.BufferGeometry, world: THREE.Matrix4, marks: Markup[], elementId: number) {
  const evaluator = new Evaluator(); evaluator.useGroups = false;
  evaluator.attributes = Object.keys(source.attributes);
  const idName = source.hasAttribute('_element') ? '_element' : '_ELEMENT';
  let geometry = source.clone();
  const material = new THREE.MeshBasicMaterial();
  try {
    // Revit can tessellate adjacent faces with different subdivisions (T-junctions).
    const halfEdges = new HalfEdgeMap() as HalfEdgeMap & { matchDisjointEdges: boolean; unmatchedEdges: number };
    halfEdges.matchDisjointEdges = true;
    halfEdges.updateFrom(geometry);
    if (halfEdges.unmatchedEdges > 0) throw new Error('La géométrie complète de ce mur comporte des faces manquantes. La découpe ne peut pas être calculée.');
    for (const mark of marks) {
      const rotation = markupQuaternion(mark.normal);
      const frame = new THREE.Matrix4().compose(new THREE.Vector3(...mark.position), rotation, new THREE.Vector3(1, 1, 1));
      // The chosen depth starts at the picked face and goes inward, in centimeters.
      const depth = mark.depthCm / 30.48;
      const box = new THREE.BoxGeometry(mark.widthCm / 30.48, mark.heightCm / 30.48, depth + .002);
      box.translate(0, 0, -depth / 2 + .001);
      box.applyMatrix4(world.clone().invert().multiply(frame));
      for (const name of evaluator.attributes) {
        if (name === 'position' || name === 'normal') continue;
        const attr = source.getAttribute(name); const values = [];
        for (let i = 0; i < box.getAttribute('position').count; i++) for (let c = 0; c < attr.itemSize; c++) values.push(name === idName ? elementId : attr.getComponent(0, c));
        box.setAttribute(name, new THREE.Float32BufferAttribute(values, attr.itemSize));
      }
      for (const name of Object.keys(box.attributes)) if (!evaluator.attributes.includes(name)) box.deleteAttribute(name);
      const a = new Brush(geometry, material), b = new Brush(box, material);
      a.updateMatrixWorld(); b.updateMatrixWorld();
      try {
        const result = evaluator.evaluate(a, b, SUBTRACTION);
        const next = result.geometry.index ? result.geometry.toNonIndexed() : result.geometry.clone();
        const start = result.geometry.drawRange.start;
        const length = Math.min(result.geometry.drawRange.count, next.getAttribute('position').count - start);
        const trimmed = new THREE.BufferGeometry();
        for (const name of evaluator.attributes) {
          const attr = next.getAttribute(name); const values = [];
          for (let i = start; i < start + length; i++) for (let c = 0; c < attr.itemSize; c++) values.push(name === idName ? elementId : attr.getComponent(i, c));
          trimmed.setAttribute(name, new THREE.Float32BufferAttribute(values, attr.itemSize));
        }
        next.dispose(); result.geometry.dispose(); geometry.dispose(); geometry = trimmed;
        if (Array.from(geometry.getAttribute('position').array).some(n => !Number.isFinite(n))) throw new Error('Découpe impossible sur ce mur.');
      } finally { a.disposeCacheData(); b.disposeCacheData(); box.dispose(); }
    }
    return geometry;
  } catch (error) { geometry.dispose(); throw error; }
  finally { material.dispose(); }
}

export class WallCutouts {
  private originals = new Map<THREE.Mesh, THREE.BufferGeometry>();
  private members = new Map<number, THREE.Mesh[]>();
  private signature = '';
  private bounds = new Map<number, THREE.Box3>();
  constructor(private meshes: THREE.Mesh[], private properties: Map<number, WebProperty>) {
    for (const mesh of meshes) {
      this.originals.set(mesh, mesh.geometry);
      const ids = mesh.geometry.getAttribute('_element') || mesh.geometry.getAttribute('_ELEMENT');
      if (!ids) continue;
      mesh.updateWorldMatrix(true, false);
      const positions = mesh.geometry.getAttribute('position');
      const point = new THREE.Vector3();
      for (let i = 0; i < positions.count; i++) {
        const id = Math.round(ids.getX(i));
        if (!isWall(this.properties.get(id))) continue;
        const bounds = this.bounds.get(id) || new THREE.Box3();
        bounds.expandByPoint(point.fromBufferAttribute(positions, i).applyMatrix4(mesh.matrixWorld)); this.bounds.set(id, bounds);
      }
      for (const id of new Set(Array.from(ids.array, value => Math.round(value)))) {
        const members = this.members.get(id) || []; members.push(mesh); this.members.set(id, members);
      }
    }
  }
  update(marks: Markup[]) {
    const cuts = new Map<number, Markup[]>();
    for (const [index, property] of this.properties) if (isWall(property)) {
      const matches = marks.filter(mark => mark.kind === 'reservation' && !!this.bounds.get(index)?.intersectsBox(reservationBounds(mark)) && mark.widthCm > 0 && mark.heightCm > 0 && mark.depthCm > 0);
      if (matches.length && this.members.has(index)) cuts.set(index, matches);
    }
    const signature = JSON.stringify([...cuts]);
    if (signature === this.signature) return;
    const output = new Map<THREE.Mesh, THREE.BufferGeometry[]>();
    const removed = new Set<number>();
    const errors: string[] = [];
    const replacements = new Map<THREE.Mesh, THREE.BufferGeometry>();
    try {
      for (const [id, reservations] of cuts) {
        const members = this.members.get(id)!;
        const host = members[0]; host.updateWorldMatrix(true, false);
        const inverse = host.matrixWorld.clone().invert();
        const fragments: THREE.BufferGeometry[] = [];
        let wall: THREE.BufferGeometry | null = null;
        try {
          for (const member of members) {
            member.updateWorldMatrix(true, false);
            const fragment = triangleSubset(this.originals.get(member)!, candidate => candidate === id);
            fragment.applyMatrix4(inverse.clone().multiply(member.matrixWorld));
            fragments.push(fragment);
          }
          wall = mergeGeometries(fragments, false);
          if (!wall) throw new Error('Les faces de ce mur ne peuvent pas être réunies.');
          const result = cutWall(wall, host.matrixWorld, reservations, id);
          const parts = output.get(host) || []; parts.push(result); output.set(host, parts);
          removed.add(id);
        } catch (error) { errors.push(error instanceof Error ? error.message : 'Découpe impossible sur ce mur.'); }
        finally { wall?.dispose(); fragments.forEach(fragment => fragment.dispose()); }
      }
      for (const mesh of this.meshes) {
        const original = this.originals.get(mesh)!;
        const ids = original.getAttribute('_element') || original.getAttribute('_ELEMENT');
        const affected = ids && Array.from(ids.array).some(id => removed.has(Math.round(id)));
        let replacement = original;
        if (affected) {
          const rest = triangleSubset(original, id => !removed.has(id));
          try {
            replacement = mergeGeometries([rest, ...(output.get(mesh) || [])], false)!;
            if (!replacement) throw new Error('Assemblage du mur impossible.');
            if (replacement.getAttribute('position').count) replacement.computeBoundsTree({ targetLeafSize: 24, indirect: true });
          } finally { rest.dispose(); }
        }
        replacements.set(mesh, replacement);
      }
      for (const [mesh, replacement] of replacements) {
        if (mesh.geometry === replacement) continue;
        const original = this.originals.get(mesh)!;
        if (mesh.geometry !== original) { mesh.geometry.disposeBoundsTree(); mesh.geometry.dispose(); }
        mesh.geometry = replacement;
        for (const child of mesh.children) if (child instanceof THREE.LineSegments) {
          child.geometry.dispose(); child.geometry = new THREE.EdgesGeometry(replacement, 22);
        }
      }
      this.signature = signature;
    } catch (error) {
      for (const [mesh, geometry] of replacements) if (geometry !== this.originals.get(mesh) && geometry !== mesh.geometry) geometry.dispose();
      throw error;
    } finally { for (const parts of output.values()) parts.forEach(part => part.dispose()); }
    if (errors.length) throw new Error([...new Set(errors)].join(' '));
  }
  dispose() {
    for (const [mesh, original] of this.originals) if (mesh.geometry !== original) {
      mesh.geometry.disposeBoundsTree(); mesh.geometry.dispose(); mesh.geometry = original;
    }
    this.originals.clear(); this.members.clear(); this.signature = '';
  }
}
