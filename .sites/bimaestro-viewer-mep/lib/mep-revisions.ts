import type { WebProperty } from './mep-contract';
import type { Markup } from './mep-markup';

export function anchorMarkup(mark: Markup, property: WebProperty, revision: number): Markup {
  return { ...mark, modelRevision: revision, elementKey: property.key, elementName: property.name,
    stableKey: property.stableKey, anchorCenter: property.center, anchorSize: property.size, needsReview: false };
}

export function reconcileMarkups(marks: Markup[], properties: WebProperty[], revision: number): Markup[] {
  return marks.map(mark => {
    if (mark.modelRevision === revision) return mark;
    const matches = properties.filter(p => mark.stableKey ? p.stableKey === mark.stableKey : p.key === mark.elementKey);
    if (matches.length !== 1) return { ...mark, needsReview: true };
    const property = matches[0];
    if (!mark.anchorCenter || !property.center || !mark.anchorSize || !property.size)
      return { ...mark, needsReview: true };
    const position = mark.position.map((n, i) => n + property.center![i] - mark.anchorCenter![i]) as Markup['position'];
    // Changes in extent can indicate resizing or rotation. Keep the old mark for manual relocation.
    if (property.size.some((n, i) => Math.abs(n - mark.anchorSize![i]) > .03)) return { ...mark, needsReview: true };
    return anchorMarkup({ ...mark, position }, property, revision);
  });
}
