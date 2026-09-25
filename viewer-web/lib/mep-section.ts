import { Box3, Plane, Vector3 } from 'three';

export type SectionSettings = { enabled: boolean; axis: 'x' | 'y' | 'z'; position: number; inverted: boolean };
export const defaultSection: SectionSettings = { enabled: false, axis: 'y', position: 50, inverted: false };
export type FaceSection = { id: string; name: string; enabled: boolean; normal: [number, number, number]; anchor: [number, number, number]; offset: number; inverted: boolean };
export const maximumSections = 8;

export function sectionFromFace(id: string, name: string, point: Vector3, outwardNormal: Vector3): FaceSection {
  const normal = outwardNormal.clone().normalize().negate();
  return { id, name, enabled: true, normal: normal.toArray(), anchor: point.toArray(), offset: 0, inverted: false };
}

export function faceSectionPlane(section: FaceSection): Plane {
  const normal = new Vector3(...section.normal).normalize();
  const plane = new Plane(normal, -normal.dot(new Vector3(...section.anchor)) - section.offset);
  return section.inverted ? plane.negate() : plane;
}

export function sectionPlanes(sections: FaceSection[]): Plane[] {
  return sections.filter(section => section.enabled).slice(0, maximumSections).map(faceSectionPlane);
}

/** Positive signed distance is visible, including on the plane. Bounds are in viewer coordinates. */
export function sectionPlane(settings: SectionSettings, bounds: Box3): Plane | null {
  if (!settings.enabled || bounds.isEmpty()) return null;
  const min = bounds.min[settings.axis], max = bounds.max[settings.axis];
  if (![min, max, settings.position].every(Number.isFinite)) return null;
  const offset = min + (max - min) * Math.min(100, Math.max(0, settings.position)) / 100;
  const sign = settings.inverted ? 1 : -1;
  const normal = new Vector3(); normal[settings.axis] = sign;
  return new Plane(normal, -sign * offset);
}

export function sectionContains(plane: Plane | Plane[] | null, point: Vector3): boolean {
  return !plane || (Array.isArray(plane) ? plane.every(item => sectionContains(item, point)) : plane.distanceToPoint(point) >= -1e-6);
}
