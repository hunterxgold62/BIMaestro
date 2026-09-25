import * as THREE from 'three';
export function decodeMepPoint(value: unknown): [number, number, number] | null {
  let coordinates: unknown[];
  if (typeof value === 'string') coordinates = value.split(',').map((part) => Number(part.trim()));
  else if (Array.isArray(value)) coordinates = value;
  else if (value && typeof value === 'object') {
    const point = value as Record<string, unknown>;
    coordinates = [point.x ?? point.X, point.y ?? point.Y, point.z ?? point.Z];
  } else return null;
  if (coordinates.length < 3) return null;
  const result = coordinates.slice(0, 3).map(Number);
  return result.every(Number.isFinite) ? result as [number, number, number] : null;
}

export function toWebPoint(value: unknown): THREE.Vector3 | null {
  const point = decodeMepPoint(value);
  return point ? new THREE.Vector3(point[0], point[2], -point[1]) : null;
}
