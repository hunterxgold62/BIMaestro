import * as THREE from 'three';
import { isObjectVisible } from './mep-visibility';

const range = 30 / .3048;
type Segment = [number, number, number, number];

// Reuse the collision BVH to slice nearby geometry without a second 3D render.
export class MepMiniMap {
  private segments: Segment[] = [];
  private lastPlan = -Infinity;
  private lastDraw = -Infinity;
  draw(canvas: HTMLCanvasElement, camera: THREE.Camera, objects: THREE.Object3D[], now: number, altitude: number) {
    if (now - this.lastDraw < 200) return;
    this.lastDraw = now;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const center = camera.position;
    if (now - this.lastPlan >= 1000) {
      this.lastPlan = now;
      this.segments = [];
      const region = new THREE.Box3(
        new THREE.Vector3(center.x - range / 2, altitude - .02, center.z - range / 2),
        new THREE.Vector3(center.x + range / 2, altitude + .02, center.z + range / 2));
      const worldBox = new THREE.Box3();
      const triangle = new THREE.Triangle();
      const points = [new THREE.Vector3(), new THREE.Vector3()];
      for (const object of objects) {
        if (!(object instanceof THREE.Mesh) || !isObjectVisible(object)) continue;
        const mesh = object;
        mesh.updateWorldMatrix(true, false);
        const geometry = mesh.geometry;
        if (!geometry.boundingBox) geometry.computeBoundingBox();
        if (!geometry.boundingBox || !worldBox.copy(geometry.boundingBox).applyMatrix4(mesh.matrixWorld).intersectsBox(region)) continue;
        const slice = (source: THREE.Triangle) => {
          triangle.copy(source);
          triangle.a.applyMatrix4(mesh.matrixWorld); triangle.b.applyMatrix4(mesh.matrixWorld); triangle.c.applyMatrix4(mesh.matrixWorld);
          const vertices = [triangle.a, triangle.b, triangle.c];
          let count = 0;
          for (let i = 0; i < 3 && count < 2; i++) {
            const a = vertices[i], b = vertices[(i + 1) % 3];
            if ((a.y <= altitude && b.y > altitude) || (b.y <= altitude && a.y > altitude)) {
              points[count++].copy(a).lerp(b, (altitude - a.y) / (b.y - a.y));
            }
          }
          if (count === 2) this.segments.push([points[0].x, points[0].z, points[1].x, points[1].z]);
          return this.segments.length >= 20000;
        };
        if (geometry.boundsTree) geometry.boundsTree.shapecast({
          intersectsBounds: (box: THREE.Box3) => worldBox.copy(box).applyMatrix4(mesh.matrixWorld).intersectsBox(region),
          intersectsTriangle: slice,
        });
        else {
          const positions = geometry.getAttribute('position'), indices = geometry.index;
          if (!positions) continue;
          const count = indices?.count ?? positions.count;
          for (let i = 0; i + 2 < count; i += 3) {
            triangle.a.fromBufferAttribute(positions, indices ? indices.getX(i) : i);
            triangle.b.fromBufferAttribute(positions, indices ? indices.getX(i + 1) : i + 1);
            triangle.c.fromBufferAttribute(positions, indices ? indices.getX(i + 2) : i + 2);
            if (slice(triangle)) break;
          }
        }
        if (this.segments.length >= 20000) break;
      }
    }
    const size = canvas.width, scale = (size - 24) / range;
    const x = (value: number) => size / 2 + (value - center.x) * scale;
    const y = (value: number) => size / 2 + (value - center.z) * scale;
    ctx.clearRect(0, 0, size, size);
    ctx.fillStyle = '#101e18'; ctx.fillRect(0, 0, size, size);
    ctx.save(); ctx.beginPath(); ctx.rect(12, 12, size - 24, size - 24); ctx.clip();
    ctx.strokeStyle = '#789e90'; ctx.lineWidth = 1;
    ctx.beginPath();
    for (const line of this.segments) { ctx.moveTo(x(line[0]), y(line[1])); ctx.lineTo(x(line[2]), y(line[3])); }
    ctx.stroke(); ctx.restore();
    const direction = camera.getWorldDirection(new THREE.Vector3());
    const angle = Math.atan2(direction.z, direction.x);
    ctx.save(); ctx.translate(size / 2, size / 2); ctx.rotate(angle);
    ctx.beginPath(); ctx.moveTo(12, 0); ctx.lineTo(-6, 6); ctx.lineTo(-6, -6); ctx.closePath();
    ctx.fillStyle = '#f59e0b'; ctx.strokeStyle = '#fff'; ctx.fill(); ctx.stroke(); ctx.restore();
    ctx.fillStyle = '#dcece4'; ctx.font = '12px Segoe UI'; ctx.fillText('Y ↑', 12, 22);
    ctx.fillText('30 m · zone chargée', 12, size - 8);
  }
}
