import type { WebProperty } from './mep-contract';
import type { Markup } from './mep-markup';

export function anchorMarkup(mark: Markup, property: WebProperty, revision: number): Markup {
  return { ...mark, modelRevision: revision, elementKey: property.key, elementName: property.name,
    stableKey: property.stableKey, anchorCenter: property.center, anchorSize: property.size, needsReview: false };
}

export function reconcileMarkups(marks: Markup[], properties: WebProperty[], revision: number): Markup[] {
  const offsets = marks.flatMap(mark => {
    if (!mark.anchorCenter || mark.modelRevision === revision) return [];
    const matches = properties.filter(p => mark.stableKey ? p.stableKey === mark.stableKey : p.key === mark.elementKey);
    return matches.length === 1 && matches[0].center ? [matches[0].center.map((n, i) => n - mark.anchorCenter![i])] : [];
  });
  // Only infer a model-wide offset when several independent anchors agree.
  const shift = offsets.length >= 3 && offsets.every(offset => offset.every((n, i) => Math.abs(n - offsets[0][i]) < .03)) ? offsets[0] : null;
  return marks.map(mark => {
    if (mark.modelRevision === revision) return mark;
    let matches = properties.filter(p => mark.stableKey ? p.stableKey === mark.stableKey : p.key === mark.elementKey);
    let replacement = false;
    if (!matches.length && shift && mark.anchorCenter && mark.anchorSize) {
      matches = properties.filter(p => p.name === mark.elementName && p.center && p.size &&
        p.center.every((n, i) => Math.abs(n - mark.anchorCenter![i] - shift[i]) < .03) &&
        p.size.every((n, i) => Math.abs(n - mark.anchorSize![i]) < .03));
      replacement = matches.length === 1;
    }
    if (matches.length !== 1) return { ...mark, needsReview: true };
    const property = matches[0];
    if (!mark.anchorCenter || !property.center || !mark.anchorSize || !property.size)
      return { ...mark, needsReview: true };
    const position = mark.position.map((n, i) => n + property.center![i] - mark.anchorCenter![i]) as Markup['position'];
    // Changes in extent can indicate resizing or rotation. Keep the old mark for manual relocation.
    const dimensions = mark.dimensions?.map(dimension => ({ ...dimension,
      point: dimension.point.map((n, i) => n + property.center![i] - mark.anchorCenter![i]) as Markup['position'] }));
    // Preview the recovered host position; confirmation is required for changed
    // geometry or reference faces whose historical placement is unavailable.
    if (replacement || mark.dimensions?.length || property.size.some((n, i) => Math.abs(n - mark.anchorSize![i]) > .03))
      return { ...mark, position, dimensions, elementKey: property.key, elementName: property.name, needsReview: true, reviewCandidate: true };
    return anchorMarkup({ ...mark, position }, property, revision);
  });
}
