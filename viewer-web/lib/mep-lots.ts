import * as THREE from 'three';
import { lotColor, reservationLot, type Markup } from './mep-markup';

export type ReservationLot = { name: string; color: string };
export type ReservationLots = Record<string, ReservationLot>;
export type LotOption = ReservationLot & { id: string };
export function lotDetails(id: string, settings: ReservationLots = {}): ReservationLot {
  return Object.hasOwn(settings, id) ? settings[id] : { name: id, color: '#' + new THREE.Color(lotColor(id)).getHexString() };
}
export function reservationLots(marks: Markup[], settings: ReservationLots = {}): LotOption[] {
  const ids = new Set(['MEP', 'GC', 'ELEC', ...Object.keys(settings), ...marks.map(reservationLot)]);
  return [...ids].map(id => ({ id, ...lotDetails(id, settings) })).sort((a, b) => a.name.localeCompare(b.name, 'fr'));
}
