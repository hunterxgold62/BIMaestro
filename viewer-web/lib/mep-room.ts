import type { ViewerRoom } from './mep-contract';

// Viewer coordinates are Y-up, in feet. Even/odd handles holes and disjoint regions.
export function roomContains(room: ViewerRoom, x: number, z: number): boolean {
  let inside = false;
  for (const loop of room.loops) {
    for (let i = 0, j = loop.length - 1; i < loop.length; j = i++) {
      const a = loop[i], b = loop[j];
      if ((a[2] > z) !== (b[2] > z) && x < (b[0] - a[0]) * (z - a[2]) / (b[2] - a[2]) + a[0]) inside = !inside;
    }
  }
  return inside;
}

export type RoomLocation = { room: ViewerRoom; approximate: boolean };
export function locateRoom(rooms: ViewerRoom[], x: number, altitude: number, z: number): RoomLocation | null {
  const candidates = rooms.filter(room => altitude >= room.baseElevation - .35 && altitude < room.navigationTop && roomContains(room, x, z));
  // A room and its associated MEP space may overlap. Prefer the architectural room.
  const preferred = candidates.some(room => room.kind === 'room') ? candidates.filter(room => room.kind === 'room') : candidates;
  if (preferred.length !== 1) return null; // Multiple phases/overlapping footprints: do not guess.
  const room = preferred[0];
  return { room, approximate: altitude > room.declaredTop };
}
