import { describe, expect, it } from 'vitest';
import { decodeMepPoint, toWebPoint } from './mep-point';

describe('decodeMepPoint', () => {
  it('lit le format texte produit par Revit/Newtonsoft', () => {
    expect(decodeMepPoint('-40.2,5.63,20.45')).toEqual([-40.2, 5.63, 20.45]);
    expect(toWebPoint('-40.2,5.63,20.45')?.toArray()).toEqual([-40.2, 20.45, -5.63]);
  });

  it('reste compatible avec les objets et tableaux futurs', () => {
    expect(decodeMepPoint({ x: 1, y: 2, z: 3 })).toEqual([1, 2, 3]);
    expect(decodeMepPoint({ X: 1, Y: 2, Z: 3 })).toEqual([1, 2, 3]);
    expect(decodeMepPoint([1, 2, 3])).toEqual([1, 2, 3]);
  });

  it('rejette une coordonnée incomplète ou invalide', () => {
    expect(decodeMepPoint('1,2')).toBeNull();
    expect(decodeMepPoint('1,non,3')).toBeNull();
  });
});
