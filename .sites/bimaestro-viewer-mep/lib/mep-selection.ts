import * as THREE from 'three';

// Share vertex buffers with the model; only indices for the selected element are copied.
export class ElementSelection {
  readonly group = new THREE.Group();
  readonly bounds = new THREE.Box3();
  private entries: { source: THREE.Mesh; geometry: THREE.BufferGeometry; overlay: THREE.Mesh }[] = [];
  private material = new THREE.MeshBasicMaterial({ color: '#00baff', transparent: true, opacity: .48, depthWrite: false, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 });
  private ids = new Set<number>();
  private meshes: THREE.Mesh[] = [];
  set(meshes: THREE.Mesh[], ids: Set<number>) {
    this.clear(); this.ids = ids; this.meshes = meshes; this.bounds.makeEmpty();
    if (!ids.size) return;
    const point = new THREE.Vector3();
    for (const source of meshes) {
      let parent: THREE.Object3D | null = source;
      while (parent && !(parent.userData.selectionElements instanceof Set)) parent = parent.parent;
      if (parent && ![...ids].some(id => parent!.userData.selectionElements.has(id))) continue;
      const geometry = source.geometry, attribute = geometry.getAttribute('_element') || geometry.getAttribute('_ELEMENT');
      if (!attribute) continue;
      const indices: number[] = [], position = geometry.getAttribute('position');
      source.updateWorldMatrix(true, false);
      for (let i = 0, count = geometry.index?.count ?? position.count; i < count; i += 3) {
        const a = geometry.index ? geometry.index.getX(i) : i;
        if (!ids.has(Math.round(attribute.getX(a)))) continue;
        for (let j = 0; j < 3; j++) {
          const vertex = geometry.index ? geometry.index.getX(i + j) : i + j;
          indices.push(vertex); this.bounds.expandByPoint(point.fromBufferAttribute(position, vertex).applyMatrix4(source.matrixWorld));
        }
      }
      if (!indices.length) continue;
      const subset = new THREE.BufferGeometry(); subset.setAttribute('position', position); subset.setIndex(indices);
      const overlay = new THREE.Mesh(subset, this.material); overlay.matrixAutoUpdate = false; overlay.renderOrder = 5;
      this.group.add(overlay); this.entries.push({ source, geometry, overlay });
    }
    this.sync();
  }
  sync() {
    if (this.entries.some(entry => entry.geometry !== entry.source.geometry)) { this.set(this.meshes, this.ids); return; }
    for (const entry of this.entries) { entry.source.updateWorldMatrix(true, false); entry.overlay.matrix.copy(entry.source.matrixWorld); entry.overlay.visible = entry.source.visible; }
  }
  private clear() { this.entries.forEach(entry => entry.overlay.geometry.dispose()); this.entries = []; this.group.clear(); }
  dispose() { this.clear(); this.material.dispose(); }
}

export function zoomStep(distance: number, delta: number, deltaMode = 0) {
  const pixels = delta * (deltaMode === 1 ? 16 : deltaMode === 2 ? 800 : 1);
  return Math.max(.2, distance) * (1 - Math.exp(THREE.MathUtils.clamp(pixels, -500, 500) * .0025));
}
