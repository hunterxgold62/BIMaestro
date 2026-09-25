import * as THREE from 'three';
import { triangleSubset } from './mep-cutouts';
import type { WebProperty } from './mep-contract';

export function blocksView(property?: WebProperty) {
  const category = property?.category.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase() || '';
  return /mur|wall|sols|floor|dalle|plafond|ceiling|toit|roof|porte|door|poteau|column|ossature|framing|escalier|stair|fondation|foundation|panneau.*rideau|curtain.*panel/.test(category);
}

export function elementCenter(meshes: THREE.Mesh[], index: number) {
  const bounds = new THREE.Box3(); const point = new THREE.Vector3();
  for (const mesh of meshes) {
    const ids = mesh.geometry.getAttribute('_element') || mesh.geometry.getAttribute('_ELEMENT');
    if (!ids) continue;
    mesh.updateWorldMatrix(true, false);
    const positions = mesh.geometry.getAttribute('position');
    for (let i = 0; i < positions.count; i++) if (Math.round(ids.getX(i)) === index) bounds.expandByPoint(point.fromBufferAttribute(positions, i).applyMatrix4(mesh.matrixWorld));
  }
  return bounds.isEmpty() ? null : bounds.getCenter(new THREE.Vector3());
}

// A separate depth pass masks flows behind architecture, without hiding them inside pipes.
export class BuildingVisibility {
  readonly scene = new THREE.Scene();
  private entries = new Map<THREE.Mesh, { source: THREE.BufferGeometry; proxy: THREE.Mesh }>();
  private material = new THREE.MeshBasicMaterial({ colorWrite: false, depthWrite: true, depthTest: true, side: THREE.DoubleSide });
  private ray = new THREE.Raycaster();
  private blockers: Set<number>;
  constructor(private meshes: THREE.Mesh[], properties: Map<number, WebProperty>) {
    this.blockers = new Set([...properties].filter(([, property]) => blocksView(property)).map(([index]) => index));
    this.ray.firstHitOnly = true;
  }
  setMeshes(meshes: THREE.Mesh[]) {
    const retained = new Set(meshes);
    for (const [mesh, entry] of this.entries) if (!retained.has(mesh)) {
      entry.proxy.geometry.disposeBoundsTree(); entry.proxy.geometry.dispose(); this.scene.remove(entry.proxy); this.entries.delete(mesh);
    }
    this.meshes = meshes;
  }
  replace(mesh: THREE.Mesh, geometry: THREE.BufferGeometry) {
    const previous = this.entries.get(mesh);
    if (previous) { previous.proxy.geometry.disposeBoundsTree(); previous.proxy.geometry.dispose(); this.scene.remove(previous.proxy); }
    const proxy = new THREE.Mesh(geometry, this.material); proxy.matrixAutoUpdate = false;
    this.entries.set(mesh, { source: mesh.geometry, proxy }); this.scene.add(proxy);
  }
  sync() {
    for (const mesh of this.meshes) {
      if (!mesh.geometry.hasAttribute('_element') && !mesh.geometry.hasAttribute('_ELEMENT')) continue;
      let entry = this.entries.get(mesh);
      if (!entry || entry.source !== mesh.geometry) {
        if (entry) { entry.proxy.geometry.disposeBoundsTree(); entry.proxy.geometry.dispose(); this.scene.remove(entry.proxy); }
        const geometry = triangleSubset(mesh.geometry, id => this.blockers.has(id));
        if (geometry.getAttribute('position').count) geometry.computeBoundsTree({ targetLeafSize: 24, indirect: true });
        const proxy = new THREE.Mesh(geometry, this.material); proxy.matrixAutoUpdate = false;
        entry = { source: mesh.geometry, proxy }; this.entries.set(mesh, entry); this.scene.add(proxy);
      }
      mesh.updateWorldMatrix(true, false);
      entry.proxy.matrix.copy(mesh.matrixWorld); entry.proxy.matrixWorld.copy(mesh.matrixWorld);
      entry.proxy.visible = mesh.visible && entry.proxy.geometry.getAttribute('position').count > 0;
    }
  }
  obscured(camera: THREE.Vector3, anchor: THREE.Vector3) {
    const direction = anchor.clone().sub(camera); const distance = direction.length();
    if (distance <= .05) return false;
    this.ray.set(camera, direction.divideScalar(distance)); this.ray.near = 0; this.ray.far = distance - .05;
    return this.ray.intersectObjects([...this.entries.values()].filter(entry => entry.proxy.visible).map(entry => entry.proxy), false).length > 0;
  }
  dispose() {
    for (const entry of this.entries.values()) { entry.proxy.geometry.disposeBoundsTree(); entry.proxy.geometry.dispose(); }
    this.entries.clear(); this.scene.clear(); this.material.dispose();
  }
}
