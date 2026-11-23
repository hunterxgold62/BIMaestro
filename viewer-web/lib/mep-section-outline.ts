import { Matrix4, Mesh, Plane, Triangle, Vector3 } from 'three';
import { LineMaterial } from 'three/examples/jsm/lines/LineMaterial.js';
import { LineSegments2 } from 'three/examples/jsm/lines/LineSegments2.js';
import { LineSegmentsGeometry } from 'three/examples/jsm/lines/LineSegmentsGeometry.js';
import { isObjectVisible } from './mep-visibility';

const tolerance = 1e-6;

export function appendSectionOutline(mesh: Mesh, planes: readonly Plane[], positions: number[]) {
  const geometry = mesh.geometry;
  const vertices = geometry.getAttribute('position');
  if (!vertices?.count) return;
  if (!geometry.boundingBox) geometry.computeBoundingBox();
  if (!geometry.boundingBox) return;

  const inverse = new Matrix4().copy(mesh.matrixWorld).invert();
  const localPlanes = planes.map(plane => plane.clone().applyMatrix4(inverse));
  const triangle = new Triangle();
  const corners = [triangle.a, triangle.b, triangle.c];
  const distances = [0, 0, 0];
  const points = [new Vector3(), new Vector3(), new Vector3()];
  const direction = new Vector3();
  const start = new Vector3(), end = new Vector3();
  const index = geometry.getIndex();

  localPlanes.forEach((plane, section) => {
    if (!plane.intersectsBox(geometry.boundingBox!)) return;

    const intersect = (source: Triangle) => {
      triangle.copy(source);
      for (let i = 0; i < 3; i++) {
        const distance = plane.distanceToPoint(corners[i]);
        distances[i] = Math.abs(distance) <= tolerance ? 0 : distance;
      }
      // A coplanar face has no cut edges. A triangle on the retained side
      // still contributes an edge when two of its vertices lie on the plane.
      if (Math.max(...distances) <= 0 || Math.min(...distances) > 0) return false;

      let count = 0;
      for (let i = 0; i < 3; i++) {
        const j = (i + 1) % 3;
        const a = distances[i], b = distances[j];
        if (a === 0) points[count++].copy(corners[i]);
        else if (a * b < 0) points[count++].copy(corners[i]).lerp(corners[j], a / (a - b));
      }
      if (count !== 2) return false;

      const [from, to] = points;
      let first = 0, last = 1;
      for (let i = 0; i < localPlanes.length; i++) {
        if (i === section) continue;
        const a = localPlanes[i].distanceToPoint(from), b = localPlanes[i].distanceToPoint(to);
        if (a < -tolerance && b < -tolerance) return false;
        if (a < -tolerance) first = Math.max(first, a / (a - b));
        if (b < -tolerance) last = Math.min(last, a / (a - b));
      }
      if (last <= first) return false;

      direction.subVectors(to, from);
      start.copy(from).addScaledVector(direction, first).applyMatrix4(mesh.matrixWorld);
      end.copy(from).addScaledVector(direction, last).applyMatrix4(mesh.matrixWorld);
      if (start.distanceToSquared(end) > tolerance ** 2) positions.push(start.x, start.y, start.z, end.x, end.y, end.z);
      return false;
    };

    if (geometry.boundsTree) {
      geometry.boundsTree.shapecast({
        intersectsBounds: box => plane.intersectsBox(box),
        intersectsTriangle: intersect,
      });
    } else {
      const count = index?.count ?? vertices.count;
      for (let i = 0; i + 2 < count; i += 3) {
        for (let j = 0; j < 3; j++) corners[j].fromBufferAttribute(vertices, index ? index.getX(i + j) : i + j);
        intersect(triangle);
      }
    }
  });
}

export class SectionOutline extends LineSegments2 {
  private planes: readonly Plane[] | undefined;
  private meshes = new Map<Mesh, { geometry: Mesh['geometry']; matrix: Matrix4 }>();
  private lastUpdate = -Infinity;

  constructor() {
    super(new LineSegmentsGeometry(), new LineMaterial({
      color: '#47f582',
      linewidth: 2,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -2,
      toneMapped: false,
    }));
    this.visible = false;
  }

  update(objects: readonly unknown[], planes: readonly Plane[], now: number) {
    if (!planes.length) {
      this.visible = false;
      this.planes = undefined;
      this.meshes.clear();
      return;
    }
    if (now - this.lastUpdate < 32) return;
    this.lastUpdate = now;

    let changed = this.planes !== planes;
    const visibleMeshes: Mesh[] = [];
    for (const mesh of objects) {
      if (!(mesh instanceof Mesh) || !isObjectVisible(mesh)) continue;
      mesh.updateWorldMatrix(true, false);
      visibleMeshes.push(mesh);
      const previous = this.meshes.get(mesh);
      changed ||= !previous || previous.geometry !== mesh.geometry || !previous.matrix.equals(mesh.matrixWorld);
    }
    if (!changed && visibleMeshes.length === this.meshes.size) return;

    this.planes = planes;
    this.meshes.clear();
    const positions: number[] = [];
    for (const mesh of visibleMeshes) {
      this.meshes.set(mesh, { geometry: mesh.geometry, matrix: mesh.matrixWorld.clone() });
      appendSectionOutline(mesh, planes, positions);
    }

    this.visible = positions.length > 0;
    this.geometry.dispose();
    this.geometry = new LineSegmentsGeometry();
    if (this.visible) this.geometry.setPositions(positions);
  }

  dispose() {
    this.geometry.dispose();
    this.material.dispose();
    this.meshes.clear();
  }
}
